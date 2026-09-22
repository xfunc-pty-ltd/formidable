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

    /// <summary>One profile's selected rules, held together so the pair cannot be read torn.</summary>
    private sealed record SelectedRulesSnapshot(ValidationProfile Profile, IReadOnlyList<RuleIdentity> Rules);

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

    /// <summary>
    /// Runs the wrapped validator's own ruleset-name verification where it has one, so a
    /// profile naming a ruleset that was never registered fails the same way here as it does
    /// when the profile is validated.
    /// </summary>
    private void VerifyRuleSets(ValidationProfile profile) =>
        ProfiledValidator<TModel>.VerifyRuleSetsIfProfiled(_validator, profile);

    /// <summary>
    /// The profile's root selector, built by FluentValidation's global ruleset-selector factory
    /// from <see cref="ValidationProfile.ToRuleSetNames"/> — the very list the profile-aware
    /// entry points (<see cref="ValidatorProfileExtensions"/>) name on their validation strategy,
    /// so the two selections read one profile once rather than agreeing by discipline. The
    /// strategy resolves its own selector through the same factory, so a consumer who replaces
    /// <c>ValidatorOptions.Global.ValidatorSelectors.RulesetValidatorSelectorFactory</c>
    /// changes what a whole-profile run selects and what this validator selects and reports in
    /// the same stroke.
    /// </summary>
    private static IValidatorSelector BuildProfileSelector(ValidationProfile profile) =>
        ValidatorOptions.Global.ValidatorSelectors.RulesetValidatorSelectorFactory(profile.ToRuleSetNames());

    /// <summary>
    /// The stock selector for the profile's <see cref="ValidationProfile.ToRuleSetNames"/> list —
    /// the shape the whole-profile entry points produce while the global factory is unreplaced.
    /// The single-rule executor delegates its child-context decisions here.
    /// </summary>
    private static RulesetValidatorSelector BuildWholeProfileSelector(ValidationProfile profile) =>
        new(profile.ToRuleSetNames());

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
