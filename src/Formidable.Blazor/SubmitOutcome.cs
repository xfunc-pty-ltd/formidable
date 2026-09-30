namespace Formidable.Blazor;

/// <summary>The result of a submit: whether it may proceed, the full report, and, for a blocked submit, the names of the errors showing as it finished.</summary>
/// <param name="CanProceed"><see langword="true"/> when the submit landed with a report holding no error; <see langword="false"/> for a blocked submit, and for one displaced by a newer submit, a load, or the engine being rebuilt or torn down, whatever its report holds. Warnings and infos do not block.</param>
/// <param name="Report">The submit profile's full report.</param>
/// <param name="VisibleErrorSummary">For a blocked submit, the distinct names of the errors showing as it finished, each named as <see cref="VisibleIssue.DisplayName"/> names it, its own first and then a server reply's still standing on fields its own errors do not name, for a dialog or a summary line; empty for a submit that proceeds or was displaced.</param>
/// <remarks>Grows by init-only properties, never by constructor parameters, so code constructing it keeps compiling.</remarks>
public sealed record SubmitOutcome(
    bool CanProceed,
    ValidationReport Report,
    IReadOnlyList<string> VisibleErrorSummary);
