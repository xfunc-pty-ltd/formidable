using System.Collections.Frozen;

namespace Formidable;

/// <summary>Base class for a validator that wraps another, forwarding every member the wrapped validator implements and answering the rest as an absent capability.</summary>
/// <typeparam name="TModel">The model type the validator accepts.</typeparam>
/// <remarks>
/// A wrapper written against <see cref="IModelValidator{TModel}"/> alone validates correctly and
/// presents neither <see cref="IRuleInspectingValidator{TModel}"/> nor
/// <see cref="IRuleLevelValidator{TModel}"/>, and no caller's test can tell that from a validator
/// that never had them. Override <see cref="Validate"/>, <see cref="ValidateAsync"/> and
/// <see cref="ValidateRulesAsync"/> together when changing what is validated: a form that can
/// take the validator rule by rule reaches only the last. To withdraw that capability instead,
/// override <see cref="CanValidateByRule"/> to <see langword="false"/> and the three members
/// that select or run rules to throw <see cref="NotSupportedException"/>.
/// </remarks>
// Deriving is the point, so there is no way to construct the pass-through on its own: forwarding
// everything and changing nothing is what a caller already has when it holds the wrapped
// validator itself.
public abstract class DelegatingModelValidator<TModel>
    : IModelValidator<TModel>, IRuleInspectingValidator<TModel>, IRuleLevelValidator<TModel>
{
    private readonly IModelValidator<TModel> _inner;
    private readonly IRuleInspectingValidator<TModel>? _inspector;
    private readonly IRuleLevelValidator<TModel>? _ruleLevel;

    /// <summary>Wraps <paramref name="inner"/>, resolving which capability interfaces it implements once here; its testers are read on every call.</summary>
    /// <param name="inner">The validator every member of this class forwards to.</param>
    /// <exception cref="ArgumentNullException"><paramref name="inner"/> is <see langword="null"/>.</exception>
    protected DelegatingModelValidator(IModelValidator<TModel> inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        // The interface test is made once because an object's runtime type cannot change over
        // its lifetime; the testers are read per call because a wrapped validator's own answer
        // can, and following it beats freezing it at construction.
        _inspector = inner as IRuleInspectingValidator<TModel>;
        _ruleLevel = inner as IRuleLevelValidator<TModel>;
    }

    /// <summary>Validates <paramref name="model"/> under <paramref name="profile"/> through the wrapped validator.</summary>
    /// <param name="model">The model to validate.</param>
    /// <param name="profile">The rule selection to run.</param>
    /// <param name="cancellationToken">Cancels an async rule still running.</param>
    /// <returns>The wrapped validator's report.</returns>
    public virtual Task<ValidationReport> ValidateAsync(
        TModel model, ValidationProfile profile, CancellationToken cancellationToken = default) =>
        _inner.ValidateAsync(model, profile, cancellationToken);

    /// <summary>Validates <paramref name="model"/> under <paramref name="profile"/> synchronously through the wrapped validator.</summary>
    /// <param name="model">The model to validate.</param>
    /// <param name="profile">The rule selection to run.</param>
    /// <returns>The wrapped validator's report.</returns>
    public virtual ValidationReport Validate(TModel model, ValidationProfile profile) =>
        _inner.Validate(model, profile);

    /// <summary>The wrapped validator's own answer; <see langword="false"/> where it does not implement <see cref="IRuleInspectingValidator{TModel}"/>.</summary>
    public virtual bool CanInspectRules => _inspector?.CanInspectRules ?? false;

    /// <summary>The wrapped validator's answer; <see cref="FieldRequirement.NotRequired"/> for every path where it does not implement <see cref="IRuleInspectingValidator{TModel}"/>.</summary>
    /// <param name="fieldPath">The declared or indexed field path to ask about.</param>
    /// <param name="profile">The rule selection that decides the answer.</param>
    /// <returns>The demand the wrapped validator reports.</returns>
    public virtual FieldRequirement GetFieldRequirement(string fieldPath, ValidationProfile profile) =>
        _inspector is { } inspector
            ? inspector.GetFieldRequirement(fieldPath, profile)
            : FieldRequirement.NotRequired;

    /// <summary>The wrapped validator's answer; empty where it does not implement <see cref="IRuleInspectingValidator{TModel}"/>.</summary>
    /// <param name="profile">The rule selection that decides which rules are read.</param>
    /// <returns>The declared paths the wrapped validator reports.</returns>
    public virtual IReadOnlySet<string> GetDeclaredFieldPaths(ValidationProfile profile) =>
        _inspector is { } inspector
            ? inspector.GetDeclaredFieldPaths(profile)
            : FrozenSet<string>.Empty;

    /// <summary>The wrapped validator's own answer; <see langword="false"/> where it does not implement <see cref="IRuleLevelValidator{TModel}"/>.</summary>
    public virtual bool CanValidateByRule => _ruleLevel?.CanValidateByRule ?? false;

    /// <summary>The wrapped validator's own identities for the rules <paramref name="profile"/> selects, which <see cref="ValidateRulesAsync"/> hands back to it.</summary>
    /// <param name="profile">The rule selection to list.</param>
    /// <returns>The identities, scoped to the wrapped validator instance.</returns>
    /// <exception cref="NotSupportedException"><see cref="CanValidateByRule"/> is <see langword="false"/>: the wrapped validator does not implement <see cref="IRuleLevelValidator{TModel}"/>, or answers <see langword="false"/> itself.</exception>
    public virtual IReadOnlyList<RuleIdentity> SelectRules(ValidationProfile profile) =>
        _ruleLevel is { } ruleLevel
            ? ruleLevel.SelectRules(profile)
            : throw NoRuleLevelCapability();

    /// <summary>Validates <paramref name="model"/> against exactly the given rules through the wrapped validator.</summary>
    /// <param name="model">The model to validate.</param>
    /// <param name="profile">The rule selection whose ruleset names filter child rules.</param>
    /// <param name="rules">Identities from <see cref="SelectRules"/> on this instance.</param>
    /// <param name="cancellationToken">Cancels an async rule still running.</param>
    /// <returns>The wrapped validator's result.</returns>
    /// <exception cref="NotSupportedException"><see cref="CanValidateByRule"/> is <see langword="false"/>: the wrapped validator does not implement <see cref="IRuleLevelValidator{TModel}"/>, or answers <see langword="false"/> itself.</exception>
    public virtual Task<RuleLevelResult> ValidateRulesAsync(
        TModel model, ValidationProfile profile, IReadOnlyList<RuleIdentity> rules,
        CancellationToken cancellationToken = default) =>
        _ruleLevel is { } ruleLevel
            ? ruleLevel.ValidateRulesAsync(model, profile, rules, cancellationToken)
            : throw NoRuleLevelCapability();

    /// <summary>The wrapped validator's own partition of <paramref name="rules"/> into groups no profile splits.</summary>
    /// <param name="rules">Identities from <see cref="SelectRules"/> on this instance.</param>
    /// <returns>The groups the wrapped validator reports.</returns>
    /// <exception cref="NotSupportedException"><see cref="CanValidateByRule"/> is <see langword="false"/>: the wrapped validator does not implement <see cref="IRuleLevelValidator{TModel}"/>, or answers <see langword="false"/> itself.</exception>
    public virtual IReadOnlyList<IReadOnlyList<RuleIdentity>> GroupBySelectionClass(
        IReadOnlyList<RuleIdentity> rules) =>
        // The wrapped validator's own partition is forwarded rather than coarsened: a coarser
        // answer of the wrapper's own would cost the reuse of the wrapped validator's answers
        // without changing a single verdict, so nothing would report the loss.
        _ruleLevel is { } ruleLevel
            ? ruleLevel.GroupBySelectionClass(rules)
            : throw NoRuleLevelCapability();

    private NotSupportedException NoRuleLevelCapability() =>
        new($"'{FriendlyTypeName.Of(_inner.GetType())}' does not implement IRuleLevelValidator<{FriendlyTypeName.Of(typeof(TModel))}>, " +
            $"so the rules it holds cannot be selected or run apart from a whole-profile pass. " +
            $"Check {nameof(CanValidateByRule)} first.");
}
