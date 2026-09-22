using System.Collections.Frozen;

namespace Formidable;

/// <summary>
/// Base class for a validator that wraps another one. Every member forwards to the validator
/// handed to the constructor and every capability tester answers what that validator answers, so
/// a derived class writes the members whose behaviour it changes and everything else goes on
/// reaching a caller unchanged.
/// </summary>
/// <remarks>
/// <para>
/// The loss this base exists to prevent. <see cref="IModelValidator{TModel}"/> is the seam a
/// caller validates through, and two optional capabilities sit beside it —
/// <see cref="IRuleInspectingValidator{TModel}"/> and <see cref="IRuleLevelValidator{TModel}"/>,
/// both of which <see cref="FluentValidationModelValidator{TModel}"/> implements. A wrapper
/// written against <see cref="IModelValidator{TModel}"/> alone compiles, validates correctly and
/// presents neither capability; a caller's capability test then reads exactly what it reads for a
/// validator that genuinely cannot read its own rules or run a chosen set of them on its own.
/// Reporting the difference falls to the caller, and Formidable's own form engine does it for one
/// half: a form whose validator cannot report its rules writes one line when its engine is built,
/// naming what will not render. That line names the state rather than the mistake, since the two
/// read alike, and nothing reports the rule-level half.
/// </para>
/// <para>
/// Three things go missing with those two capabilities: the per-field requirement a form draws its
/// marker and its announcement from, which the rules cannot be read to answer, so it reads
/// <see cref="FieldRequirement.NotRequired"/> wherever nothing else declares it; the half of a
/// draft load that confirms values the rules pass, which needs the declared-path list and has no
/// other source, leaving a loaded form silent about the values it holds while still disclosing the
/// wrong ones; and the per-rule verdicts a live pass, a refresh and a form-validity probe otherwise
/// share, which is both a cost — each of those passes evaluates the whole profile for itself —
/// and, where nothing but a live pass has yet answered under the submit profile, the difference
/// between a field a form can vouch for and one it cannot.
/// </para>
/// <para>
/// A capability member forwards only where the wrapped validator implements the interface
/// declaring it. Where it does not, the member answers as that interface documents for a validator
/// without the capability, which is a different answer for each of the two:
/// <see cref="CanInspectRules"/> reads <see langword="false"/> and both inspection readers report
/// the empty answer, since an inspection answer decorates a form rather than deciding a verdict;
/// <see cref="CanValidateByRule"/> reads <see langword="false"/> and every rule-level member
/// throws <see cref="NotSupportedException"/>, since running part of a profile and reporting it as
/// the whole would under-validate in silence.
/// </para>
/// <para>
/// A derived class that changes WHAT is validated changes it at every entry point that validates.
/// Three members take a model — <see cref="Validate"/>, <see cref="ValidateAsync"/> and
/// <see cref="ValidateRulesAsync"/> — and which of them runs is the caller's choice: a caller that
/// finds <see cref="CanValidateByRule"/> <see langword="true"/> may validate entirely through
/// <see cref="ValidateRulesAsync"/>, a set of rules at a time, reaching neither of the other two.
/// Formidable's own form engine is such a caller and takes that path without exception: every pass
/// it runs, and the form-validity probe beside them, go through it wherever the capability is
/// present. So a subclass overriding <see cref="ValidateAsync"/> alone leaves that engine
/// validating what the wrapped validator would have validated, and breaks the equivalence
/// <see cref="IRuleLevelValidator{TModel}.ValidateRulesAsync"/> promises between the rules run in
/// sets and the profile run whole. Overriding all three gives one answer whoever asks. The other
/// consistent choice is to withdraw the capability, which takes four overrides rather than one:
/// <see cref="CanValidateByRule"/> to <see langword="false"/>, and the three doers to throw
/// <see cref="NotSupportedException"/>, since <see cref="IRuleLevelValidator{TModel}"/> holds a
/// tester and its doers to one answer. Withdrawing costs the per-rule sharing.
/// </para>
/// <para>
/// Deriving is the point, so there is no way to construct the pass-through on its own: forwarding
/// everything and changing nothing is what a caller already has when it holds the wrapped
/// validator itself.
/// </para>
/// </remarks>
public abstract class DelegatingModelValidator<TModel>
    : IModelValidator<TModel>, IRuleInspectingValidator<TModel>, IRuleLevelValidator<TModel>
{
    private readonly IModelValidator<TModel> _inner;
    private readonly IRuleInspectingValidator<TModel>? _inspector;
    private readonly IRuleLevelValidator<TModel>? _ruleLevel;

    /// <summary>
    /// Wraps <paramref name="inner"/>. Which capability interfaces it implements is resolved
    /// once, here — an object's runtime type cannot change over its lifetime, so that answer
    /// cannot go stale — while each capability's own testers (<see cref="CanInspectRules"/>,
    /// <see cref="CanValidateByRule"/>) are read at every call, so a wrapped validator whose own
    /// testers change answer over its lifetime is followed rather than frozen at construction.
    /// </summary>
    /// <param name="inner">The validator every member of this class forwards to.</param>
    protected DelegatingModelValidator(IModelValidator<TModel> inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        _inspector = inner as IRuleInspectingValidator<TModel>;
        _ruleLevel = inner as IRuleLevelValidator<TModel>;
    }

    /// <inheritdoc />
    public virtual Task<ValidationReport> ValidateAsync(
        TModel model, ValidationProfile profile, CancellationToken cancellationToken = default) =>
        _inner.ValidateAsync(model, profile, cancellationToken);

    /// <inheritdoc />
    public virtual ValidationReport Validate(TModel model, ValidationProfile profile) =>
        _inner.Validate(model, profile);

    /// <inheritdoc />
    /// <remarks>
    /// The wrapped validator's own answer, and <see langword="false"/> where it does not implement
    /// <see cref="IRuleInspectingValidator{TModel}"/>.
    /// </remarks>
    public virtual bool CanInspectRules => _inspector?.CanInspectRules ?? false;

    /// <inheritdoc />
    /// <remarks>
    /// Where the wrapped validator does not implement
    /// <see cref="IRuleInspectingValidator{TModel}"/>, <see cref="FieldRequirement.NotRequired"/>
    /// for every path — the answer <see cref="CanInspectRules"/> reading <see langword="false"/>
    /// promises.
    /// </remarks>
    public virtual FieldRequirement GetFieldRequirement(string fieldPath, ValidationProfile profile) =>
        _inspector is { } inspector
            ? inspector.GetFieldRequirement(fieldPath, profile)
            : FieldRequirement.NotRequired;

    /// <inheritdoc />
    /// <remarks>
    /// Empty where the wrapped validator does not implement
    /// <see cref="IRuleInspectingValidator{TModel}"/> — the answer
    /// <see cref="CanInspectRules"/> reading <see langword="false"/> promises.
    /// </remarks>
    public virtual IReadOnlySet<string> GetDeclaredFieldPaths(ValidationProfile profile) =>
        _inspector is { } inspector
            ? inspector.GetDeclaredFieldPaths(profile)
            : FrozenSet<string>.Empty;

    /// <inheritdoc />
    /// <remarks>
    /// The wrapped validator's own answer, and <see langword="false"/> where it does not implement
    /// <see cref="IRuleLevelValidator{TModel}"/>.
    /// </remarks>
    public virtual bool CanValidateByRule => _ruleLevel?.CanValidateByRule ?? false;

    /// <inheritdoc />
    /// <remarks>
    /// The identities are the wrapped validator's own, and
    /// <see cref="ValidateRulesAsync(TModel, ValidationProfile, IReadOnlyList{RuleIdentity}, CancellationToken)"/>
    /// hands them back to it, so the instance scoping <see cref="RuleIdentity"/> describes holds
    /// across the wrapper. Throws <see cref="NotSupportedException"/> where the wrapped validator
    /// does not implement <see cref="IRuleLevelValidator{TModel}"/>.
    /// </remarks>
    public virtual IReadOnlyList<RuleIdentity> SelectRules(ValidationProfile profile) =>
        _ruleLevel is { } ruleLevel
            ? ruleLevel.SelectRules(profile)
            : throw NoRuleLevelCapability();

    /// <inheritdoc />
    /// <remarks>
    /// Throws <see cref="NotSupportedException"/> where the wrapped validator does not implement
    /// <see cref="IRuleLevelValidator{TModel}"/>.
    /// </remarks>
    public virtual Task<RuleLevelResult> ValidateRulesAsync(
        TModel model, ValidationProfile profile, IReadOnlyList<RuleIdentity> rules,
        CancellationToken cancellationToken = default) =>
        _ruleLevel is { } ruleLevel
            ? ruleLevel.ValidateRulesAsync(model, profile, rules, cancellationToken)
            : throw NoRuleLevelCapability();

    /// <inheritdoc />
    /// <remarks>
    /// The wrapped validator's own partition, which is what keeps a wrapper's verdicts as
    /// reusable as the wrapped validator's are: a coarser answer of the wrapper's own would cost
    /// the reuse without changing a single verdict, so nothing would report the loss. Throws
    /// <see cref="NotSupportedException"/> where the wrapped validator does not implement
    /// <see cref="IRuleLevelValidator{TModel}"/>.
    /// </remarks>
    public virtual IReadOnlyList<IReadOnlyList<RuleIdentity>> GroupBySelectionClass(
        IReadOnlyList<RuleIdentity> rules) =>
        _ruleLevel is { } ruleLevel
            ? ruleLevel.GroupBySelectionClass(rules)
            : throw NoRuleLevelCapability();

    private NotSupportedException NoRuleLevelCapability() =>
        new($"'{FriendlyTypeName.Of(_inner.GetType())}' does not implement IRuleLevelValidator<{FriendlyTypeName.Of(typeof(TModel))}>, " +
            $"so the rules it holds cannot be selected or run apart from a whole-profile pass. " +
            $"Check {nameof(CanValidateByRule)} first.");
}
