using System.Collections.Concurrent;
using System.Reflection;
using System.Text.RegularExpressions;
using FluentValidation;
using FluentValidation.Internal;
using FluentValidation.Results;
using FluentValidation.Validators;

namespace Formidable;

/// <summary>
/// Adapts a FluentValidation <see cref="IValidator{T}"/> to <see cref="IModelValidator{TModel}"/>,
/// and exposes rule-level selection and execution through
/// <see cref="IRuleLevelValidator{TModel}"/> and rule-level inspection through
/// <see cref="IRuleInspectingValidator{TModel}"/>.
/// </summary>
public sealed partial class FluentValidationModelValidator<TModel>
    : IModelValidator<TModel>, IRuleLevelValidator<TModel>, IRuleInspectingValidator<TModel>
{
    private readonly IValidator<TModel> _validator;

    // Keyed by component reference, since a component is one declaration on one rule and
    // this validator's rules are fixed once it is constructed. Bounded by the number of
    // child-validator components the wrapped validator declares, which is a property of the
    // code rather than of anything a request carries.
    private readonly ConcurrentDictionary<IRuleComponent, ChildReading> _childReadings = new();

    private DeclaredSnapshot? _declared;
    private SelectedRulesSnapshot? _selectedRules;

    /// <summary>Wraps the given FluentValidation validator.</summary>
    public FluentValidationModelValidator(IValidator<TModel> validator)
    {
        ArgumentNullException.ThrowIfNull(validator);
        _validator = validator;
    }

    /// <inheritdoc />
    public async Task<ValidationReport> ValidateAsync(TModel model, ValidationProfile profile, CancellationToken cancellationToken = default) =>
        ToReport(await _validator.ValidateAsync(model, profile, cancellationToken).ConfigureAwait(false));

    /// <inheritdoc />
    public ValidationReport Validate(TModel model, ValidationProfile profile) =>
        ToReport(_validator.Validate(model, profile));

    /// <inheritdoc />
    /// <remarks>
    /// True exactly when the wrapped validator is a FluentValidation
    /// <c>AbstractValidator&lt;TModel&gt;</c> whose <c>ClassLevelCascadeMode</c> is
    /// <c>CascadeMode.Continue</c>. A hand-rolled <see cref="IValidator{T}"/> cannot
    /// enumerate its rules, and a class-level cascade stop lets a failing rule suppress later
    /// rules within one whole-profile pass — executing part of a profile can reproduce
    /// neither, so both shapes report false and belong on a caller's whole-profile path.
    /// </remarks>
    public bool CanValidateByRule =>
        _validator is AbstractValidator<TModel> { ClassLevelCascadeMode: CascadeMode.Continue };

    /// <inheritdoc />
    /// <remarks>
    /// When the wrapped validator derives from <see cref="ProfiledValidator{T}"/>, its
    /// ruleset-name verification runs first, exactly as whole-profile validation performs it.
    /// Computed once per profile REFERENCE and cached afterward, the same one-slot pattern
    /// <see cref="DeclaredFields"/> uses: a plan-building caller asks this repeatedly for the
    /// same stored profile instance, and answering from the cache skips both the ruleset
    /// verification and the walk of the validator's rules. A validator's rules are fixed once
    /// it is constructed, so a rule added afterward is never read by a call this cache serves.
    /// The cached answer is a copy the cache alone holds a reference to, so what a caller does
    /// with the list it is handed cannot reach what the next caller is served.
    /// </remarks>
    public IReadOnlyList<RuleIdentity> SelectRules(ValidationProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        // Reference rather than equality, matching DeclaredFields: two callers racing both walk
        // and one wins, wasteful once, never wrong — the walk reads the profile's shape alone,
        // so a reference miss only ever recomputes an answer already implied by it.
        if (_selectedRules is { } snapshot && ReferenceEquals(snapshot.Profile, profile))
        {
            return snapshot.Rules;
        }

        var abstractValidator = RequireRuleLevelCapability();
        VerifyRuleSets(profile);

        var selector = BuildProfileSelector(profile);
        var selectionContext = CreateSelectionContext();
        var selected = new List<RuleIdentity>();
        foreach (var rule in (IEnumerable<IValidationRule>)abstractValidator)
        {
            if (selector.CanExecute(rule, string.Empty, selectionContext))
            {
                selected.Add(new RuleIdentity(rule));
            }
        }

        // The walk's own list is filed as a copy, so the answer every later call is handed
        // carries no route back into the cache for a caller who downcasts it.
        var filed = new SelectedRulesSnapshot(profile, [.. selected]);
        _selectedRules = filed;
        return filed.Rules;
    }

    /// <inheritdoc />
    public async Task<RuleLevelResult> ValidateRulesAsync(TModel model, ValidationProfile profile,
        IReadOnlyList<RuleIdentity> rules, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(rules);
        var abstractValidator = RequireRuleLevelCapability();
        VerifyRuleSets(profile);

        if (rules.Count == 0)
        {
            return new RuleLevelResult(ValidationReport.Empty, false);
        }

        // One context, one selector, one walk of the validator's rules however many the set
        // holds: FluentValidation asks the selector about every rule it carries on each call, so
        // asking about one rule at a time costs that walk once per rule.
        var targets = ResolveRules(abstractValidator, rules);
        var selector = new RuleSetSelector(targets, BuildWholeProfileSelector(profile));
        var context = new ValidationContext<TModel>(model, new PropertyChain(), selector);
        var report = ToReport(await _validator.ValidateAsync(context, cancellationToken).ConfigureAwait(false));
        return new RuleLevelResult(report, selector.SawProfileScopedDecision);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The class is the rule's own ruleset membership, which is everything FluentValidation's
    /// selection reads about a top-level rule that is not an <c>Include()</c>: two rules
    /// carrying the same memberships are admitted together by every profile, and a wildcard
    /// profile admits every group whole. Two shapes take a group of their own. An
    /// <c>Include()</c> rule is admitted under every profile whatever it is tagged with, so its
    /// selection does not follow its membership. And a rule whose scope reaches a child
    /// validator is the one that can record a profile-scoped decision, which a set-level verdict
    /// carries for the whole set — a group of its own keeps that off its siblings, so a
    /// sibling's profile-independent verdict survives a profile change. Splitting a group finer
    /// than selection demands stays sound: no profile can split a group of one either.
    /// </remarks>
    public IReadOnlyList<IReadOnlyList<RuleIdentity>> GroupBySelectionClass(IReadOnlyList<RuleIdentity> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        RequireRuleLevelCapability();

        if (rules.Count <= 1)
        {
            return [rules];
        }

        Dictionary<string, List<RuleIdentity>>? byMembership = null;
        var groups = new List<List<RuleIdentity>>();
        foreach (var identity in rules)
        {
            if (identity.Key is IValidationRule rule && SelectionFollowsMoreThanMembership(rule))
            {
                groups.Add([identity]);
                continue;
            }

            byMembership ??= new Dictionary<string, List<RuleIdentity>>(StringComparer.OrdinalIgnoreCase);
            var key = MembershipKey(identity);
            if (!byMembership.TryGetValue(key, out var group))
            {
                group = [];
                byMembership[key] = group;
                groups.Add(group);
            }

            group.Add(identity);
        }

        return [.. groups];
    }

    /// <summary>
    /// Whether <paramref name="rule"/> belongs in a group of its own: an <c>Include()</c> rule,
    /// whose selection does not follow its ruleset memberships, or any rule reaching a child
    /// validator — <c>SetValidator</c>, <c>ChildRules</c> and <c>Include()</c> alike — whose
    /// child-scope decisions are what a profile-scoped verdict is made of.
    /// </summary>
    private static bool SelectionFollowsMoreThanMembership(IValidationRule rule)
    {
        if (rule is IIncludeRule)
        {
            return true;
        }

        foreach (var component in rule.Components)
        {
            if (component.Validator is IChildValidatorAdaptor)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The rule's ruleset memberships as one comparable key. Sorted, because declaration order
    /// carries no meaning; compared the way FluentValidation compares ruleset names, which is
    /// case-insensitively; and joined on NUL, a character a ruleset name would have to carry
    /// deliberately, so two names cannot read as one.
    /// </summary>
    private static string MembershipKey(RuleIdentity identity)
    {
        if (identity.Key is not IValidationRule rule || rule.RuleSets is not { Length: > 0 } tags)
        {
            return string.Empty;
        }

        var sorted = new string[tags.Length];
        Array.Copy(tags, sorted, tags.Length);
        Array.Sort(sorted, StringComparer.OrdinalIgnoreCase);
        return string.Join('\u0000', sorted);
    }

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

    /// <summary>One profile's selected rules, held together so the pair cannot be read torn.</summary>
    private sealed record SelectedRulesSnapshot(ValidationProfile Profile, IReadOnlyList<RuleIdentity> Rules);

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
                || HasRowFilter(rule, modelType);

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
                        : $"{prefix}{name}{(IsCollectionRule(rule, modelType) ? "[]" : string.Empty)}.";

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
    /// Whether the rule filters the rows it judges — <c>RuleForEach(...).Where(...)</c> or its
    /// asynchronous twin, which FluentValidation stores as the collection rule's <c>Filter</c>
    /// and <c>AsyncFilter</c> properties rather than as condition flags. A row filter is a
    /// per-row condition, and inspection has no rows to evaluate it against, so its presence
    /// makes the rule's demands conditional exactly as a <c>When</c> on the rule does. The
    /// closed interface is built from a literal <c>typeof</c> so the trimmer keeps the
    /// interface the instance test needs; the properties are found by name, which no
    /// <c>typeof</c> roots, so a build that trims them away fails the read quietly rather
    /// than crashing.
    /// </summary>
    /// <remarks>
    /// True only on a positive read of a non-null filter: a rule whose filter cannot be read
    /// answers as unfiltered, because a demand the rules make of every row must not weaken
    /// merely because a property could not be reached — the failed read costs the filtered
    /// shape its cap, never an unfiltered rule its demand.
    /// </remarks>
    private static bool HasRowFilter(IValidationRule rule, Type modelType)
    {
        try
        {
            var collectionRule = typeof(ICollectionRule<,>)
                .MakeGenericType(modelType, rule.TypeToValidate);
            if (!collectionRule.IsInstanceOfType(rule))
            {
                return false;
            }

            return collectionRule.GetProperty("Filter", BindingFlags.Public | BindingFlags.Instance)?.GetValue(rule) is not null
                || collectionRule.GetProperty("AsyncFilter", BindingFlags.Public | BindingFlags.Instance)?.GetValue(rule) is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether the rule judges each element of a collection rather than the member itself —
    /// what decides whether the walk descends under <c>Name[]</c> or under <c>Name</c>. The
    /// closed interface is built from a literal <c>typeof</c> so the trimmer keeps what this
    /// tests for.
    /// </summary>
    private static bool IsCollectionRule(IValidationRule rule, Type modelType)
    {
        try
        {
            return typeof(ICollectionRule<,>)
                .MakeGenericType(modelType, rule.TypeToValidate)
                .IsInstanceOfType(rule);
        }
        catch (ArgumentException)
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
            var getValidator = adaptor.GetMethod("GetValidator", BindingFlags.Public | BindingFlags.Instance);
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
                .GetProperty("RuleSets", BindingFlags.Public | BindingFlags.Instance)
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

    /// <summary>
    /// Runs the wrapped validator's own ruleset-name verification where it has one, so a
    /// profile naming a ruleset that was never registered fails the same way here as it does
    /// when the profile is validated.
    /// </summary>
    private void VerifyRuleSets(ValidationProfile profile)
    {
        if (_validator is ProfiledValidator<TModel> profiledValidator)
        {
            profiledValidator.VerifyRuleSets(profile);
        }
    }

    /// <summary>
    /// The profile's root selector, obtained from FluentValidation's global ruleset-selector
    /// factory with the same name list the profile-aware entry points hand its validation
    /// strategy (<see cref="ValidatorProfileExtensions"/>) — and the strategy resolves its own
    /// selector through the same factory, so a consumer who replaces
    /// <c>ValidatorOptions.Global.ValidatorSelectors.RulesetValidatorSelectorFactory</c>
    /// changes what a whole-profile run selects and what this validator selects and reports in
    /// the same stroke.
    /// </summary>
    private static IValidatorSelector BuildProfileSelector(ValidationProfile profile) =>
        ValidatorOptions.Global.ValidatorSelectors.RulesetValidatorSelectorFactory(ProfileRuleSetNames(profile));

    /// <summary>
    /// The stock selector for the profile's name list — the shape the whole-profile entry
    /// points produce while the global factory is unreplaced. The single-rule executor
    /// delegates its child-context decisions here.
    /// </summary>
    private static RulesetValidatorSelector BuildWholeProfileSelector(ValidationProfile profile) =>
        new(ProfileRuleSetNames(profile));

    /// <summary>
    /// The profile's rulesets, plus <c>"default"</c> when
    /// <see cref="ValidationProfile.IncludeDefaultRules"/> is set — the name list every
    /// profile-shaped selector is built from.
    /// </summary>
    private static string[] ProfileRuleSetNames(ValidationProfile profile)
    {
        var names = new List<string>(profile.RuleSets.Count + 1);
        names.AddRange(profile.RuleSets);
        if (profile.IncludeDefaultRules)
        {
            names.Add(RulesetValidatorSelector.DefaultRuleSetName);
        }

        return [.. names];
    }

    /// <summary>
    /// The context selection questions are asked against. Inspection has no model, and none is
    /// needed: FluentValidation's ruleset selector answers from the rule's memberships and its
    /// own name list, using the context only as a scratchpad for bookkeeping it never reads
    /// back. A replaced selector factory (<see cref="BuildProfileSelector"/>) whose selector
    /// does read the model reads <see langword="null"/> here, because inspection has none to
    /// give.
    /// </summary>
    private static ValidationContext<TModel> CreateSelectionContext() => new(default!);

    private AbstractValidator<TModel> RequireRuleLevelCapability()
    {
        if (_validator is not AbstractValidator<TModel> abstractValidator)
        {
            throw new NotSupportedException(
                $"'{_validator.GetType().Name}' does not enumerate its rules; rule-level validation requires a " +
                $"FluentValidation AbstractValidator<{typeof(TModel).Name}>. Check {nameof(CanValidateByRule)} first.");
        }

        if (abstractValidator.ClassLevelCascadeMode != CascadeMode.Continue)
        {
            throw new NotSupportedException(
                $"'{_validator.GetType().Name}' sets ClassLevelCascadeMode.{abstractValidator.ClassLevelCascadeMode}, " +
                "which lets a failing rule suppress later rules within one whole-profile pass — executing part " +
                $"of a profile cannot reproduce that. Check {nameof(CanValidateByRule)} first.");
        }

        return abstractValidator;
    }

    /// <summary>
    /// Resolves a whole set of identities in one walk of the validator's rules, so the cost of
    /// the walk is paid once for the set rather than once for each of its members.
    /// </summary>
    private static HashSet<IValidationRule> ResolveRules(
        AbstractValidator<TModel> validator, IReadOnlyList<RuleIdentity> rules)
    {
        var wanted = new HashSet<object>(rules.Count, ReferenceEqualityComparer.Instance);
        foreach (var rule in rules)
        {
            if (rule.Key is not IValidationRule key)
            {
                throw new ArgumentException(
                    $"A rule identity carries no rule of this validator " +
                    $"('{validator.GetType().Name}') — identities are validator-instance-scoped.",
                    nameof(rules));
            }

            wanted.Add(key);
        }

        var resolved = new HashSet<IValidationRule>(wanted.Count, ReferenceEqualityComparer.Instance);
        foreach (var candidate in (IEnumerable<IValidationRule>)validator)
        {
            if (wanted.Contains(candidate))
            {
                resolved.Add(candidate);
            }
        }

        if (resolved.Count != wanted.Count)
        {
            throw new ArgumentException(
                $"A rule identity was not produced by SelectRules on this validator " +
                $"('{validator.GetType().Name}') — identities are validator-instance-scoped.",
                nameof(rules));
        }

        return resolved;
    }

    /// <summary>
    /// Admits exactly the given set of top-level rules, by reference. When a child validator's
    /// adaptor carries no rulesets, FluentValidation hands the child's context the parent
    /// context's selector, so consultations for that child's rules reach this selector too;
    /// those decisions delegate to the profile's own <see cref="RulesetValidatorSelector"/> —
    /// the same selector shape, built from the same name list, that filters them in a
    /// whole-profile run — so nested rulesets, unscoped child validators, and <c>Include()</c>
    /// internals are filtered identically by construction. A child whose adaptor does carry
    /// rulesets runs under a selector FluentValidation builds from those rulesets alone; this
    /// selector is never consulted for such a child's rules, and loses nothing by that — a
    /// selection that never reads the profile cannot depend on one. Along the way this
    /// selector records whether any decision it was consulted for depended on the profile
    /// rather than following from the admitting rule's own selection — the
    /// <see cref="RuleLevelResult.IsProfileScoped"/> signal, which the set carries when any one
    /// of its rules made such a decision.
    /// </summary>
    private sealed class RuleSetSelector : IValidatorSelector
    {
        private readonly HashSet<IValidationRule> _rules;
        private readonly RulesetValidatorSelector _childSelector;

        // Which admitted rule a child-context consultation belongs to. FluentValidation runs
        // top-level rules sequentially and asks CanExecute for one at the start of its own
        // execution, so every child consultation between one admission and the next is made
        // under the rule just admitted. Reading it that way makes the profile-scoped signal as
        // precise for a set as it is for a single rule; the set-wide fallback below can only ask
        // whether a decision follows for EVERY member, which reports scope wherever any member
        // could scope any child.
        private IValidationRule? _owner;

        public RuleSetSelector(HashSet<IValidationRule> rules, RulesetValidatorSelector childSelector)
        {
            _rules = rules;
            _childSelector = childSelector;
        }

        public bool SawProfileScopedDecision { get; private set; }

        public bool CanExecute(IValidationRule rule, string propertyPath, IValidationContext context)
        {
            if (!context.IsChildContext)
            {
                var admitted = _rules.Contains(rule);
                if (admitted)
                {
                    _owner = rule;
                }

                return admitted;
            }

            if (!ChildDecisionIsProfileIndependent(rule))
            {
                SawProfileScopedDecision = true;
            }

            return _childSelector.CanExecute(rule, propertyPath, context);
        }

        /// <summary>
        /// A child-context decision is profile-independent when every profile that selects the
        /// admitting top-level rule necessarily admits the child. An <c>Include()</c> rule
        /// executes under every profile, so nothing ties a selecting profile's names to its
        /// internals. An untagged child rides the default bucket, which only an untagged
        /// (default-bucket) top-level rule's selection guarantees. A tagged child is guaranteed
        /// only when it carries every one of the rule's own memberships — then any name that
        /// selected the rule also reaches the child. <c>ChildRules</c> children carry their
        /// parent declaration scope's propagated tags, so they always qualify; children with
        /// their own ruleset tags generally do not. A consultation arriving before any rule of
        /// the set has been admitted has no owner to reason from and is answered for the set,
        /// where the decision counts as profile-independent only if it follows for every member.
        /// </summary>
        private bool ChildDecisionIsProfileIndependent(IValidationRule childRule)
        {
            if (_owner is { } owner)
            {
                return FollowsFromOwner(owner, childRule);
            }

            foreach (var candidate in _rules)
            {
                if (!FollowsFromOwner(candidate, childRule))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool FollowsFromOwner(IValidationRule owner, IValidationRule childRule)
        {
            if (owner is IIncludeRule)
            {
                return false;
            }

            var ruleTags = owner.RuleSets;
            var childTags = childRule.RuleSets;
            if (childTags is not { Length: > 0 })
            {
                return ruleTags is not { Length: > 0 };
            }

            if (ruleTags is not { Length: > 0 })
            {
                return false;
            }

            foreach (var tag in ruleTags)
            {
                if (!childTags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }
    }

    private static ValidationReport ToReport(ValidationResult result)
    {
        if (result.Errors.Count == 0)
        {
            return ValidationReport.Empty;
        }

        var issues = result.Errors
            .Select(failure => new ValidationIssue(
                failure.PropertyName,
                failure.ErrorMessage,
                MapSeverity(failure.Severity),
                string.IsNullOrEmpty(failure.ErrorCode) ? null : failure.ErrorCode,
                GetDisplayName(failure),
                failure.CustomState))
            .ToList();

        return new ValidationReport(issues);
    }

    private static ValidationSeverity MapSeverity(Severity severity) => severity switch
    {
        Severity.Warning => ValidationSeverity.Warning,
        Severity.Info => ValidationSeverity.Info,
        _ => ValidationSeverity.Error
    };

    private static string? GetDisplayName(ValidationFailure failure) =>
        failure.FormattedMessagePlaceholderValues is { } values
        && values.TryGetValue("PropertyName", out var name)
            ? name?.ToString()
            : null;
}
