namespace Formidable;

/// <summary>
/// The outcome of validating a set of rules via
/// <see cref="IRuleLevelValidator{TModel}.ValidateRulesAsync"/>: their report, plus how far
/// that verdict can be reused.
/// </summary>
/// <remarks>Grows by init-only properties, never by constructor parameters, so existing
/// construction keeps compiling and binding; any added member folds into the record's
/// synthesized equality.</remarks>
/// <param name="Report">The issues the rules produced, mapped exactly as whole-profile
/// validation maps them.</param>
/// <param name="IsProfileScoped">
/// <see langword="false"/> when the verdict is profile-independent: execution consulted no
/// child-scope decision that could differ across profiles, so every profile that selects the
/// rules produces this same report at the same model state — the profile a later pass runs is
/// then no bar to serving it. Which rules the verdict speaks for is a separate condition, and
/// it is the caller's to check: the report mixes every member's issues together with nothing to
/// attribute them by, so it answers a selection holding the whole set and no other.
/// <see langword="true"/> when execution filtered a rule's child scope by ruleset tags — an
/// <c>Include()</c>'s internals, or a <c>SetValidator</c> child validator whose rules carry
/// their own ruleset memberships — so the report answers only for the profile it ran under; a
/// store must not serve it to a pass running a different profile. Untagged child scopes under
/// untagged rules, and <c>ChildRules</c> children (which carry their parent declaration scope's
/// propagated tags), never scope a verdict. The signal belongs to the CALL rather than to any
/// rule within it: one member's scoped decision raises it for everything the call answered for,
/// which is why a caller wanting the finest signal gives such a rule a call of its own.
/// </param>
public readonly record struct RuleLevelResult(ValidationReport Report, bool IsProfileScoped);
