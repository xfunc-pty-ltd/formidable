using FluentValidation;
using FluentValidation.Internal;
using FluentValidation.Results;
using FluentValidation.Validators;

namespace Formidable;

/// <summary>Adapts a FluentValidation <see cref="IValidator{T}"/> to <see cref="IModelValidator{TModel}"/>, and can run an <see cref="AbstractValidator{T}"/>'s rules one set at a time and read what they demand.</summary>
/// <typeparam name="TModel">The model type the validator accepts.</typeparam>
/// <remarks>
/// <see cref="FormidableServiceCollectionExtensions.AddFormidable"/> registers this class as the
/// <see cref="IModelValidator{TModel}"/> of any model no other registration covers. The two capabilities,
/// <see cref="IRuleLevelValidator{TModel}"/> and <see cref="IRuleInspectingValidator{TModel}"/>,
/// need the wrapped validator to be an <see cref="AbstractValidator{T}"/>; their testers say
/// which is on.
/// </remarks>
public sealed partial class FluentValidationModelValidator<TModel>
    : IModelValidator<TModel>, IRuleLevelValidator<TModel>, IRuleInspectingValidator<TModel>
{
    private readonly IValidator<TModel> _validator;

    private SelectedRulesSnapshot? _selectedRules;

    /// <summary>Wraps <paramref name="validator"/>.</summary>
    /// <param name="validator">The FluentValidation validator to adapt.</param>
    /// <exception cref="ArgumentNullException"><paramref name="validator"/> is <see langword="null"/>.</exception>
    public FluentValidationModelValidator(IValidator<TModel> validator)
    {
        ArgumentNullException.ThrowIfNull(validator);
        _validator = validator;
    }

    /// <summary>Validates <paramref name="model"/> with the rules <paramref name="profile"/> selects.</summary>
    /// <param name="model">The model to validate.</param>
    /// <param name="profile">The rule selection to run.</param>
    /// <param name="cancellationToken">Cancels an async rule still running.</param>
    /// <returns>The report; <see cref="ValidationReport.IsValid"/> is <see langword="true"/> when no error was found.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="profile"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="profile"/> names a ruleset a <see cref="ProfiledValidator{T}"/> never registered.</exception>
    public async Task<ValidationReport> ValidateAsync(TModel model, ValidationProfile profile, CancellationToken cancellationToken = default) =>
        ToReport(await _validator.ValidateAsync(model, profile, cancellationToken).ConfigureAwait(false));

    /// <summary>Validates <paramref name="model"/> synchronously with the rules <paramref name="profile"/> selects; prefer <see cref="ValidateAsync"/>.</summary>
    /// <param name="model">The model to validate.</param>
    /// <param name="profile">The rule selection to run.</param>
    /// <returns>The report.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="profile"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="profile"/> names a ruleset a <see cref="ProfiledValidator{T}"/> never registered.</exception>
    /// <exception cref="AsyncValidatorInvokedSynchronouslyException">A rule <paramref name="profile"/> selects reaches an async validator or an async condition.</exception>
    public ValidationReport Validate(TModel model, ValidationProfile profile) =>
        ToReport(_validator.Validate(model, profile));

    /// <summary>Whether rules can be selected and run one set at a time: <see langword="true"/> exactly when the wrapped validator is an <see cref="AbstractValidator{T}"/> whose <see cref="AbstractValidator{T}.ClassLevelCascadeMode"/> is <see cref="CascadeMode.Continue"/>.</summary>
    /// <remarks>
    /// A class-level cascade stop lets one failing rule suppress the rules after it, which
    /// running part of a profile cannot reproduce, so such a validator reports
    /// <see langword="false"/>; a hand-rolled <see cref="IValidator{T}"/> cannot list its rules
    /// and reports <see langword="false"/> too. A per-rule <c>Cascade(CascadeMode.Stop)</c> does
    /// not matter.
    /// </remarks>
    public bool CanValidateByRule =>
        _validator is AbstractValidator<TModel> { ClassLevelCascadeMode: CascadeMode.Continue };

    /// <summary>Returns an identity for each rule <paramref name="profile"/> selects, in declaration order; the answer for the last profile instance asked about is reused.</summary>
    /// <param name="profile">The rule selection to list.</param>
    /// <returns>A read-only list of identities scoped to this instance.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="profile"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException"><see cref="CanValidateByRule"/> is <see langword="false"/>.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="profile"/> names a ruleset a <see cref="ProfiledValidator{T}"/> never registered.</exception>
    /// <remarks>
    /// The list handed back is read-only, so no caller can alter what the next is served. One
    /// answer is kept at a time, by profile reference: a call naming the same instance as the
    /// last call is served the same list, and any other instance is computed afresh. A rule
    /// added after construction is never read. A <see cref="ProfiledValidator{T}"/>'s ruleset
    /// names are verified before anything is kept.
    /// </remarks>
    public IReadOnlyList<RuleIdentity> SelectRules(ValidationProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        // One slot keyed by profile reference, the pattern DeclaredFields uses: a caller building
        // its plan asks repeatedly for the same stored profile instance, and a hit skips both the
        // ruleset verification and the walk of the validator's rules. Reference rather than
        // equality because two callers racing both walk and one wins, wasteful once and never
        // wrong: the walk reads the profile's shape alone, so a reference miss only ever
        // recomputes an answer already implied by it.
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

        // Every caller is handed the filed instance itself. What keeps one caller from altering
        // what the next is served is that a collection expression assigned to IReadOnlyList<T>
        // is the compiler's read-only wrapper: a downcast to IList<T> throws on write.
        var filed = new SelectedRulesSnapshot(profile, [.. selected]);
        _selectedRules = filed;
        return filed.Rules;
    }

    /// <summary>One profile's selected rules, held with the profile so the pair is never read torn.</summary>
    /// <param name="Profile">The profile instance the rules were selected for.</param>
    /// <param name="Rules">The identities selected for it.</param>
    private sealed record SelectedRulesSnapshot(ValidationProfile Profile, IReadOnlyList<RuleIdentity> Rules);

    /// <summary>Validates <paramref name="model"/> against exactly the given rules in one validator call, filtering their child rules by <paramref name="profile"/> as validating the whole profile would.</summary>
    /// <param name="model">The model to validate.</param>
    /// <param name="profile">The rule selection whose ruleset names filter collection child rules, child validators and <c>Include()</c> internals.</param>
    /// <param name="rules">Identities from <see cref="SelectRules"/> on this same instance.</param>
    /// <param name="cancellationToken">Cancels an async rule still running.</param>
    /// <returns>The report for those rules, and whether it answers only for <paramref name="profile"/>; an empty set answers an empty report.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="profile"/> or <paramref name="rules"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An identity in <paramref name="rules"/> did not come from <see cref="SelectRules"/> on this instance, or is the default identity.</exception>
    /// <exception cref="NotSupportedException"><see cref="CanValidateByRule"/> is <see langword="false"/>.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="profile"/> names a ruleset a <see cref="ProfiledValidator{T}"/> never registered.</exception>
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

    /// <summary>Resolves every identity in <paramref name="rules"/> to its rule in one walk of the validator's rules.</summary>
    /// <param name="validator">The validator whose rules the identities name.</param>
    /// <param name="rules">The identities to resolve.</param>
    /// <returns>The resolved rules, compared by reference.</returns>
    /// <exception cref="ArgumentException">An identity carries no rule, or a rule this validator does not hold.</exception>
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
                    $"('{FriendlyTypeName.Of(validator.GetType())}') — identities are validator-instance-scoped.",
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
                $"('{FriendlyTypeName.Of(validator.GetType())}') — identities are validator-instance-scoped.",
                nameof(rules));
        }

        return resolved;
    }

    /// <summary>Admits exactly the given top-level rules, by reference, and hands child-context decisions to the profile's own ruleset selector.</summary>
    /// <remarks>
    /// A child whose adaptor carries no rulesets is consulted here and delegated to the profile's
    /// <see cref="RulesetValidatorSelector"/>, the selector shape a full validation uses, so nested
    /// rulesets, unscoped child validators and <c>Include()</c> internals filter identically. A
    /// child whose adaptor carries rulesets runs under a selector FluentValidation builds from
    /// those alone and never reaches this one. It also records whether any child decision
    /// depended on the profile rather than on the admitting rule, which becomes
    /// <see cref="RuleLevelResult.IsProfileScoped"/> for the whole set.
    /// </remarks>
    // Never being consulted for a scoped child loses nothing: a selection that never reads the
    // profile cannot depend on one.
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

        /// <summary>Whether every profile that selects the admitting top-level rule necessarily admits <paramref name="childRule"/>.</summary>
        /// <param name="childRule">The child rule a child context is asking about.</param>
        /// <returns><see langword="true"/> when the decision follows from the owner's own selection and never from the profile.</returns>
        /// <remarks>
        /// An <c>Include()</c> owner never guarantees it because it runs under every profile. An
        /// untagged child follows only an untagged owner; a tagged child follows only an owner
        /// whose every ruleset tag it carries, which <c>ChildRules</c> children always do. Before
        /// any rule of the set is admitted there is no owner, and the answer is taken over every
        /// member of the set.
        /// </remarks>
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

    /// <summary>Partitions <paramref name="rules"/> by ruleset membership, giving an <c>Include()</c> rule and any rule reaching a child validator a group of its own, so no profile splits a group.</summary>
    /// <param name="rules">Identities from <see cref="SelectRules"/> on this same instance.</param>
    /// <returns>Groups that together hold every identity in <paramref name="rules"/> exactly once.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="rules"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException"><see cref="CanValidateByRule"/> is <see langword="false"/>.</exception>
    /// <remarks>
    /// Membership is everything FluentValidation's selection reads about a top-level rule that
    /// is not an <c>Include()</c>; memberships compare case-insensitively and in any order, and a
    /// wildcard profile admits every group whole. A rule reaching a child validator is the one
    /// that can raise <see cref="RuleLevelResult.IsProfileScoped"/> for a call, and its own
    /// group keeps that off its siblings.
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

    /// <summary>Whether <paramref name="rule"/> takes a group of its own: an <c>Include()</c> rule, or any rule reaching a child validator.</summary>
    /// <param name="rule">The top-level rule to classify.</param>
    /// <returns><see langword="true"/> for an <c>Include()</c> rule or a rule with a child-validator component.</returns>
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

    /// <summary>The rule's ruleset memberships as one key: sorted, compared case-insensitively, joined on NUL.</summary>
    /// <param name="identity">The identity whose rule is read.</param>
    /// <returns>The key; empty for a rule outside every ruleset.</returns>
    private static string MembershipKey(RuleIdentity identity)
    {
        if (identity.Key is not IValidationRule rule || rule.RuleSets is not { Length: > 0 } tags)
        {
            return string.Empty;
        }

        // Sorted because declaration order carries no meaning; joined on NUL, a character a
        // ruleset name would have to carry deliberately, so two names cannot read as one.
        var sorted = new string[tags.Length];
        Array.Copy(tags, sorted, tags.Length);
        Array.Sort(sorted, StringComparer.OrdinalIgnoreCase);
        return string.Join('\u0000', sorted);
    }

    /// <summary>Runs the wrapped validator's ruleset-name verification where it has one, so a misnamed ruleset fails here as it does when validating.</summary>
    /// <param name="profile">The profile whose ruleset names are checked.</param>
    /// <exception cref="InvalidOperationException"><paramref name="profile"/> names a ruleset a <see cref="ProfiledValidator{T}"/> never registered.</exception>
    private void VerifyRuleSets(ValidationProfile profile) =>
        ProfiledValidator<TModel>.VerifyRuleSetsIfProfiled(_validator, profile);

    /// <summary>The profile's selector, built by FluentValidation's global ruleset-selector factory from <see cref="ValidationProfile.ToRuleSetNames"/>.</summary>
    /// <param name="profile">The profile to build the selector for.</param>
    /// <returns>The selector the factory returns for the profile's names.</returns>
    /// <remarks>
    /// A consumer who replaces <c>ValidatorOptions.Global.ValidatorSelectors.RulesetValidatorSelectorFactory</c>
    /// changes what this validator selects and what a full validation selects together.
    /// </remarks>
    // The very list ValidatorProfileExtensions names on its validation strategy, so the two
    // selections read one profile once rather than agreeing by discipline; the strategy resolves
    // its own selector through the same factory.
    private static IValidatorSelector BuildProfileSelector(ValidationProfile profile) =>
        ValidatorOptions.Global.ValidatorSelectors.RulesetValidatorSelectorFactory(profile.ToRuleSetNames());

    /// <summary>The stock <see cref="RulesetValidatorSelector"/> for the profile's names, which <see cref="RuleSetSelector"/> delegates child-context decisions to.</summary>
    /// <param name="profile">The profile to build the selector for.</param>
    /// <returns>A selector over <see cref="ValidationProfile.ToRuleSetNames"/>.</returns>
    // The shape a full validation produces while the global factory is unreplaced.
    private static RulesetValidatorSelector BuildWholeProfileSelector(ValidationProfile profile) =>
        new(profile.ToRuleSetNames());

    /// <summary>A model-less context for selection questions; the stock selector reads only the rule and its own name list.</summary>
    /// <returns>A context whose model is <see langword="null"/>.</returns>
    /// <remarks>
    /// A replaced selector factory (<see cref="BuildProfileSelector"/>) whose selector reads the
    /// model reads <see langword="null"/> here.
    /// </remarks>
    // FluentValidation's ruleset selector answers from the rule's memberships and its own name
    // list, using the context only as a scratchpad for bookkeeping it never reads back.
    private static ValidationContext<TModel> CreateSelectionContext() => new(default!);

    private AbstractValidator<TModel> RequireRuleLevelCapability()
    {
        if (_validator is not AbstractValidator<TModel> abstractValidator)
        {
            throw new NotSupportedException(
                $"'{FriendlyTypeName.Of(_validator.GetType())}' does not enumerate its rules; rule-level validation requires a " +
                $"FluentValidation AbstractValidator<{FriendlyTypeName.Of(typeof(TModel))}>. Check {nameof(CanValidateByRule)} first.");
        }

        if (abstractValidator.ClassLevelCascadeMode != CascadeMode.Continue)
        {
            throw new NotSupportedException(
                $"'{FriendlyTypeName.Of(_validator.GetType())}' sets ClassLevelCascadeMode.{abstractValidator.ClassLevelCascadeMode}, " +
                "which lets a failing rule suppress later rules within one whole-profile pass — executing part " +
                $"of a profile cannot reproduce that. Check {nameof(CanValidateByRule)} first.");
        }

        return abstractValidator;
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
