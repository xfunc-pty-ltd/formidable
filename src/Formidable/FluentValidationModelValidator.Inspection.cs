using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.RegularExpressions;
using FluentValidation;
using FluentValidation.Internal;
using FluentValidation.Validators;

namespace Formidable;

// Answers what a profile requires of a field, and which fields its rules mention at
// all, without a model to run the rules against. A top-level rule's own components
// answer directly; a rule reaching a child validator hands the walk to that
// validator's own rules, under the selector FluentValidation itself would hand the
// child at that boundary, so the walk agrees with a real run about which child rules
// a profile reaches.
public sealed partial class FluentValidationModelValidator<TModel>
{
    // Keyed by component reference, since a component is one declaration on one rule and
    // this validator's rules are fixed once it is constructed. Bounded by the number of
    // child-validator components the wrapped validator declares, which is a property of the
    // code rather than of anything a request carries.
    private readonly ConcurrentDictionary<IRuleComponent, ChildValidatorReader.ChildReading> _childReadings = new();

    private DeclaredSnapshot? _declared;

    /// <summary>Whether the rules can be read: <see langword="true"/> exactly when the wrapped validator is an <see cref="AbstractValidator{T}"/> and the loaded FluentValidation assembly carries the members the reading needs.</summary>
    /// <remarks>
    /// Cascade mode does not matter, so a validator <see cref="CanValidateByRule"/> refuses can
    /// still be read. A hand-rolled <see cref="IValidator{T}"/> reports <see langword="false"/>.
    /// The assembly check runs once per process and writes one Trace line when it fails, which
    /// a FluentValidation resolved above the range this package declares can cause; both readers
    /// then report the empty answer.
    /// </remarks>
    public bool CanInspectRules =>
        _validator is AbstractValidator<TModel> && FluentValidationInspectionSurface.Intact;

    /// <summary>Reports whether the rules <paramref name="profile"/> selects demand a value at <paramref name="fieldPath"/>: required, conditionally required, or not required.</summary>
    /// <param name="fieldPath">A declared path (<c>Title</c>, <c>Attendees[].Name</c>) or an indexed form of one (<c>Attendees[0].Name</c>); any other string reads <see cref="FieldRequirement.NotRequired"/>.</param>
    /// <param name="profile">The rule selection that decides the answer.</param>
    /// <returns>The demand; <see cref="FieldRequirement.NotRequired"/> when <see cref="CanInspectRules"/> is <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fieldPath"/> or <paramref name="profile"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="profile"/> names a ruleset a <see cref="ProfiledValidator{T}"/> never registered.</exception>
    public FieldRequirement GetFieldRequirement(string fieldPath, ValidationProfile profile)
    {
        ArgumentNullException.ThrowIfNull(fieldPath);
        ArgumentNullException.ThrowIfNull(profile);

        var declared = DeclaredFields(profile);
        if (declared.TryGetValue(fieldPath, out var requirement))
        {
            return requirement;
        }

        // An indexed path (Attendees[0].Name) is answered by the template that declares its
        // rule (Attendees[].Name). Exact match first, normalised second — the order
        // FluentValidation's own member-name matching tries them in — so a rule whose
        // overridden property name literally carries brackets is answered as declared before
        // any rewriting is tried. The bracket test just skips the rewrite where it could only
        // reproduce the miss above.
        if (fieldPath.Contains('[')
            && declared.TryGetValue(CollectionIndexNormalizer().Replace(fieldPath, "[]"), out var templated))
        {
            return templated;
        }

        return FieldRequirement.NotRequired;
    }

    /// <summary>FluentValidation's collection-index pattern, mirrored because its own is private; an index becomes <c>[]</c> before a path is matched to a template.</summary>
    /// <returns>The pattern <c>MemberNameValidatorSelector.CollectionIndexNormalizer</c> declares.</returns>
    // The replacement applied to it, "[]", is the one that selector's CanExecute applies before
    // comparing a property path against wildcard member names.
    // The library reads bracket segments in one other place, Introspection.PropertyPath, which
    // the introspector parses concrete paths through on the way to an owner instance. The two
    // agree on what an index is (a bracketed span ending at the first ']', the parser by scanning
    // to it and this pattern by its lazy quantifier) and divide by question rather than by
    // disputed authority: matching a path against declared templates is FluentValidation's own
    // question, so its pattern is authoritative here; resolving a path to the object owning its
    // final member is a question FluentValidation never answers, so the parser is authoritative
    // there. The parser never meets a template, because it rejects the empty [] span, the one
    // segment shape only templates carry: a template must be expanded against a live model
    // before anything can resolve it.
    [GeneratedRegex(@"\[.*?\]")]
    private static partial Regex CollectionIndexNormalizer();

