namespace Formidable;

/// <summary>
/// The outcome of validating one rule via
/// <see cref="IRuleLevelValidator{TModel}.ValidateRuleAsync"/>: the rule's report, plus how
/// far that verdict can be reused.
/// </summary>
/// <remarks>Grows by init-only properties, never by constructor parameters, so existing
/// construction keeps compiling and binding; any added member folds into the record's
/// synthesized equality.</remarks>
/// <param name="Report">The issues the rule produced, mapped exactly as whole-profile
/// validation maps them.</param>
/// <param name="IsProfileScoped">
/// <see langword="false"/> when the verdict is profile-independent: execution consulted no
/// child-scope decision that could differ across profiles, so every profile that selects the
/// rule produces this same report at the same model state — a verdict store may serve it to a
/// pass running any profile. <see langword="true"/> when execution filtered the rule's child
/// scope by ruleset tags — an <c>Include()</c>'s internals, or a <c>SetValidator</c> child
/// validator whose rules carry their own ruleset memberships — so the report answers only for
/// the profile it ran under; a store must not serve it to a pass running a different profile.
/// Untagged child scopes under untagged rules, and <c>ChildRules</c> children (which carry
/// their parent declaration scope's propagated tags), never scope a verdict.
/// </param>
public readonly record struct RuleLevelResult(ValidationReport Report, bool IsProfileScoped);
