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
    public void Distinct_error_display_names_fall_back_to_path_and_preserve_order()
    {
        var report = new ValidationReport(
        [
            new ValidationIssue("Customer.Name", "required", DisplayName: "Customer name"),
            new ValidationIssue("Customer.Name", "too short", DisplayName: "Customer name"),
            new ValidationIssue("LineItems[0].Sku", "required"),
            new ValidationIssue("Notes", "ignore me", ValidationSeverity.Warning, DisplayName: "Notes")
        ]);

        Assert.Equal(new[] { "Customer name", "LineItems[0].Sku" }, report.GetDistinctErrorDisplayNames());
    }
}
