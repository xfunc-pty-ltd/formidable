using Formidable;
using Formidable.AspNetCore;

namespace Formidable.AspNetCore.Tests;

public class ValidationReportProblemMapperTests
{
    private static readonly ValidationReport Report = new([
        new ValidationIssue("Description", "Required"),
        new ValidationIssue("Items[0].Sku", "Unknown SKU"),
        new ValidationIssue("Items[0].Sku", "Too short"),
        new ValidationIssue("Description", "Avoid hyphens", ValidationSeverity.Warning, "HYPHENS", "Description"),
        new ValidationIssue("Notes", "FYI", ValidationSeverity.Info)
    ]);

    [Fact]
    public void ToErrorDictionary_groups_error_messages_by_path_in_order()
    {
        var errors = ValidationReportProblemMapper.ToErrorDictionary(Report);

        Assert.Equal(2, errors.Count);
        Assert.Equal(["Required"], errors["Description"]);
        Assert.Equal(["Unknown SKU", "Too short"], errors["Items[0].Sku"]);
    }

    [Fact]
    public void ToWarnings_carries_non_error_issues_with_severity_names()
    {
        var warnings = ValidationReportProblemMapper.ToWarnings(Report);

        Assert.Equal(2, warnings.Count);
        Assert.Equal(new ValidationProblemWarning("Description", "Avoid hyphens", "Warning", "HYPHENS", "Description"), warnings[0]);
        Assert.Equal(new ValidationProblemWarning("Notes", "FYI", "Info"), warnings[1]);
    }

    [Fact]
    public void Warnings_extension_key_matches_the_client_contract()
    {
        Assert.Equal("warnings", ValidationReportProblemMapper.WarningsExtensionKey);
    }
}
