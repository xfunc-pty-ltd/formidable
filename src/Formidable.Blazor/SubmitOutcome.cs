namespace Formidable.Blazor;

/// <summary>The result of a submit: whether it may proceed, the full report, and, for a blocked submit, the names of the errors showing as it finished.</summary>
/// <param name="CanProceed"><see langword="true"/> when the submit landed with a report holding no error and no value was edited while that check ran: an edit during the check makes the submit check again first. <see langword="false"/> for a blocked submit, for one whose check a rule failure ended, for one displaced by a newer submit, a load, or the engine being rebuilt or torn down, and for one edited during each of its three checks, whatever its report holds. Warnings and infos do not block.</param>
/// <param name="Report">The submit profile's full report; empty for a submit whose check a rule failure ended.</param>
/// <param name="VisibleErrorSummary">For a blocked submit, the distinct names of the errors showing as it finished, each named as <see cref="VisibleIssue.DisplayName"/> names it, its own first and then a server reply's still standing on fields its own errors do not name, for a dialog or a summary line; for one whose <see cref="Fault"/> is set, the model-level name its fault message is listed under; empty for a submit that proceeds or was displaced, and for one edited during each of its three checks whose last check found no error.</param>
/// <remarks>Grows by init-only properties, never by constructor parameters, so code constructing it keeps compiling.</remarks>
public sealed record SubmitOutcome(
    bool CanProceed,
    ValidationReport Report,
    IReadOnlyList<string> VisibleErrorSummary)
{
    /// <summary>What the submit's check threw when a rule failed (an <c>HttpClient</c> timeout or a service that is down, say), which blocked the submit; <see langword="null"/> for every other submit, one a newer submit or load had displaced before its rule failed included.</summary>
    /// <remarks>
    /// The form shows <see cref="FormidableOptions.ValidationFaultMessage"/> for it and raises
    /// <see cref="IFormidableEngine.ValidationFaulted"/> with the same exception.
    /// </remarks>
    public Exception? Fault { get; init; }
}
