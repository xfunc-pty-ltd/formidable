namespace Formidable;

/// <summary>
/// Rule-level access to a validator: enumerate the rules a <see cref="ValidationProfile"/>
/// selects and execute a chosen set of them in one pass. An optional capability alongside
/// <see cref="IModelValidator{TModel}"/>, implemented by
/// <see cref="FluentValidationModelValidator{TModel}"/>. An engine's capability test is
/// <c>validator is IRuleLevelValidator&lt;TModel&gt; ruleLevel &amp;&amp;
/// ruleLevel.CanValidateByRule</c> — never the type test alone — falling back to whole-profile
/// validation when the test fails.
/// </summary>
/// <remarks>
/// There is deliberately no synchronous variant: the consuming engines are async, and a rule
/// with async components could not honour one.
/// <para>
/// An implementation minting its own identities resolves the ones
/// <see cref="ValidateRulesAsync"/> hands back by reading <see cref="RuleIdentity.Key"/> — the
/// rule object it wrapped in <see cref="SelectRules"/> — rather than by carrying its own
/// identity-to-rule lookup.
/// </para>
/// <para>
/// A validator wrapping another one forwards this capability by deriving from
/// <see cref="DelegatingModelValidator{TModel}"/>, whose tester answers the wrapped validator's
/// own answer. A wrapper implementing <see cref="IModelValidator{TModel}"/> alone presents no
/// capability at all, which a caller's test reads exactly as it reads a validator that never had
/// one.
/// </para>
/// <para>
/// Implementing this interface is supported surface, and it grows accordingly: a member added
/// after v1 carries a default implementation matching this interface's own posture for absent
/// capability — a new tester reads <see langword="false"/>, and a new doer throws
/// <see cref="NotSupportedException"/> rather than silently under-validating — so a caller
/// routes around an implementation that does not override the addition exactly as it routes
/// around <see cref="CanValidateByRule"/> being <see langword="false"/>.
/// </para>
/// </remarks>
public interface IRuleLevelValidator<in TModel>
{
    /// <summary>
    /// Whether this instance can select rules and execute a chosen set of them — the tester
    /// beside the doers, which throw <see cref="NotSupportedException"/> when it is
    /// <see langword="false"/> rather than silently under-validate. Callers check it once and
    /// route capability-less validators to whole-profile validation.
    /// </summary>
    bool CanValidateByRule { get; }

    /// <summary>
    /// Returns identities for exactly the rules <paramref name="profile"/> selects, in
    /// declaration order, matching what whole-profile validation of the same profile
    /// executes: rules outside any ruleset when
    /// <see cref="ValidationProfile.IncludeDefaultRules"/> is set (or the profile names the
    /// literal <c>"default"</c> ruleset); every ruleset-tagged rule whose membership
    /// intersects <see cref="ValidationProfile.RuleSets"/>, case-insensitively — a rule
    /// declared into several rulesets at once is selected through any of them; every rule
    /// when the profile names the wildcard ruleset <c>"*"</c>; and an untagged
    /// <c>Include()</c> rule always, so its included rules can be filtered individually at
    /// execution — a tagged one is admitted on membership like any other rule. Ruleset names
    /// are verified first where the underlying validator supports it, so a typo'd name throws
    /// exactly as whole-profile validation does.
    /// </summary>
    /// <remarks>
    /// The identities are opaque to the caller and validator-instance-scoped — see
    /// <see cref="RuleIdentity"/>. Throws <see cref="NotSupportedException"/> when
    /// <see cref="CanValidateByRule"/> is <see langword="false"/>.
    /// </remarks>
    IReadOnlyList<RuleIdentity> SelectRules(ValidationProfile profile);

    /// <summary>
    /// Validates <paramref name="model"/> against exactly the given top-level rules under
    /// <paramref name="profile"/>'s selection scope, in ONE pass over the validator: their
    /// children — collection child rules with their indexed paths (<c>Items[0].Sku</c>), child
    /// validators, and <c>Include()</c> internals — are filtered by the profile's ruleset names
    /// exactly as a whole-profile run filters them. A call carrying the profile's whole
    /// selection reproduces the whole-profile report of the same profile issue for issue.
    /// Splitting that selection across several calls moves no issue and drops none: each call's
    /// report carries its own rules' issues in <see cref="SelectRules"/> order, so concatenating
    /// the reports reproduces the whole-profile report's issues ordered by call rather than by
    /// declaration. The result's <see cref="RuleLevelResult.IsProfileScoped"/> says whether the
    /// verdict may be reused across profiles or answers only for <paramref name="profile"/>.
    /// </summary>
    /// <remarks>
    /// Every identity in <paramref name="rules"/> must come from <see cref="SelectRules"/> on
    /// this same instance; a foreign or default identity throws
    /// <see cref="ArgumentException"/> rather than silently validating nothing, and
    /// <see cref="NotSupportedException"/> is thrown when <see cref="CanValidateByRule"/> is
    /// <see langword="false"/>. An empty set is a legal request and answers with an empty
    /// report. Each call validates in its own context, so validator-level hooks run once per
    /// call — <c>PreValidate</c> in particular executes for every call rather than for every
    /// rule.
    /// </remarks>
    Task<RuleLevelResult> ValidateRulesAsync(TModel model, ValidationProfile profile,
        IReadOnlyList<RuleIdentity> rules, CancellationToken cancellationToken = default);

    /// <summary>
    /// Partitions <paramref name="rules"/> into groups no <see cref="ValidationProfile"/> can
    /// split: every profile either selects a whole group or none of it. A caller holding one
    /// verdict per executed SET can then serve a stored group to a pass running any profile
    /// whose own selection contains it, which is what keeps verdict reuse independent of the
    /// order two profiles' passes happen to land in.
    /// </summary>
    /// <remarks>
    /// The groups COVER <paramref name="rules"/> exactly: every identity appears in exactly one
    /// group, none is dropped and none is repeated. A caller runs the groups and nothing else,
    /// so a dropped identity is a rule that silently never executes, and a repeated one is a set
    /// of issues reported twice.
    /// <para>
    /// An implementation with no partition to offer returns one group per rule. That answer
    /// satisfies both properties under every profile and gives up only speed: it costs one
    /// validation call per rule, which is the cost taking a set at a time exists to avoid.
    /// </para>
    /// <para>
    /// The one group holding every rule is the answer to be careful with. It covers the input,
    /// but it is a partition no profile can split only while the whole set is selected by the
    /// same profiles — two rules that differ in what admits them belong in different groups, or
    /// the group is servable to fewer selections than the rules in it deserve. What that costs is
    /// measured on the pass: a group a pass's own selection does not contain is a group that pass
    /// executes in full, and so does every later pass selecting the same way, where a group the
    /// selection contains is served from the caller's stored verdict instead. Sharper still:
    /// <see cref="RuleLevelResult.IsProfileScoped"/> is recorded for the CALL rather than for
    /// any one rule within it, so a single rule whose scope reaches a child validator marks the
    /// whole group, and every rule filed beside it is re-run whenever the profile changes. A set
    /// holding no such rule is free of that; one that holds any is better off giving it a group
    /// of its own. Both properties survive splitting FINER than selection demands, since no
    /// profile can split a group of one either.
    /// </para>
    /// <para>
    /// Throws <see cref="NotSupportedException"/> when <see cref="CanValidateByRule"/> is
    /// <see langword="false"/>.
    /// </para>
    /// </remarks>
    IReadOnlyList<IReadOnlyList<RuleIdentity>> GroupBySelectionClass(IReadOnlyList<RuleIdentity> rules);
}