    /// <summary>Returns every field path the rules <paramref name="profile"/> selects mention, as declared templates with each collection index left open (<c>Attendees[].Name</c>).</summary>
    /// <param name="profile">The rule selection that decides which rules are read.</param>
    /// <returns>The declared paths, compared ordinally; empty when <see cref="CanInspectRules"/> is <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="profile"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="profile"/> names a ruleset a <see cref="ProfiledValidator{T}"/> never registered.</exception>
    /// <remarks>
    /// A child validator written for a base type (an <c>AnimalValidator</c> on a <c>Dog</c>) is read
    /// like any other. A rule on each row of a collection property, written with <c>RuleForEach</c>
    /// or <c>ForEach</c>, files under the indexed path (<c>Tags[]</c>, or <c>Items[].Sku</c> for
    /// a child validator on each row), never under the collection's own path. Not read: a child
    /// supplied by a lambda that reads its model, the repeat of a validator that includes itself,
    /// and <c>DependentRules</c>.
    /// </remarks>
    public IReadOnlySet<string> GetDeclaredFieldPaths(ValidationProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return new HashSet<string>(DeclaredFields(profile).Keys, StringComparer.Ordinal);
    }

    /// <summary>The one walk both readers answer from: every declared path, mapped to the presence demand the profile's rules make of it.</summary>
    /// <param name="profile">The rule selection that decides which rules are read.</param>
    /// <returns>The map; a path whose rules demand no value (no presence component, or only ones failing as a warning or info) is present as <see cref="FieldRequirement.NotRequired"/>.</returns>
    // A path whose rules demand no value stays in the map because the two readers ask different
    // questions, "is this field spoken about" and "is it demanded", and only a map that keeps both
    // can answer them consistently.
    private Dictionary<string, FieldRequirement> DeclaredFields(ValidationProfile profile)
    {
        // One profile deep, and keyed by REFERENCE. Both readers are asked repeatedly for the
        // same profile - a form builds its whole requirement map by asking once per declared
        // path - and a walk allocates a dictionary and resolves every child validator it meets.
        // One entry covers that pattern exactly and cannot grow, which a per-profile map could
        // if a caller built a fresh profile per ask. Reference rather than equality as the
        // check: of what a caller passes, the walk reads the profile's shape alone, so value-equal
        // profiles would earn identical answers and a reference miss only ever recomputes an
        // answer - it can never serve a wrong one. The walk's one other input is FluentValidation's
        // global default severity, read on each walk, so a snapshot keeps the default its walk
        // read; the default is meant to be set once at startup.
        if (_declared is { } snapshot && ReferenceEquals(snapshot.Profile, profile))
        {
            return snapshot.Fields;
        }

        var declared = new Dictionary<string, FieldRequirement>(StringComparer.Ordinal);
        if (_validator is not AbstractValidator<TModel> abstractValidator)
        {
            return declared;
        }

        // Ahead of the store, so a profile naming a ruleset that was never registered throws on
        // every ask rather than only on the first - nothing unverified is ever remembered.
        VerifyRuleSets(profile);

        // CanInspectRules gates on the surface check as well as on the validator's shape, and
        // the readers promise an empty answer whenever it is false - so a walk whose by-name
        // reads would quietly degrade is never taken. After the ruleset verification on
        // purpose: a typo'd profile name fails the same way whatever the surface check found.
        if (!FluentValidationInspectionSurface.Intact)
        {
            return declared;
        }

        WalkDeclaredRules(
            abstractValidator,
            typeof(TModel),
            string.Empty,
            conditional: false,
            ProfileRuleSelection.BuildProfileSelector(profile),
            ProfileRuleSelection.CreateSelectionContext<TModel>(),
            declared,
            new HashSet<object>(ReferenceEqualityComparer.Instance));

        // A reference assignment, so a reader sees one snapshot or the other and never a mix.
        // Two callers racing both walk and one wins: wasteful once, never wrong.
        _declared = new DeclaredSnapshot(profile, declared);
        return declared;
    }

