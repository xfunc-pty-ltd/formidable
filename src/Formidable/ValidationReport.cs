namespace Formidable;

/// <summary>The outcome of validating a model with a <see cref="ValidationProfile"/>.</summary>
public sealed class ValidationReport
{
    private static readonly IReadOnlyList<ValidationIssue> NoIssues = [];

    /// <summary>A shared report with no issues.</summary>
    public static ValidationReport Empty { get; } = new([]);

    /// <summary>Creates a report over <paramref name="issues"/>, partitioning them by severity once.</summary>
    /// <param name="issues">The issues in validator order; the report keeps this instance.</param>
    /// <exception cref="ArgumentNullException"><paramref name="issues"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// <see cref="Issues"/> returns exactly the list handed in, while <see cref="Errors"/>,
    /// <see cref="Warnings"/>, <see cref="Infos"/> and <see cref="Advisories"/> are fixed here,
    /// so a later change to that list shows through <see cref="Issues"/> alone.
    /// </remarks>
    public ValidationReport(IReadOnlyList<ValidationIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(issues);
        Issues = issues;

        List<ValidationIssue>? errors = null;
        List<ValidationIssue>? warnings = null;
        List<ValidationIssue>? infos = null;
        List<ValidationIssue>? advisories = null;
        // The caller's list is aliased rather than copied, and the severity views are built once
        // here rather than on each read: an issue added to or removed from that list afterwards
        // shows through Issues, never through a view, and never through IsValid, which answers
        // from Errors.
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

    /// <summary>True when the report carries no <see cref="ValidationSeverity.Error"/> issue; warnings and infos do not affect validity.</summary>
    public bool IsValid => Errors.Count == 0;

    /// <summary>Error-severity issues, in issue order.</summary>
    public IReadOnlyList<ValidationIssue> Errors { get; }

    /// <summary>Warning-severity issues, in issue order.</summary>
    public IReadOnlyList<ValidationIssue> Warnings { get; }

    /// <summary>Info-severity issues, in issue order.</summary>
    public IReadOnlyList<ValidationIssue> Infos { get; }

    /// <summary>Every non-error issue, in issue order: warnings, infos and any severity outside the named members.</summary>
    /// <remarks>What a client holding the report reads where one parsing a problem response reads the <c>advisories</c> extension: the set the ASP.NET Core package's <c>ValidationReportProblemMapper.ToAdvisories</c> writes there, refusing an issue whose severity no member defines.</remarks>
    public IReadOnlyList<ValidationIssue> Advisories { get; }
}
