namespace Formidable;

/// <summary>
/// Rule-level access to a validator: enumerate the rules a <see cref="ValidationProfile"/>
/// selects and execute them one at a time. An optional capability alongside
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
/// An implementation minting its own identities resolves the one
/// <see cref="ValidateRuleAsync"/> hands back by reading <see cref="RuleIdentity.Key"/> — the
/// rule object it wrapped in <see cref="SelectRules"/> — rather than by carrying its own
/// identity-to-rule lookup.
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
    /// Whether this instance can select and execute rules individually — the tester beside
    /// the two doers, which both throw <see cref="NotSupportedException"/> when it is
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
    /// Validates <paramref name="model"/> against exactly one top-level rule under
    /// <paramref name="profile"/>'s selection scope: the rule's children — collection child
    /// rules with their indexed paths (<c>Items[0].Sku</c>), child validators, and
    /// <c>Include()</c> internals — are filtered by the profile's ruleset names exactly as a
    /// whole-profile run filters them. Concatenating every selected rule's
    /// <see cref="RuleLevelResult.Report"/> in <see cref="SelectRules"/> order reproduces the
    /// whole-profile report of the same profile issue for issue. The result's
    /// <see cref="RuleLevelResult.IsProfileScoped"/> says whether that verdict may be reused
    /// across profiles or answers only for <paramref name="profile"/>.
    /// </summary>
    /// <remarks>
    /// <paramref name="rule"/> must come from <see cref="SelectRules"/> on this same
    /// instance; a foreign or default identity throws <see cref="ArgumentException"/> rather
    /// than silently validating nothing, and <see cref="NotSupportedException"/> is thrown
    /// when <see cref="CanValidateByRule"/> is <see langword="false"/>. Each call validates
    /// in its own context, so validator-level hooks run once per rule call —
    /// <c>PreValidate</c> in particular executes for every rule rather than once per pass.
    /// </remarks>
    Task<RuleLevelResult> ValidateRuleAsync(TModel model, ValidationProfile profile,
        RuleIdentity rule, CancellationToken cancellationToken = default);
}