    /// <summary>One profile's declared fields, held with the profile so the pair is never read torn.</summary>
    /// <param name="Profile">The profile instance the fields were read for.</param>
    /// <param name="Fields">The declared paths and their demands.</param>
    private sealed record DeclaredSnapshot(ValidationProfile Profile, Dictionary<string, FieldRequirement> Fields);

    /// <summary>Files each selected leaf component of one validator under the path its failures carry, descending into every child validator it can resolve.</summary>
    /// <param name="validator">The validator whose rules are read; one that enumerates no rules contributes nothing.</param>
    /// <param name="modelType">The type this validator's rules are written for.</param>
    /// <param name="prefix">The path travelled so far, empty or ending in a dot; a rule reaching a child adds <c>Name.</c>, or <c>Name[].</c> for a collection rule, a collection rule with no property name adds <c>[]</c> to the path travelled, and any other rule with no property name adds nothing.</param>
    /// <param name="conditional">Whether the walk reached this validator only through a condition.</param>
    /// <param name="selector">The selector rules are admitted by at this level.</param>
    /// <param name="selectionContext">The model-less context selection questions are asked against.</param>
    /// <param name="declared">The map being filled.</param>
    /// <param name="walking">The validators on the current path, so a validator that reaches itself is read once.</param>
    /// <remarks>
    /// A child adaptor carrying rulesets replaces the selector its child is read under, as
    /// FluentValidation's own dispatch does, and one carrying none hands its child the selector
    /// in force. A validator reached twice down two branches is read twice; one that reaches
    /// itself is read once, which bounds a recursive validator without a depth cap. A child
    /// reached only through a conditional rule is conditionally demanded, however
    /// unconditionally the child declares it.
    /// </remarks>
    private void WalkDeclaredRules(
        object validator,
        Type modelType,
        string prefix,
        bool conditional,
        IValidatorSelector selector,
        IValidationContext selectionContext,
        Dictionary<string, FieldRequirement> declared,
        HashSet<object> walking)
    {
        if (validator is not IEnumerable<IValidationRule> rules || !walking.Add(validator))
        {
            return;
        }

        foreach (var rule in rules)
        {
            if (!selector.CanExecute(rule, string.Empty, selectionContext))
            {
                continue;
            }

            // Read once per rule: both the row-filter check below and the paths the rule files
            // under ask the identical question of the identical rule, and neither needs it more
            // than once.
            var collectionRuleType = CollectionRuleTypeOf(rule, modelType);
            var (ownPath, childPrefix) = RulePaths(prefix, rule.PropertyName, collectionRuleType is not null);

            // FluentValidation records a condition in one of two places depending on how it was
            // written: a When block wrapping the rule declaration marks the RULE, while a When
            // chained after a component marks the COMPONENT — including the
            // ApplyConditionTo.CurrentValidator form, which marks that one component alone and
            // leaves its siblings unconditional. Both places carry a synchronous and an
            // asynchronous flag, so all four are consulted. A collection rule can carry one
            // more, judged per row rather than per model: RuleForEach(...).Where and its
            // asynchronous twin, stored as the rule's Filter/AsyncFilter rather than as
            // condition flags. Which rows a filter admits is as unanswerable without a model
            // as any When, so a filtered rule is conditional the same way.
            var ruleConditional = conditional
                || rule.HasCondition
                || rule.HasAsyncCondition
                || HasRowFilter(rule, collectionRuleType);

            foreach (var component in rule.Components)
            {
                var componentConditional =
                    ruleConditional || component.HasCondition || component.HasAsyncCondition;

                if (component.Validator is IChildValidatorAdaptor)
                {
                    var child = ResolveChildValidator(component, modelType, rule.TypeToValidate);
                    if (child.Validator is null)
                    {
                        continue;
                    }

                    // FluentValidation's own child dispatch (ChildValidatorAdaptor.GetSelector):
                    // rulesets on the adaptor build a replacing selector — the one in force is
                    // not intersected with, and never sees the child's rules — while an adaptor
                    // carrying none inherits it unchanged. Constructed directly because
                    // GetSelector constructs directly, without consulting the global selector
                    // factory; routing this through the factory would diverge from what a run
                    // does at this boundary.
                    var childSelector = child.RuleSets is { Length: > 0 }
                        ? new RulesetValidatorSelector(child.RuleSets)
                        : selector;

                    WalkDeclaredRules(
                        child.Validator, ValidatedModelTypeOf(child.Validator, rule.TypeToValidate),
                        childPrefix, componentConditional, childSelector, selectionContext, declared,
                        walking);
                    continue;
                }

                if (ownPath is null)
                {
                    continue; // a model-level component names no field
                }

                // A presence component demands a value only when its failure blocks a submit, and
                // only an error blocks one. So the severity grades it before the condition does: a
                // warning or info never demands anything, conditional or not; a severity decided
                // from the model is as undecidable here as a When, so it is conditional; an error
                // is required, or conditional under a condition.
                var demand = !IsPresenceComponent(component)
                    ? FieldRequirement.NotRequired
                    : ReadSeverity(component) switch
                    {
                        SeverityReading.NeverBlocks => FieldRequirement.NotRequired,
                        SeverityReading.NeedsModel => FieldRequirement.ConditionallyRequired,
                        _ => componentConditional
                            ? FieldRequirement.ConditionallyRequired
                            : FieldRequirement.Required,
                    };

                // The strongest demand any component makes wins, which is what puts an
                // unconditional presence rule ahead of a conditional one on the same field, and an
                // error presence rule ahead of a warning one, and keeps a field whose rules demand
                // no value in the map at all.
                declared[ownPath] = declared.TryGetValue(ownPath, out var existing) && existing > demand
                    ? existing
                    : demand;
            }
        }

        walking.Remove(validator);
    }

