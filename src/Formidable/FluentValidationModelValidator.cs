using System.Collections.Concurrent;
using System.Reflection;
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
public sealed class FluentValidationModelValidator<TModel>
    : IModelValidator<TModel>, IRuleLevelValidator<TModel>, IRuleInspectingValidator<TModel>
{
    private readonly IValidator<TModel> _validator;

    // Keyed by component reference, since a component is one declaration on one rule and
    // this validator's rules are fixed once it is constructed. Bounded by the number of
    // child-validator components the wrapped validator declares, which is a property of the
    // code rather than of anything a request carries.
    private readonly ConcurrentDictionary<IRuleComponent, IValidator?> _childValidators = new();

    private DeclaredSnapshot? _declared;

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
    /// rules within one whole-profile pass — separate per-rule executions can reproduce
    /// neither, so both shapes report false and belong on a caller's whole-profile path.
    /// </remarks>
    public bool CanValidateByRule =>
        _validator is AbstractValidator<TModel> { ClassLevelCascadeMode: CascadeMode.Continue };

    /// <inheritdoc />
    /// <remarks>
    /// When the wrapped validator derives from <see cref="ProfiledValidator{T}"/>, its
    /// ruleset-name verification runs first, exactly as whole-profile validation performs it.
    /// </remarks>
    public IReadOnlyList<RuleIdentity> SelectRules(ValidationProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var abstractValidator = RequireRuleLevelCapability();
        VerifyRuleSets(profile);

        var selected = new List<RuleIdentity>();
        foreach (var rule in (IEnumerable<IValidationRule>)abstractValidator)
        {
            if (IsSelected(rule, profile))
            {
                selected.Add(new RuleIdentity(rule));
            }
        }

        return selected;
    }

    /// <inheritdoc />
    public async Task<RuleLevelResult> ValidateRuleAsync(TModel model, ValidationProfile profile, RuleIdentity rule, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var abstractValidator = RequireRuleLevelCapability();
        VerifyRuleSets(profile);

        var target = ResolveRule(abstractValidator, rule);
        var selector = new SingleRuleSelector(target, BuildWholeProfileSelector(profile));
        var context = new ValidationContext<TModel>(model, new PropertyChain(), selector);
        var report = ToReport(await _validator.ValidateAsync(context, cancellationToken).ConfigureAwait(false));
        return new RuleLevelResult(report, selector.SawProfileScopedDecision);
    }

    /// <inheritdoc />
    /// <remarks>
    /// True exactly when the wrapped validator is a FluentValidation
    /// <c>AbstractValidator&lt;TModel&gt;</c>, whatever its cascade mode: reading a rule's
    /// declared components asks nothing of execution order, so the class-level cascade stop
    /// that bars <see cref="CanValidateByRule"/> does not bar inspection. A hand-rolled
    /// <see cref="IValidator{T}"/> keeps its rules to itself and reports false.
    /// </remarks>
    public bool CanInspectRules => _validator is AbstractValidator<TModel>;

    /// <inheritdoc />
    public RuleRequirement GetFieldRequirement(string fieldPath, ValidationProfile profile)
    {
        ArgumentNullException.ThrowIfNull(fieldPath);
        ArgumentNullException.ThrowIfNull(profile);

        return DeclaredFields(profile).TryGetValue(fieldPath, out var requirement)
            ? requirement
            : RuleRequirement.NotRequired;
    }

    /// <inheritdoc />
    public IReadOnlySet<string> GetDeclaredFieldPaths(ValidationProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return new HashSet<string>(DeclaredFields(profile).Keys, StringComparer.Ordinal);
    }

    /// <summary>
    /// The one walk both inspection readers answer from: every field path the profile's rules
    /// declare, mapped to the presence demand those rules make of it. A path with rules but no
    /// presence component is present with <see cref="RuleRequirement.NotRequired"/> — the two
    /// questions the readers ask are "is this field spoken about" and "is it demanded", and
    /// only a map that keeps both can answer them consistently.
    /// </summary>
    private Dictionary<string, RuleRequirement> DeclaredFields(ValidationProfile profile)
    {
        // One profile deep, and keyed by REFERENCE. Both readers are asked repeatedly for the
        // same profile - a form builds its whole requirement map by asking once per declared
        // path - and a walk allocates a dictionary and resolves every child validator it meets.
        // One entry covers that pattern exactly and cannot grow, which a per-profile map could
        // if a caller built a fresh profile per ask. Reference rather than equality because
        // profiles compare by NAME: two carrying the same name and different rulesets are one
        // key, and would serve each other's answer.
        if (_declared is { } snapshot && ReferenceEquals(snapshot.Profile, profile))
        {
            return snapshot.Fields;
        }

        var declared = new Dictionary<string, RuleRequirement>(StringComparer.Ordinal);
        if (_validator is not AbstractValidator<TModel> abstractValidator)
        {
            return declared;
        }

        // Ahead of the store, so a profile naming a ruleset that was never registered throws on
        // every ask rather than only on the first - nothing unverified is ever remembered.
        VerifyRuleSets(profile);
        WalkDeclaredRules(
            abstractValidator,
            typeof(TModel),
            string.Empty,
            conditional: false,
            profile,
            declared,
            new HashSet<object>(ReferenceEqualityComparer.Instance));

        // A reference assignment, so a reader sees one snapshot or the other and never a mix.
        // Two callers racing both walk and one wins: wasteful once, never wrong.
        _declared = new DeclaredSnapshot(profile, declared);
        return declared;
    }

    /// <summary>One profile's declared fields, held together so the pair cannot be read torn.</summary>
    private sealed record DeclaredSnapshot(ValidationProfile Profile, Dictionary<string, RuleRequirement> Fields);

    /// <summary>
    /// Walks one validator's selected rules, filing each leaf component under the path its
    /// failures will carry and descending through every child validator it can resolve.
    /// <paramref name="prefix"/> is what the walk has travelled so far — a collection rule
    /// contributes <c>Name[]</c> because its child judges elements, and a rule with no property
    /// name (an <c>Include</c>, or <c>RuleFor(x =&gt; x)</c>) contributes nothing, since its
    /// child's failures land at this level.
    /// </summary>
    /// <remarks>
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
        ValidationProfile profile,
        Dictionary<string, RuleRequirement> declared,
        HashSet<object> walking)
    {
        if (validator is not IEnumerable<IValidationRule> rules || !walking.Add(validator))
        {
            return;
        }

        foreach (var rule in rules)
        {
            if (!IsSelected(rule, profile))
            {
                continue;
            }

            var name = rule.PropertyName;

            // FluentValidation records a condition in one of two places depending on how it was
            // written: a When block wrapping the rule declaration marks the RULE, while a When
            // chained after a component marks the COMPONENT — including the
            // ApplyConditionTo.CurrentValidator form, which marks that one component alone and
            // leaves its siblings unconditional. Both places carry a synchronous and an
            // asynchronous flag, so all four are consulted.
            var ruleConditional = conditional || rule.HasCondition || rule.HasAsyncCondition;

            foreach (var component in rule.Components)
            {
                var componentConditional =
                    ruleConditional || component.HasCondition || component.HasAsyncCondition;

                if (component.Validator is IChildValidatorAdaptor)
                {
                    var child = ResolveChildValidator(component, modelType, rule.TypeToValidate);
                    if (child is null)
                    {
                        continue;
                    }

                    var childPrefix = string.IsNullOrEmpty(name)
                        ? prefix
                        : $"{prefix}{name}{(IsCollectionRule(rule, modelType) ? "[]" : string.Empty)}.";

                    WalkDeclaredRules(
                        child, rule.TypeToValidate, childPrefix, componentConditional, profile, declared, walking);
                    continue;
                }

                if (string.IsNullOrEmpty(name))
                {
                    continue; // a model-level component names no field
                }

                var demand = !IsPresenceComponent(component)
                    ? RuleRequirement.NotRequired
                    : componentConditional
                        ? RuleRequirement.ConditionallyRequired
                        : RuleRequirement.Required;

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
    /// The validator a child component wraps, or <see langword="null"/> where it cannot be had
    /// without a model. FluentValidation exposes the child through
    /// <c>ChildValidatorAdaptor&lt;T, TProperty&gt;.GetValidator</c>, whose signature is closed
    /// over the container type and the member type; the closed type is built from a literal
    /// <c>typeof</c>, which is what keeps the trimmer from removing the method this calls.
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
    private IValidator? ResolveChildValidator(IRuleComponent component, Type modelType, Type propertyType)
    {
        if (_childValidators.TryGetValue(component, out var cached))
        {
            return cached;
        }

        var resolved = ReadChildValidator(component, modelType, propertyType);
        _childValidators[component] = resolved;
        return resolved;
    }

    private static IValidator? ReadChildValidator(IRuleComponent component, Type modelType, Type propertyType)
    {
        try
        {
            var adaptor = typeof(ChildValidatorAdaptor<,>).MakeGenericType(modelType, propertyType);
            var getValidator = adaptor.GetMethod("GetValidator", BindingFlags.Public | BindingFlags.Instance);
            if (getValidator is null)
            {
                return null;
            }

            var context = Activator.CreateInstance(
                typeof(ValidationContext<>).MakeGenericType(modelType), [null]);

            return getValidator.Invoke(component.Validator, [context, null]) as IValidator;
        }
        catch (Exception)
        {
            return null;
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
    /// Selection semantics equivalent to the whole-profile selector the profile-aware entry
    /// points build (<see cref="ValidatorProfileExtensions"/>): FluentValidation's ruleset
    /// selector consulted with the profile's rulesets plus <c>"default"</c> when
    /// <see cref="ValidationProfile.IncludeDefaultRules"/> is set. A literal <c>"*"</c> among
    /// the profile's rulesets admits every rule; an untagged <c>Include()</c> rule always
    /// executes so its included rules can be filtered individually, while a tagged one is
    /// admitted on membership like any other tagged rule; otherwise untagged rules ride the
    /// default bucket and tagged rules are admitted on any case-insensitive membership match.
    /// A rule declared with a comma-separated ruleset list (<c>RuleSet("A,B", ...)</c>) is
    /// reachable through either name because FluentValidation splits the list into per-rule
    /// memberships at declaration time.
    /// </summary>
    private static bool IsSelected(IValidationRule rule, ValidationProfile profile)
    {
        // FluentValidation matches the wildcard by ordinal equality, and its presence admits
        // every rule regardless of what the other branches would say.
        if (profile.RuleSets.Contains(RulesetValidatorSelector.WildcardRuleSetName, StringComparer.Ordinal))
        {
            return true;
        }

        var memberships = rule.RuleSets;
        if (memberships is not { Length: > 0 })
        {
            return rule is IIncludeRule || IncludesDefaultBucket(profile);
        }

        foreach (var membership in memberships)
        {
            // A rule tagged into the literal "default" ruleset is admitted by any profile
            // that includes default rules — the name lands in the selector's list.
            if (profile.IncludeDefaultRules
                && string.Equals(membership, RulesetValidatorSelector.DefaultRuleSetName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            foreach (var ruleSet in profile.RuleSets)
            {
                if (string.Equals(membership, ruleSet, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IncludesDefaultBucket(ValidationProfile profile) =>
        profile.IncludeDefaultRules
        || profile.RuleSets.Contains(RulesetValidatorSelector.DefaultRuleSetName, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The exact selector shape the whole-profile entry points build for the profile
    /// (<see cref="ValidatorProfileExtensions"/>): the profile's rulesets, plus
    /// <c>"default"</c> when default rules are included.
    /// </summary>
    private static RulesetValidatorSelector BuildWholeProfileSelector(ValidationProfile profile)
    {
        var names = new List<string>(profile.RuleSets.Count + 1);
        names.AddRange(profile.RuleSets);
        if (profile.IncludeDefaultRules)
        {
            names.Add(RulesetValidatorSelector.DefaultRuleSetName);
        }

        return new RulesetValidatorSelector(names);
    }

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
                "which lets a failing rule suppress later rules within one whole-profile pass — separate per-rule " +
                $"executions cannot reproduce that. Check {nameof(CanValidateByRule)} first.");
        }

        return abstractValidator;
    }

    private static IValidationRule ResolveRule(AbstractValidator<TModel> validator, RuleIdentity rule)
    {
        if (rule.Key is IValidationRule target)
        {
            foreach (var candidate in (IEnumerable<IValidationRule>)validator)
            {
                if (ReferenceEquals(candidate, target))
                {
                    return target;
                }
            }
        }

        throw new ArgumentException(
            $"The rule identity was not produced by SelectRules on this validator " +
            $"('{validator.GetType().Name}') — identities are validator-instance-scoped.",
            nameof(rule));
    }

    /// <summary>
    /// Admits exactly one top-level rule, by reference. FluentValidation hands a child
    /// validator's context the parent context's selector, so child-context consultations
    /// reach this selector too; those decisions delegate to the profile's own
    /// <see cref="RulesetValidatorSelector"/> — the same selector shape, built from the same
    /// name list, that filters them in a whole-profile run — so nested rulesets, child
    /// validators, and <c>Include()</c> internals are filtered identically by construction.
    /// Along the way it records whether any child decision depended on the profile rather
    /// than following from the rule's own selection — the
    /// <see cref="RuleLevelResult.IsProfileScoped"/> signal.
    /// </summary>
    private sealed class SingleRuleSelector : IValidatorSelector
    {
        private readonly IValidationRule _rule;
        private readonly RulesetValidatorSelector _childSelector;

        public SingleRuleSelector(IValidationRule rule, RulesetValidatorSelector childSelector)
        {
            _rule = rule;
            _childSelector = childSelector;
        }

        public bool SawProfileScopedDecision { get; private set; }

        public bool CanExecute(IValidationRule rule, string propertyPath, IValidationContext context)
        {
            if (!context.IsChildContext)
            {
                return ReferenceEquals(rule, _rule);
            }

            if (!ChildDecisionIsProfileIndependent(rule))
            {
                SawProfileScopedDecision = true;
            }

            return _childSelector.CanExecute(rule, propertyPath, context);
        }

        /// <summary>
        /// A child-context decision is profile-independent when every profile that selects
        /// the top-level rule necessarily admits the child. An <c>Include()</c> rule executes
        /// under every profile, so nothing ties a selecting profile's names to its internals.
        /// An untagged child rides the default bucket, which only an untagged
        /// (default-bucket) top-level rule's selection guarantees. A tagged child is
        /// guaranteed only when it carries every one of the rule's own memberships — then any
        /// name that selected the rule also reaches the child. <c>ChildRules</c> children
        /// carry their parent declaration scope's propagated tags, so they always qualify;
        /// children with their own ruleset tags generally do not.
        /// </summary>
        private bool ChildDecisionIsProfileIndependent(IValidationRule childRule)
        {
            if (_rule is IIncludeRule)
            {
                return false;
            }

            var ruleTags = _rule.RuleSets;
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
                GetDisplayName(failure)))
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
