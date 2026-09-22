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