    /// <summary>The path a rule's own components file under, and the prefix a child validator it carries is read under.</summary>
    /// <param name="prefix">The path travelled so far, empty or ending in a dot.</param>
    /// <param name="name">The rule's property name; <see langword="null"/> or empty for a rule that names none.</param>
    /// <param name="isCollectionRule">Whether the rule judges each element of a collection.</param>
    /// <returns>The components' path, or <see langword="null"/> where they name no field, and the child prefix.</returns>
    // A named rule adds its name. A named collection rule adds Name[], for its own components
    // (Tags[0]) and for a child it carries (Items[0].Sku) alike, because every failure it reports
    // carries an index there; it demands nothing of the collection itself. A collection rule with
    // no name sits in a validator for the collection itself (ForEach builds one, and
    // RuleForEach(x => x) writes one), so the rule holding that validator has already added the
    // name and the index is all this rule adds, the same way. On the root model nothing has been
    // travelled, so there is no path to index and the rule files as any rule with no name does.
    private static (string? OwnPath, string ChildPrefix) RulePaths(string prefix, string? name, bool isCollectionRule)
    {
        if (!string.IsNullOrEmpty(name))
        {
            var own = isCollectionRule ? $"{prefix}{name}[]" : prefix + name;
            return (own, own + ".");
        }

        if (isCollectionRule && prefix.Length > 0)
        {
            var indexed = $"{prefix[..^1]}[]";
            return (indexed, indexed + ".");
        }

        return (null, prefix);
    }

