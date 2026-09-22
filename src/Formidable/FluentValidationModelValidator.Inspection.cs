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
    private readonly ConcurrentDictionary<IRuleComponent, ChildReading> _childReadings = new();

    private DeclaredSnapshot? _declared;

    /// <inheritdoc />
    /// <remarks>
    /// True exactly when the wrapped validator is a FluentValidation
    /// <c>AbstractValidator&lt;TModel&gt;</c> AND the loaded FluentValidation assembly still
    /// carries the members the inspection walk reads by name
    /// (<c>ChildValidatorAdaptor&lt;,&gt;</c>'s <c>GetValidator</c>/<c>RuleSets</c>,
    /// <c>ICollectionRule&lt;,&gt;</c>'s <c>Filter</c>/<c>AsyncFilter</c>). Those reads
    /// degrade quietly rather than throwing, so under a FluentValidation resolved above the
    /// range this package declares they could turn "conditionally required" into a flat
    /// "required" with no signal; the member check — made once per process, writing one Trace
    /// line when it fails — turns that into the honest "cannot tell", and the readers then
    /// claim nothing. Cascade mode never matters: reading a rule's declared components asks
    /// nothing of execution order, so the class-level cascade stop that bars
    /// <see cref="CanValidateByRule"/> does not bar inspection. A hand-rolled
    /// <see cref="IValidator{T}"/> keeps its rules to itself and reports false.
    /// </remarks>
    public bool CanInspectRules =>
        _validator is AbstractValidator<TModel> && FluentValidationInspectionSurface.Intact;

    /// <inheritdoc />
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

    /// <summary>
    /// FluentValidation's collection-index normalisation, mirrored: the pattern is the one its
    /// <c>MemberNameValidatorSelector.CollectionIndexNormalizer</c> declares, and the
    /// replacement applied to it above — <c>"[]"</c> — is the one that selector's
    /// <c>CanExecute</c> applies before comparing a property path against wildcard member
    /// names. Mirrored rather than invoked because that member is private.
    /// </summary>
    /// <remarks>
    /// The library reads bracket segments in one other place:
    /// <see cref="Introspection.PropertyPath"/>, which
    /// <see cref="Introspection.ReflectionModelIntrospector"/> parses concrete paths through on
    /// the way to an owner instance. The two agree on what an index IS — a bracketed span
    /// ending at the first <c>]</c>, the parser by scanning to it and this pattern by its lazy
    /// quantifier — and they divide by question rather than by disputed authority. Matching a
    /// path against declared templates is FluentValidation's own question, so its pattern is
    /// authoritative here; resolving a path to the object owning its final member is a question
    /// FluentValidation never answers, so the parser is authoritative there — and it never
    /// meets a template, because it rejects the empty <c>[]</c> span, the one segment shape
    /// only templates carry: a template must be expanded against a live model before anything
    /// can resolve it.
    /// </remarks>
    [GeneratedRegex(@"\[.*?\]")]
    private static partial Regex CollectionIndexNormalizer();

    /// <inheritdoc />
    public IReadOnlySet<string> GetDeclaredFieldPaths(ValidationProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return new HashSet<string>(DeclaredFields(profile).Keys, StringComparer.Ordinal);
    }

    /// <summary>
    /// The one walk both inspection readers answer from: every field path the profile's rules
    /// declare, mapped to the presence demand those rules make of it. A path with rules but no
    /// presence component is present with <see cref="FieldRequirement.NotRequired"/> — the two
    /// questions the readers ask are "is this field spoken about" and "is it demanded", and
    /// only a map that keeps both can answer them consistently.
    /// </summary>
    private Dictionary<string, FieldRequirement> DeclaredFields(ValidationProfile profile)
    {
        // One profile deep, and keyed by REFERENCE. Both readers are asked repeatedly for the
        // same profile - a form builds its whole requirement map by asking once per declared
        // path - and a walk allocates a dictionary and resolves every child validator it meets.
        // One entry covers that pattern exactly and cannot grow, which a per-profile map could
        // if a caller built a fresh profile per ask. Reference rather than equality as the
        // check: the walk reads the profile's shape alone, so value-equal profiles would earn
        // identical answers and a reference miss only ever recomputes an answer - it can never
        // serve a wrong one.
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
            BuildProfileSelector(profile),
            CreateSelectionContext(),
            declared,
            new HashSet<object>(ReferenceEqualityComparer.Instance));

        // A reference assignment, so a reader sees one snapshot or the other and never a mix.
        // Two callers racing both walk and one wins: wasteful once, never wrong.
        _declared = new DeclaredSnapshot(profile, declared);
        return declared;
    }

    /// <summary>One profile's declared fields, held together so the pair cannot be read torn.</summary>
    private sealed record DeclaredSnapshot(ValidationProfile Profile, Dictionary<string, FieldRequirement> Fields);

    /// <summary>
    /// Walks one validator's selected rules, filing each leaf component under the path its
    /// failures will carry and descending through every child validator it can resolve.
    /// <paramref name="prefix"/> is what the walk has travelled so far — a collection rule
    /// contributes <c>Name[]</c> because its child judges elements, and a rule with no property
    /// name (an <c>Include</c>, or <c>RuleFor(x =&gt; x)</c>) contributes nothing, since its
    /// child's failures land at this level.
    /// </summary>
    /// <remarks>
    /// Selection is asked of <paramref name="selector"/>, and a child boundary follows
    /// FluentValidation's own dispatch: an adaptor carrying rulesets replaces the selector its
    /// child is read under — a scoped child's rules are never consulted against the profile's
    /// selector, exactly as a run never consults them against it — while an adaptor carrying
    /// none hands its child the selector in force unchanged.
    /// <paramref name="walking"/> holds the validators on the current path, not every validator
    /// seen: a validator reached twice down two different branches is read twice (the paths
    /// differ), while one that reaches itself is read once, which is what bounds a recursive
    /// validator without a depth cap. Conditionality travels down — a child reached only
    /// through a conditional rule is conditionally demanded, however unconditionally the child
    /// declares it.
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

            var name = rule.PropertyName;

            // Read once per rule: both the row-filter check below and the child prefix's
            // Name[]-versus-Name choice ask the identical question of the identical rule, and
            // neither needs it more than once.
            var collectionRuleType = CollectionRuleTypeOf(rule, modelType);

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

                    var childPrefix = string.IsNullOrEmpty(name)
                        ? prefix
                        : $"{prefix}{name}{(collectionRuleType is not null ? "[]" : string.Empty)}.";

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
                        child.Validator, rule.TypeToValidate, childPrefix, componentConditional,
                        childSelector, selectionContext, declared, walking);
                    continue;
                }

                if (string.IsNullOrEmpty(name))
                {
                    continue; // a model-level component names no field
                }

                var demand = !IsPresenceComponent(component)
                    ? FieldRequirement.NotRequired
                    : componentConditional
                        ? FieldRequirement.ConditionallyRequired
                        : FieldRequirement.Required;

                var path = prefix + name;

                // The strongest demand any component makes wins, which is what puts an
                // unconditional presence rule ahead of a conditional one on the same field and
                // keeps a field with rules but no presence component in the map at all.
                declared[path] = declared.TryGetValue(path, out var existing) && existing > demand
                    ? existing
                    : demand;
            }
        }

        walking.Remove(validator);
    }

    /// <summary>
    /// The closed <c>ICollectionRule&lt;,&gt;</c> for this rule's model and element type, or
    /// <see langword="null"/> when the rule does not implement it — the one question behind
    /// both "does the walk descend under <c>Name[]</c> or under <c>Name</c>" and "can this rule
    /// carry a row filter at all", asked once per rule rather than once per question. The
    /// closed interface is built from a literal <c>typeof</c> so the trimmer keeps the
    /// interface both callers' instance tests need.
    /// </summary>
    /// <remarks>
    /// A model/element pair that cannot close the interface answers <see langword="null"/> the
    /// same as a rule that simply isn't one — <c>MakeGenericType</c> has no constraints to fail
    /// against on this interface, so the narrow catch is defensive rather than load-bearing. The
    /// return carries a <c>PublicProperties</c> annotation for <see cref="HasRowFilter"/>'s
    /// sake — the trimmer already keeps the closed interface's full member set once it is
    /// constructed from a literal <c>typeof</c>, but that provenance does not survive crossing
    /// a method boundary as a plain <see cref="Type"/> without one.
    /// </remarks>
    [return: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)]
    private static Type? CollectionRuleTypeOf(IValidationRule rule, Type modelType)
    {
        try
        {
            var collectionRuleType = typeof(ICollectionRule<,>)
                .MakeGenericType(modelType, rule.TypeToValidate);
            return collectionRuleType.IsInstanceOfType(rule) ? collectionRuleType : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether the rule filters the rows it judges — <c>RuleForEach(...).Where(...)</c> or its
    /// asynchronous twin, which FluentValidation stores as the collection rule's <c>Filter</c>
    /// and <c>AsyncFilter</c> properties rather than as condition flags. A row filter is a
    /// per-row condition, and inspection has no rows to evaluate it against, so its presence
    /// makes the rule's demands conditional exactly as a <c>When</c> on the rule does. The
    /// properties are found by name, which no <c>typeof</c> roots, so a build that trims them
    /// away fails the read quietly rather than crashing.
    /// </summary>
    /// <remarks>
    /// True only on a positive read of a non-null filter: a rule whose filter cannot be read
    /// answers as unfiltered, because a demand the rules make of every row must not weaken
    /// merely because a property could not be reached — the failed read costs the filtered
    /// shape its cap, never an unfiltered rule its demand. A rule that is not a collection rule
    /// at all — <paramref name="collectionRuleType"/> null — answers unfiltered on the same
    /// grounds, with nothing to read in the first place.
    /// </remarks>
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

    /// <summary>
    /// What one child-validator component yields to inspection: the validator it wraps — or
    /// <see langword="null"/> where that cannot be had without a model — and the rulesets the
    /// adaptor scopes the child with, exactly as declared, <see langword="null"/> and empty
    /// both meaning unscoped.
    /// </summary>
    private sealed record ChildReading(IValidator? Validator, string[]? RuleSets)
    {
        public static readonly ChildReading Unreadable = new(null, null);
    }

    /// <summary>
    /// The validator a child component wraps and the rulesets its adaptor scopes it with, the
    /// validator <see langword="null"/> where it cannot be had without a model.
    /// FluentValidation exposes both through <c>ChildValidatorAdaptor&lt;T, TProperty&gt;</c> —
    /// the <c>GetValidator</c> method and the public <c>RuleSets</c> property its published XML
    /// docs do not mention — whose closed type is built from a literal <c>typeof</c>, which is
    /// what keeps the trimmer from removing the members this reads.
    /// </summary>
    /// <remarks>
    /// The call needs a context, and inspection has no model, so it passes one carrying none.
    /// An adaptor holding a validator INSTANCE ignores it and hands the validator back; one
    /// holding a factory runs that factory against a model that is not there, which is why a
    /// lambda-supplied child validator reports as unreadable rather than as having no rules.
    /// Every failure lands in the same place — no child, so no paths from it — because an
    /// inspection answer decorates a form and a missing decoration beats a thrown render.
    /// <para>
    /// Answers are remembered per component, including the answer "cannot be had": a component
    /// is one declaration on one rule of one validator, and this validator's rules are fixed
    /// once it is constructed, so the child behind a component cannot change. That is what
    /// keeps a caller asking per field — the required indicator does — from paying for a
    /// reflective resolve of every child in the form on every ask.
    /// </para>
    /// </remarks>
    private ChildReading ResolveChildValidator(IRuleComponent component, Type modelType, Type propertyType)
    {
        if (_childReadings.TryGetValue(component, out var cached))
        {
            return cached;
        }

        var resolved = ReadChildValidator(component, modelType, propertyType);
        _childReadings[component] = resolved;
        return resolved;
    }

    private static ChildReading ReadChildValidator(IRuleComponent component, Type modelType, Type propertyType)
    {
        try
        {
            var adaptor = typeof(ChildValidatorAdaptor<,>).MakeGenericType(modelType, propertyType);
            var getValidator = adaptor.GetMethod(FluentValidationInspectionSurface.GetValidatorMethod, BindingFlags.Public | BindingFlags.Instance);
            if (getValidator is null)
            {
                return ChildReading.Unreadable;
            }

            var context = Activator.CreateInstance(
                typeof(ValidationContext<>).MakeGenericType(modelType), [null]);

            if (getValidator.Invoke(component.Validator, [context, null]) is not IValidator validator)
            {
                return ChildReading.Unreadable;
            }

            var ruleSets = adaptor
                .GetProperty(FluentValidationInspectionSurface.RuleSetsProperty, BindingFlags.Public | BindingFlags.Instance)
                ?.GetValue(component.Validator) as string[];

            return new ChildReading(validator, ruleSets);
        }
        catch (Exception)
        {
            return ChildReading.Unreadable;
        }
    }

    /// <summary>
    /// Presence as FluentValidation itself declares it: the marker interfaces its
    /// <c>NotEmpty()</c> and <c>NotNull()</c> validators carry. Matching on the marker rather
    /// than on a validator's name keeps <c>Null()</c> — whose marker is the similarly spelled
    /// <see cref="INullValidator"/>, and which demands the opposite — out of the answer.
    /// </summary>
    private static bool IsPresenceComponent(IRuleComponent component) =>
        component.Validator is INotEmptyValidator or INotNullValidator;
}
