using System.Collections.Frozen;

namespace Formidable;

/// <summary>Base class for a validator that wraps another, forwarding every member the wrapped validator implements and answering the rest as an absent capability.</summary>
/// <typeparam name="TModel">The model type the validator accepts.</typeparam>
/// <remarks>
/// A wrapper written against <see cref="IModelValidator{TModel}"/> alone validates correctly but
/// presents neither capability, and no caller's test can tell it from one that never had them.
/// Override <see cref="Validate"/>, <see cref="ValidateAsync"/> and
/// <see cref="ValidateRulesAsync"/> together to change what is validated, and a form keeps
/// checking rule by rule. Override <see cref="ValidateAsync"/> alone and a form validates the
/// whole profile through it; a validator not derived from this class answers for itself.
/// To withdraw the capability, override <see cref="CanValidateByRule"/> to
/// <see langword="false"/> and the three members that select or run rules to throw
/// <see cref="NotSupportedException"/>.
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

    // ValidateAsyncOverriddenAlone's answer, as RememberedAnswer keeps it.
    private int _validateAsyncOverriddenAlone;

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

    /// <summary>The wrapped validator's own answer; <see langword="false"/> where it does not implement <see cref="IRuleLevelValidator{TModel}"/>, or where this class overrides <see cref="ValidateAsync"/> and not <see cref="ValidateRulesAsync"/>.</summary>
    /// <remarks>
    /// A form that can take a validator rule by rule calls <see cref="ValidateRulesAsync"/> and
    /// never <see cref="ValidateAsync"/>. So where the <see cref="ValidateAsync"/> override is
    /// declared in a class that has no <see cref="ValidateRulesAsync"/> override of its own or
    /// below it, this answers <see langword="false"/> and a form validates through that override.
    /// An override of <see cref="Validate"/> alone changes nothing here: no form calls it.
    /// </remarks>
    public virtual bool CanValidateByRule => (_ruleLevel?.CanValidateByRule ?? false) && !ValidateAsyncOverriddenAlone();

    /// <summary>Whether a form taking this validator rule by rule would miss its <see cref="ValidateAsync"/> override; worked out on the first ask and kept.</summary>
    /// <returns><see langword="true"/> when the class declaring the <see cref="ValidateAsync"/> a call reaches derives from the class declaring the <see cref="ValidateRulesAsync"/> a call reaches.</returns>
    // Kept because the answer belongs to this instance's class, which never changes, while the
    // capability is read at every field's class computation.
    private bool ValidateAsyncOverriddenAlone() =>
        RememberedAnswer.Read(ref _validateAsyncOverriddenAlone, this, static self => self.EntryPointsDisagree());

    // Each entry point is bound as a C# method group on this instance. A method group on a
    // virtual method resolves the override a call reaches, so each delegate's Method names the
    // class that declares the method a form would run; nothing is looked up by name, so trimming
    // has nothing to keep. A ValidateRulesAsync override in the same class as the ValidateAsync
    // override, or in a class below it, was written knowing that override, so only a ValidateAsync
    // declared further down than the ValidateRulesAsync in use withdraws the capability.
    private bool EntryPointsDisagree()
    {
        Func<TModel, ValidationProfile, CancellationToken, Task<ValidationReport>> validate = ValidateAsync;
        var validateOwner = validate.Method.DeclaringType;
        if (validateOwner is null || validateOwner == typeof(DelegatingModelValidator<TModel>))
        {
            return false;
        }

        Func<TModel, ValidationProfile, IReadOnlyList<RuleIdentity>, CancellationToken, Task<RuleLevelResult>> byRule =
            ValidateRulesAsync;
        var byRuleOwner = byRule.Method.DeclaringType;

        // By here validateOwner derives from this class, so a ValidateRulesAsync this class
        // declares is one the subclass test finds above it.
        return byRuleOwner is not null && validateOwner.IsSubclassOf(byRuleOwner);
    }

    /// <summary>The wrapped validator's own identities for the rules <paramref name="profile"/> selects, which <see cref="ValidateRulesAsync"/> hands back to it.</summary>
    /// <param name="profile">The rule selection to list.</param>
    /// <returns>The identities, scoped to the wrapped validator instance.</returns>
    /// <exception cref="NotSupportedException"><see cref="CanValidateByRule"/> is <see langword="false"/>: the wrapped validator does not implement <see cref="IRuleLevelValidator{TModel}"/>, or answers <see langword="false"/> itself, or this class overrides <see cref="ValidateAsync"/> and not <see cref="ValidateRulesAsync"/>.</exception>
    public virtual IReadOnlyList<RuleIdentity> SelectRules(ValidationProfile profile) =>
        RuleLevel().SelectRules(profile);

    /// <summary>Validates <paramref name="model"/> against exactly the given rules through the wrapped validator.</summary>
    /// <param name="model">The model to validate.</param>
    /// <param name="profile">The rule selection whose ruleset names filter child rules.</param>
    /// <param name="rules">Identities from <see cref="SelectRules"/> on this instance.</param>
    /// <param name="cancellationToken">Cancels an async rule still running.</param>
    /// <returns>The wrapped validator's result.</returns>
    /// <exception cref="NotSupportedException"><see cref="CanValidateByRule"/> is <see langword="false"/>: the wrapped validator does not implement <see cref="IRuleLevelValidator{TModel}"/>, or answers <see langword="false"/> itself, or this class overrides <see cref="ValidateAsync"/> and not <see cref="ValidateRulesAsync"/>.</exception>
    public virtual Task<RuleLevelResult> ValidateRulesAsync(
        TModel model, ValidationProfile profile, IReadOnlyList<RuleIdentity> rules,
        CancellationToken cancellationToken = default) =>
        RuleLevel().ValidateRulesAsync(model, profile, rules, cancellationToken);

    /// <summary>The wrapped validator's own partition of <paramref name="rules"/> into groups no profile splits.</summary>
    /// <param name="rules">Identities from <see cref="SelectRules"/> on this instance.</param>
    /// <returns>The groups the wrapped validator reports.</returns>
    /// <exception cref="NotSupportedException"><see cref="CanValidateByRule"/> is <see langword="false"/>: the wrapped validator does not implement <see cref="IRuleLevelValidator{TModel}"/>, or answers <see langword="false"/> itself, or this class overrides <see cref="ValidateAsync"/> and not <see cref="ValidateRulesAsync"/>.</exception>
    public virtual IReadOnlyList<IReadOnlyList<RuleIdentity>> GroupBySelectionClass(
        IReadOnlyList<RuleIdentity> rules) =>
        // The wrapped validator's own partition is forwarded rather than coarsened: a coarser
        // answer of the wrapper's own would cost the reuse of the wrapped validator's answers
        // without changing a single verdict, so nothing would report the loss.
        RuleLevel().GroupBySelectionClass(rules);

    /// <summary>The wrapped validator, for the members that select or run rules one set at a time.</summary>
    /// <returns>The wrapped validator, as the interface.</returns>
    /// <exception cref="NotSupportedException">The wrapped validator does not implement the interface, or this class overrides <see cref="ValidateAsync"/> and not <see cref="ValidateRulesAsync"/>.</exception>
    // A wrapped validator that answers false itself is left to refuse, since its own contract
    // promises that and its message names its own reason.
    private IRuleLevelValidator<TModel> RuleLevel()
    {
        if (_ruleLevel is not { } ruleLevel)
        {
            throw NoRuleLevelCapability();
        }

        if (ValidateAsyncOverriddenAlone())
        {
            throw FormidableConfigurationError.Mark(new NotSupportedException(
                $"'{FriendlyTypeName.Of(GetType())}' overrides ValidateAsync and not ValidateRulesAsync, so a form " +
                "taking its rules one set at a time would never run that override. Override ValidateRulesAsync " +
                $"as well to keep that path. Check {nameof(CanValidateByRule)} first."));
        }

        return ruleLevel;
    }

    private NotSupportedException NoRuleLevelCapability() =>
        FormidableConfigurationError.Mark(new NotSupportedException(
            $"'{FriendlyTypeName.Of(_inner.GetType())}' does not implement IRuleLevelValidator<{FriendlyTypeName.Of(typeof(TModel))}>, " +
            $"so the rules it holds cannot be selected or run apart from a whole-profile pass. " +
            $"Check {nameof(CanValidateByRule)} first."));
}
