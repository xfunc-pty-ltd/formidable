namespace Formidable;

/// <summary>The outcome of validating a model with a <see cref="ValidationProfile"/>.</summary>
public sealed class ValidationReport
{
    /// <summary>A shared valid, issue-free report.</summary>
    public static ValidationReport Empty { get; } = new([]);

    /// <summary>Creates a report from the given issues.</summary>
    public ValidationReport(IReadOnlyList<ValidationIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(issues);
        Issues = issues;
    }

    /// <summary>All issues, in validator order.</summary>
    public IReadOnlyList<ValidationIssue> Issues { get; }

    /// <summary>
    /// True when there are no <see cref="ValidationSeverity.Error"/> issues.
    /// Warnings and infos do not affect validity.
    /// </summary>
    public bool IsValid => !Errors.Any();

    /// <summary>Error-severity issues.</summary>
    public IEnumerable<ValidationIssue> Errors => Issues.Where(i => i.Severity == ValidationSeverity.Error);

    /// <summary>Warning-severity issues.</summary>
    public IEnumerable<ValidationIssue> Warnings => Issues.Where(i => i.Severity == ValidationSeverity.Warning);

    /// <summary>Info-severity issues.</summary>
    public IEnumerable<ValidationIssue> Infos => Issues.Where(i => i.Severity == ValidationSeverity.Info);

    /// <summary>
    /// Non-error issues (warnings and infos), in issue order. Mirrors the ASP.NET Core
    /// package's wire-level <c>ValidationReportProblemMapper.ToAdvisories</c> mapping for a
    /// client that holds the report directly instead of a parsed problem response.
    /// </summary>
    public IEnumerable<ValidationIssue> Advisories => Issues.Where(i => i.Severity != ValidationSeverity.Error);
}
