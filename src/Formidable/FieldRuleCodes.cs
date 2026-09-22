namespace Formidable;

/// <summary>
/// One field's error codes, partitioned by whether the rule component that produces them
/// checks for presence. It maps a failure back to its kind: an issue whose
/// <see cref="ValidationIssue.Code"/> is in <see cref="PresenceCodes"/> reports an empty
/// field; any other code reports that the value the field holds is wrong. Produced by
/// <see cref="IRuleInspectingValidator{TModel}.GetFieldRuleCodes"/>.
/// </summary>
/// <remarks>
/// Codes are the ones FluentValidation puts on the failure — the configured
/// <c>WithErrorCode</c> where there is one, otherwise the default code for the component's
/// validator — and are compared ordinally. Partitioning is per field, so two fields reusing
/// one code for different purposes never interfere. Severity is not represented: a code says
/// what a component checks, never how loudly it complains, so a caller that must ignore
/// advisories filters the issues by severity itself.
/// </remarks>
/// <param name="PresenceCodes">
/// Codes produced only by this field's presence components — <c>NotEmpty()</c> and
/// <c>NotNull()</c>.
/// </param>
/// <param name="OtherCodes">
/// Codes produced only by this field's components that check something other than presence.
/// </param>
/// <param name="AmbiguousCodes">
/// Codes shared by this field's presence and non-presence components — reachable by giving one
/// <c>WithErrorCode</c> to both. Such a code identifies nothing, so it is listed here and in
/// neither of the other two sets: a caller testing <see cref="PresenceCodes"/> alone treats it
/// as "not presence", which surfaces the failure rather than hiding it, and this set is what
/// makes the ambiguity visible instead of guessed.
/// </param>
public sealed record FieldRuleCodes(
    IReadOnlySet<string> PresenceCodes,
    IReadOnlySet<string> OtherCodes,
    IReadOnlySet<string> AmbiguousCodes);
