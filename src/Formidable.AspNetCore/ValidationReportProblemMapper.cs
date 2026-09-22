namespace Formidable.AspNetCore;

/// <summary>
/// Maps a <see cref="ValidationReport"/> to the wire shape shared with the client:
/// error messages keyed by property path plus non-error issues for the
/// <see cref="AdvisoriesExtensionKey"/> ProblemDetails extension.
/// </summary>
public static class ValidationReportProblemMapper
{
    /// <summary>The ProblemDetails extension key carrying non-error issues.</summary>
    public const string AdvisoriesExtensionKey = "advisories";

    /// <summary>
    /// Error messages grouped by path, preserving issue order within each path. A
    /// <see langword="null"/> path reads as <c>""</c>, the model-level path, and a
    /// <see langword="null"/> message as <c>""</c> — the same tolerance
    /// <see cref="FormidableValidationProblem.ToIssues"/> applies on the client, because
    /// <see cref="IModelValidator{TModel}"/> is a consumer-implementable seam and a hand-rolled
    /// one can hand back either however the type is annotated.
    /// </summary>
    public static Dictionary<string, string[]> ToErrorDictionary(ValidationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return report.Errors
            .GroupBy(issue => issue.Path ?? string.Empty)
            .ToDictionary(group => group.Key, group => group.Select(issue => issue.Message ?? string.Empty).ToArray());
    }

    /// <summary>
    /// Non-error issues as the advisories-extension payload, in issue order. Null paths and
    /// messages are tolerated exactly as in <see cref="ToErrorDictionary"/>.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// An issue carries a <see cref="ValidationSeverity"/> value no member defines. The wire
    /// field is a member NAME, so there is nothing honest to write: the value's own
    /// <c>ToString</c> would put a number there, which the client reads as
    /// <see cref="ValidationSeverity.Warning"/> — relabelling a caller's bug rather than
    /// reporting it. Nothing shipped can produce one (the FluentValidation adapter maps
    /// exhaustively), so the value comes from a cast in a hand-rolled validator, and naming it
    /// is the only way its author learns of it.
    /// </exception>
    public static List<ValidationProblemAdvisory> ToAdvisories(ValidationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return report.Issues
            .Where(issue => issue.Severity != ValidationSeverity.Error)
            .Select(issue => new ValidationProblemAdvisory(
                issue.Path ?? string.Empty,
                issue.Message ?? string.Empty,
                SeverityName(issue, nameof(report)),
                issue.Code,
                issue.DisplayName))
            .ToList();
    }

    private static string SeverityName(ValidationIssue issue, string parameterName) =>
        Enum.IsDefined(issue.Severity)
            ? issue.Severity.ToString()
            : throw new ArgumentException(
                $"Issue '{issue.Path}' carries severity {(int)issue.Severity}, which no ValidationSeverity member " +
                "defines. The advisories extension carries a member name, so there is no name to write — give the " +
                "issue Error, Warning or Info.",
                parameterName);

    // The ProblemDetails extensions dictionary for `report`, keyed under
    // AdvisoriesExtensionKey -- or null when there are no advisories to carry, so a caller can
    // attach it only when non-empty rather than repeating that count check itself. Both server
    // adapters (the minimal-API filter and the MVC action filter) share this one step, so
    // ToAdvisories' rejection of an undefined severity is a rejection on both: a report carrying
    // one fails the request it was built for, whichever adapter is serving it.
    internal static Dictionary<string, object?>? ToAdvisoriesExtensions(ValidationReport report)
    {
        var advisories = ToAdvisories(report);
        return advisories.Count > 0
            ? new Dictionary<string, object?> { [AdvisoriesExtensionKey] = advisories }
            : null;
    }
}
