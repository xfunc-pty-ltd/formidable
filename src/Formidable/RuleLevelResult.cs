namespace Formidable;

/// <summary>The outcome of one <see cref="IRuleLevelValidator{TModel}.ValidateRulesAsync"/> call: its report, and whether the answer holds only for the profile it ran under.</summary>
/// <param name="Report">The issues the rules produced, mapped as validating the whole profile maps them.</param>
/// <param name="IsProfileScoped"><see langword="true"/> when a rule's child scope was filtered by the profile's ruleset names (an <c>Include()</c>'s internals, or a <c>SetValidator</c> child whose rules carry their own memberships), so the report answers for that profile alone; reported per call, never per rule.</param>
/// <remarks>
/// <paramref name="IsProfileScoped"/> being <see langword="false"/> means every profile that
/// selects these rules produces the same report at the same model state. Which rules the report speaks for is the caller's to check: it mixes
/// every member's issues with nothing to attribute them by, so it answers a selection holding the
/// whole set and no other. One scoped member marks the whole call, so a caller wanting the finest
/// signal gives such a rule a call of its own.
/// </remarks>
// Grows by init-only properties, never by constructor parameters, so existing construction keeps
// compiling and binding; any added member folds into the record's synthesized equality.
public readonly record struct RuleLevelResult(ValidationReport Report, bool IsProfileScoped);
