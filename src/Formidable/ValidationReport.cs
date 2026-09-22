namespace Formidable;

/// <summary>The outcome of validating a model with a <see cref="ValidationProfile"/>.</summary>
public sealed class ValidationReport
{
    private static readonly IReadOnlyList<ValidationIssue> NoIssues = [];

    /// <summary>A shared valid, issue-free report.</summary>
    public static ValidationReport Empty { get; } = new([]);

    /// <summary>Creates a report from the given issues.</summary>
    /// <remarks>
    /// The report keeps the caller's list itself — <see cref="Issues"/> returns exactly what
    /// was handed in — while the severity views partition it here, in one pass: an issue added
    /// to or removed from that list afterwards shows through <see cref="Issues"/>, never
    /// through a view, and never through <see cref="IsValid"/>, which answers from
    /// <see cref="Errors"/>.
    /// </remarks>
    public ValidationReport(IReadOnlyList<ValidationIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(issues);
        Issues = issues;

        List<ValidationIssue>? errors = null;
        List<ValidationIssue>? warnings = null;
        List<ValidationIssue>? infos = null;
        List<ValidationIssue>? advisories = null;
        foreach (var issue in issues)
        {
            switch (issue.Severity)
            {
                case ValidationSeverity.Error:
                    (errors ??= []).Add(issue);
                    break;
                case ValidationSeverity.Warning:
                    (warnings ??= []).Add(issue);
                    (advisories ??= []).Add(issue);
                    break;
                case ValidationSeverity.Info:
                    (infos ??= []).Add(issue);
                    (advisories ??= []).Add(issue);
                    break;
                default:
                    // A severity outside the enum's named values is possible (the enum is
                    // public and an integer casts in), and it is not an error, so it belongs
                    // to the advisory tier alone.
                    (advisories ??= []).Add(issue);
                    break;
            }
        }

        Errors = errors ?? NoIssues;
        Warnings = warnings ?? NoIssues;
        Infos = infos ?? NoIssues;
        Advisories = advisories ?? NoIssues;
    }

    /// <summary>All issues, in validator order.</summary>
    public IReadOnlyList<ValidationIssue> Issues { get; }

    /// <summary>
    /// True when there are no <see cref="ValidationSeverity.Error"/> issues.
    /// Warnings and infos do not affect validity.
    /// </summary>
    public bool IsValid => Errors.Count == 0;

    /// <summary>Error-severity issues, in issue order.</summary>
    public IReadOnlyList<ValidationIssue> Errors { get; }

    /// <summary>Warning-severity issues, in issue order.</summary>
    public IReadOnlyList<ValidationIssue> Warnings { get; }

    /// <summary>Info-severity issues, in issue order.</summary>
    public IReadOnlyList<ValidationIssue> Infos { get; }

    /// <summary>
    /// Non-error issues (warnings and infos), in issue order. Mirrors the ASP.NET Core
    /// package's wire-level <c>ValidationReportProblemMapper.ToAdvisories</c> mapping for a
    /// client that holds the report directly instead of a parsed problem response.
    /// </summary>
    public IReadOnlyList<ValidationIssue> Advisories { get; }
}
