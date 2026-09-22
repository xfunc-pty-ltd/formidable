namespace Formidable.Tests;

public class ValidationReportTests
{
    [Fact]
    public void Empty_report_is_valid()
    {
        Assert.True(ValidationReport.Empty.IsValid);
        Assert.Empty(ValidationReport.Empty.Issues);
    }

    [Fact]
    public void Report_with_only_warnings_and_infos_is_valid()
    {
        var report = new ValidationReport(
        [
            new ValidationIssue("A", "warn", ValidationSeverity.Warning),
            new ValidationIssue("B", "info", ValidationSeverity.Info)
        ]);

        Assert.True(report.IsValid);
        Assert.Single(report.Warnings);
        Assert.Single(report.Infos);
        Assert.Empty(report.Errors);
    }

    [Fact]
    public void Report_with_an_error_is_invalid()
    {
        var report = new ValidationReport([new ValidationIssue("A", "bad")]);

        Assert.False(report.IsValid);
        Assert.Single(report.Errors);
    }

    [Fact]
    public void Issue_severity_defaults_to_error()
    {
        Assert.Equal(ValidationSeverity.Error, new ValidationIssue("A", "bad").Severity);
    }

    /// <summary>
    /// The severity views are materialized once: every read hands back the same list, so a
    /// consumer binding a view repeatedly never re-filters and <c>Count</c> is a plain read.
    /// A view that filtered per read would hand back a fresh sequence each time and fail the
    /// reference checks.
    /// </summary>
    [Fact]
    public void A_severity_view_is_the_same_list_on_every_read()
    {
        var report = new ValidationReport(
        [
            new ValidationIssue("A", "bad", ValidationSeverity.Error),
            new ValidationIssue("B", "warn", ValidationSeverity.Warning),
            new ValidationIssue("C", "info", ValidationSeverity.Info),
            new ValidationIssue("D", "also bad", ValidationSeverity.Error)
        ]);

        Assert.Same(report.Errors, report.Errors);
        Assert.Same(report.Warnings, report.Warnings);
        Assert.Same(report.Infos, report.Infos);
        Assert.Same(report.Advisories, report.Advisories);
        Assert.Equal(2, report.Errors.Count);
        Assert.Single(report.Warnings);
        Assert.Single(report.Infos);
        Assert.Equal(2, report.Advisories.Count);
        Assert.Equal(new[] { "bad", "also bad" }, report.Errors.Select(i => i.Message));
    }

    /// <summary>
    /// The constructor keeps the caller's list — <see cref="ValidationReport.Issues"/> returns
    /// it — while the severity views partition it during construction: an issue added to the
    /// list afterwards is visible through <see cref="ValidationReport.Issues"/>, never through
    /// a view, and never through <see cref="ValidationReport.IsValid"/>, which answers from
    /// <see cref="ValidationReport.Errors"/>.
    /// </summary>
    [Fact]
    public void The_views_snapshot_at_construction_while_Issues_aliases_the_callers_list()
    {
        var issues = new List<ValidationIssue> { new("A", "warn", ValidationSeverity.Warning) };
        var report = new ValidationReport(issues);

        issues.Add(new ValidationIssue("B", "bad", ValidationSeverity.Error));

        Assert.Equal(2, report.Issues.Count);
        Assert.Empty(report.Errors);
        Assert.True(report.IsValid);
        Assert.Single(report.Advisories);
    }

    [Fact]
    public void Advisories_yields_warnings_and_infos_in_order_excluding_errors()
    {
        var report = new ValidationReport(
        [
            new ValidationIssue("A", "bad", ValidationSeverity.Error),
            new ValidationIssue("B", "warn", ValidationSeverity.Warning),
            new ValidationIssue("C", "info", ValidationSeverity.Info),
            new ValidationIssue("D", "also bad", ValidationSeverity.Error)
        ]);

        Assert.Equal(
            new[] { "warn", "info" },
            report.Advisories.Select(i => i.Message));
    }
}
