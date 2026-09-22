namespace Formidable.Blazor;

/// <summary>The result of running the submit pipeline.</summary>
/// <remarks>Grows by init-only properties, never by constructor parameters, so existing
/// construction keeps compiling and binding; any added member folds into the record's
/// synthesized equality.</remarks>
/// <param name="CanProceed">True when no error-severity issues exist (warnings do not block).</param>
/// <param name="Report">The full validation report from the submit profile.</param>
/// <param name="VisibleErrorSummary">Distinct display names of the errors shown to the user — dialog/summary fodder.</param>
public sealed record SubmitOutcome(
    bool CanProceed,
    ValidationReport Report,
    IReadOnlyList<string> VisibleErrorSummary);
