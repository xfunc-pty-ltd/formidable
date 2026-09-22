namespace Formidable.AspNetCore;

/// <summary>Maps a <see cref="ValidationReport"/> to the 400 wire shape the client reads: errors keyed by property path and advisories under <see cref="AdvisoriesExtensionKey"/>.</summary>
public static class ValidationReportProblemMapper
{
    /// <summary>The ProblemDetails extension key that carries a report's non-error issues: <c>"advisories"</c>.</summary>
    public const string AdvisoriesExtensionKey = "advisories";

    /// <summary>Error messages of <paramref name="report"/> grouped by property path, in report order within each path.</summary>
    /// <param name="report">The report to map.</param>
    /// <returns>One entry per path with an error, keyed case-sensitively; a <see langword="null"/> path reads as <c>""</c> (the model-level path) and a <see langword="null"/> message as <c>""</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="report"/> is <see langword="null"/>.</exception>
    // The null tolerance matches FormidableValidationProblem.ToIssues on the client:
    // IModelValidator<TModel> is a consumer-implementable seam, and a hand-rolled one can hand
    // back a null path or message however the type is annotated.
    public static Dictionary<string, string[]> ToErrorDictionary(ValidationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return report.Errors
            .GroupBy(issue => issue.Path ?? string.Empty)
            .ToDictionary(group => group.Key, group => group.Select(issue => issue.Message ?? string.Empty).ToArray());
    }

    /// <summary>Non-error issues of <paramref name="report"/> as the advisories-extension payload, in report order.</summary>
    /// <param name="report">The report to map.</param>
    /// <returns>One advisory per non-error issue, its severity as the member name; a <see langword="null"/> path or message reads as <c>""</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="report"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An issue carries a <see cref="ValidationSeverity"/> value no member defines; the wire field is a member name, so there is none to write.</exception>
    public static List<ValidationProblemAdvisory> ToAdvisories(ValidationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return report.Advisories
            .Select(issue => new ValidationProblemAdvisory(
                issue.Path ?? string.Empty,
                issue.Message ?? string.Empty,
                SeverityName(issue, nameof(report)),
                issue.Code,
                issue.DisplayName))
            .ToList();
    }

    // Throwing is the only honest answer to an undefined severity: the value's own ToString would
    // put a number on the wire, which the client reads as Warning, relabelling a caller's bug
    // rather than reporting it. Nothing shipped produces one (the FluentValidation adapter maps
    // exhaustively), so the value comes from a cast in a hand-rolled validator, and naming it is
    // the only way its author learns of it.
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
