using System.Collections.ObjectModel;
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
        if (_validator is not AbstractValidator<TModel> abstractValidator)
        {
            return RuleRequirement.NotRequired;
        }

        VerifyRuleSets(profile);

        var conditional = false;
        foreach (var rule in (IEnumerable<IValidationRule>)abstractValidator)
        {
            if (!string.Equals(rule.PropertyName, fieldPath, StringComparison.Ordinal) || !IsSelected(rule, profile))
            {
                continue;
            }

            foreach (var component in rule.Components)
            {
                if (!IsPresenceComponent(component))
                {
                    continue;
                }

                if (!IsConditional(rule, component))
                {
                    return RuleRequirement.Required;
                }

                conditional = true;
            }
        }

        return conditional ? RuleRequirement.ConditionallyRequired : RuleRequirement.NotRequired;
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, FieldRuleCodes> GetFieldRuleCodes(ValidationProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (_validator is not AbstractValidator<TModel> abstractValidator)
        {
            return ReadOnlyDictionary<string, FieldRuleCodes>.Empty;
        }

        VerifyRuleSets(profile);

        var presence = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var other = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var rule in (IEnumerable<IValidationRule>)abstractValidator)
        {
            var field = rule.PropertyName;
            if (string.IsNullOrEmpty(field) || !IsSelected(rule, profile))
            {
                continue;
            }

            foreach (var component in rule.Components)
            {
                // A child or collection validator's failures carry the child's own path, so its
                // codes belong to those fields rather than to the field carrying the rule.
                if (component.Validator is IChildValidatorAdaptor)
                {
                    continue;
                }

                var bucket = IsPresenceComponent(component) ? presence : other;
                if (!bucket.TryGetValue(field, out var codes))
                {
                    codes = new HashSet<string>(StringComparer.Ordinal);
                    bucket[field] = codes;
                }

                codes.Add(ErrorCodeOf(component));
            }
        }

        var map = new Dictionary<string, FieldRuleCodes>(StringComparer.Ordinal);
        foreach (var field in presence.Keys.Concat(other.Keys).Distinct(StringComparer.Ordinal))
        {
            var presenceCodes = presence.TryGetValue(field, out var found) ? found : [];
            var otherCodes = other.TryGetValue(field, out var rest) ? rest : [];

            // A code both kinds of component can produce identifies neither, so it is reported
            // as ambiguous and withheld from both sets rather than silently attributed to one.
            var ambiguous = new HashSet<string>(presenceCodes, StringComparer.Ordinal);
            ambiguous.IntersectWith(otherCodes);
            if (ambiguous.Count > 0)
            {
                presenceCodes = new HashSet<string>(presenceCodes.Except(ambiguous, StringComparer.Ordinal), StringComparer.Ordinal);
                otherCodes = new HashSet<string>(otherCodes.Except(ambiguous, StringComparer.Ordinal), StringComparer.Ordinal);
            }

            map[field] = new FieldRuleCodes(presenceCodes, otherCodes, ambiguous);
        }

        return map;
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
    /// Whether the component is reached only through a condition. FluentValidation records a
    /// condition in one of two places depending on how it was written: a <c>When</c> block
    /// wrapping the rule declaration marks the rule, while a <c>When</c> chained after a
    /// component marks the component — including the <c>ApplyConditionTo.CurrentValidator</c>
    /// form, which marks that one component alone and leaves its siblings unconditional. Both
    /// places carry a synchronous and an asynchronous flag, so all four are consulted.
    /// </summary>
    private static bool IsConditional(IValidationRule rule, IRuleComponent component) =>
        rule.HasCondition || rule.HasAsyncCondition || component.HasCondition || component.HasAsyncCondition;

    /// <summary>
    /// The code FluentValidation puts on a failure this component produces: the configured
    /// <c>WithErrorCode</c> where there is one, otherwise the global resolver's default for the
    /// component's validator. Deriving it the way the failure does is what lets a caller match
    /// a reported issue back to the component that reported it.
    /// </summary>
    private static string ErrorCodeOf(IRuleComponent component) =>
        string.IsNullOrEmpty(component.ErrorCode)
            ? ValidatorOptions.Global.ErrorCodeResolver(component.Validator)
            : component.ErrorCode;

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