    /// <summary>The type a validator's rules are written for: the argument of the <see cref="AbstractValidator{T}"/> it derives from, or <paramref name="fallback"/> when it derives from none.</summary>
    /// <param name="validator">The child validator.</param>
    /// <param name="fallback">The type to answer for a validator that derives from no <see cref="AbstractValidator{T}"/>.</param>
    /// <returns>The type the validator's rules judge.</returns>
    // A child validator can be written for a base type of what it validates (an
    // AnimalValidator on a Dog, a PersonValidator included into a CustomerValidator, the
    // IEnumerable<T> validator ForEach hands a List<T>), and its rules are typed on that base, so
    // the walk reads them under it. The base types are walked as ChildValidatorReader.ClosedTypeOf
    // walks them, each tested against a literal typeof and nothing built.
    private static Type ValidatedModelTypeOf(object validator, Type fallback)
    {
        for (var type = validator.GetType(); type is not null; type = type.BaseType)
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(AbstractValidator<>))
            {
                return type.GetGenericArguments()[0];
            }
        }

        return fallback;
    }

    /// <summary>The closed <c>ICollectionRule&lt;,&gt;</c> for the rule's model and element types, or <see langword="null"/> when the rule is not one.</summary>
    /// <param name="rule">The rule to classify.</param>
    /// <param name="modelType">The type the rule's validator judges.</param>
    /// <returns>The closed interface the rule implements, or <see langword="null"/>.</returns>
    // The one question behind both "does the walk descend under Name[] or under Name" and "can
    // this rule carry a row filter at all", asked once per rule rather than once per question.
    // The closed interface is built from a literal typeof so the trimmer keeps the interface both
    // callers' instance tests need. A model/element pair that cannot close the interface answers
    // null the same as a rule that is not one. MakeGenericType has no constraints to fail against
    // on this interface, and Native AOT hands back a type to test even for a pair it has no code
    // for, so both caught exceptions are defensive rather than load-bearing. The return carries a
    // PublicProperties annotation for HasRowFilter's sake: the trimmer already keeps the closed
    // interface's full member set once it is constructed from a literal typeof, but that
    // provenance does not survive crossing a method boundary as a plain Type without one.
    [return: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)]
    [UnconditionalSuppressMessage(
        "AOT",
        "IL3050",
        Justification = "The built type is only tested against a live rule, and read for two " +
            "properties when the test passes. A rule implementing the interface carries that " +
            "closed type in its own type, so the test meets the type the rule implements. For " +
            "any other pair nothing is instantiated from the type, and a NotSupportedException " +
            "from a runtime that will not build it answers null like a failed test.")]
    private static Type? CollectionRuleTypeOf(IValidationRule rule, Type modelType)
    {
        try
        {
            var collectionRuleType = typeof(ICollectionRule<,>)
                .MakeGenericType(modelType, rule.TypeToValidate);
            return collectionRuleType.IsInstanceOfType(rule) ? collectionRuleType : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Whether the collection rule filters its rows with <c>Where</c> or <c>WhereAsync</c>, read by name; an unreadable filter answers <see langword="false"/>.</summary>
    /// <param name="rule">The rule to read.</param>
    /// <param name="collectionRuleType">The rule's closed <c>ICollectionRule&lt;,&gt;</c>, or <see langword="null"/> for a rule that is not one.</param>
    /// <returns><see langword="true"/> only on a positive read of a non-null <c>Filter</c> or <c>AsyncFilter</c>.</returns>
    /// <remarks>
    /// A row filter is a per-row condition the reading has no rows to evaluate, so it makes the
    /// rule's demands conditional as a <c>When</c> on the rule does. A failed read costs the
    /// filtered shape its conditional grade, never an unfiltered rule its demand.
    /// </remarks>
    // The properties are found by name, which no typeof roots, so a build that trims them away
    // fails the read quietly rather than crashing; a demand the rules make of every row must not
    // weaken merely because a property could not be reached.
    private static bool HasRowFilter(
        IValidationRule rule,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] Type? collectionRuleType)
    {
        if (collectionRuleType is null)
        {
            return false;
        }

        try
        {
            return collectionRuleType.GetProperty(FluentValidationInspectionSurface.FilterProperty, BindingFlags.Public | BindingFlags.Instance)?.GetValue(rule) is not null
                || collectionRuleType.GetProperty(FluentValidationInspectionSurface.AsyncFilterProperty, BindingFlags.Public | BindingFlags.Instance)?.GetValue(rule) is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>The validator a child component wraps and the rulesets scoping it, remembered per component; the validator is <see langword="null"/> where it needs a model.</summary>
    /// <param name="component">The child-validator component to read.</param>
    /// <param name="modelType">The type the component's rule judges.</param>
    /// <param name="propertyType">The type the child validator judges.</param>
    /// <returns>The reading; <see cref="ChildValidatorReader.ChildReading.Unreadable"/> where the child cannot be had.</returns>
    /// <remarks>
    /// A child that cannot be had without a model contributes no paths. The read itself is
    /// <see cref="ChildValidatorReader.Read"/>.
    /// </remarks>
    // The walk carries the type each validator's rules are written for, which is what the read's
    // pair check holds the adaptor to.
    // Answers are remembered per component, including "cannot be had": a component is one
    // declaration on one rule of one validator, and this validator's rules are fixed once it is
    // constructed, so the child behind a component cannot change. That keeps a caller asking per
    // field (the required indicator does) from paying for a reflective resolve of every child in
    // the form on every ask.
    private ChildValidatorReader.ChildReading ResolveChildValidator(IRuleComponent component, Type modelType, Type propertyType)
    {
        if (_childReadings.TryGetValue(component, out var cached))
        {
            return cached;
        }

        var resolved = ChildValidatorReader.Read(component, modelType, propertyType);
        _childReadings[component] = resolved;
        return resolved;
    }

    /// <summary>Whether the component is a <c>NotEmpty()</c> or <c>NotNull()</c> validator, by the marker interface FluentValidation gives each.</summary>
    /// <param name="component">The component to classify.</param>
    /// <returns><see langword="true"/> for <see cref="INotEmptyValidator"/> or <see cref="INotNullValidator"/>; <c>Null()</c>, whose marker is <see cref="INullValidator"/>, is excluded.</returns>
    private static bool IsPresenceComponent(IRuleComponent component) =>
        component.Validator is INotEmptyValidator or INotNullValidator;

    /// <summary>What a presence component's severity says about blocking a submit, read without a model.</summary>
    private enum SeverityReading
    {
        /// <summary>The failure is an error, which blocks a submit; also the answer when the severity cannot be read.</summary>
        Blocks,

        /// <summary>The failure is a warning or info whatever the model holds, which never blocks a submit.</summary>
        NeverBlocks,

        /// <summary>The severity is decided per model, or by code this reading does not run.</summary>
        NeedsModel,
    }

    /// <summary>Reads the severity a presence component fails with, without a model and without running any code the rules' author wrote.</summary>
    /// <param name="component">The presence component to read.</param>
    /// <returns>Whether its failure blocks a submit, never does, or needs a model to say.</returns>
    /// <remarks>
    /// A component with no severity of its own fails with FluentValidation's global default, read
    /// here on each walk of the rules, so an answer already held keeps the default its walk read.
    /// </remarks>
    // The severity sits in RuleComponent<T, TProperty>.SeverityProvider, a delegate FluentValidation
    // calls with the context and the value when a failure is created. The surface check finds the
    // property once, on the open type named by a literal typeof, and the reading matches it to the
    // live component's closed type, as the child reading does with the adaptor's members.
    // A provider whose closure is FluentValidation's own is called with a null context. The
    // constant WithSeverity(Severity) closure ignores its arguments and answers. Each
    // model-reading WithSeverity overload stores a FluentValidation wrapper that reads the model
    // off the context before it calls the author's lambda, so the null context throws inside
    // FluentValidation first, and that throw is what says the severity needs a model. A provider
    // from anywhere else (assigned through Configure, or by an extension method) could run the
    // author's code with no model, so it is never called and reads as needing a model.
    // A failed read answers Blocks, an error's grade, because a failed read must never cost an
    // unconditional rule its demand (HasRowFilter's rule). A component of another type, a property
    // that cannot be read, and a call that fails before it reaches the closure are all failed
    // reads. Only a throw from inside the closure says the severity needs a model, and reflection
    // hands that one back wrapped in a TargetInvocationException.
    private static SeverityReading ReadSeverity(IRuleComponent component)
    {
        Delegate? provider;
        try
        {
            var closed = ChildValidatorReader.ClosedTypeOf(component, typeof(RuleComponent<,>));
            if (closed is null)
            {
                return SeverityReading.Blocks;
            }

            // The walk runs only while the surface is intact, so the member is there. Were that
            // ever not so, the dereference would throw into the catch below and read as Blocks.
            var property = (PropertyInfo)closed.GetMemberWithSameMetadataDefinitionAs(
                FluentValidationInspectionSurface.Members!.SeverityProvider);
            provider = property.GetValue(component) as Delegate;
        }
        catch (Exception)
        {
            return SeverityReading.Blocks;
        }

        if (provider is null)
        {
            return Classify(ValidatorOptions.Global.Severity);
        }

        if (provider.Target?.GetType().Assembly != typeof(AbstractValidator<>).Assembly)
        {
            return SeverityReading.NeedsModel;
        }

        object? answer;
        try
        {
            answer = provider.DynamicInvoke(null, null);
        }
        catch (TargetInvocationException)
        {
            return SeverityReading.NeedsModel;
        }
        catch (Exception)
        {
            return SeverityReading.Blocks;
        }

        return answer is Severity severity ? Classify(severity) : SeverityReading.Blocks;

        // The mapping the report applies to a failure, so the reading and a run agree on which
        // severities block.
        static SeverityReading Classify(Severity severity) =>
            MapSeverity(severity) == ValidationSeverity.Error
                ? SeverityReading.Blocks
                : SeverityReading.NeverBlocks;
    }
}
