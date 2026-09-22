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
    public void ToAdvisories_carries_non_error_issues_with_severity_names()
    {
        var advisories = ValidationReportProblemMapper.ToAdvisories(Report);

        Assert.Equal(2, advisories.Count);
        Assert.Equal(new ValidationProblemAdvisory("Description", "Avoid hyphens", "Warning", "HYPHENS", "Description"), advisories[0]);
        Assert.Equal(new ValidationProblemAdvisory("Notes", "FYI", "Info"), advisories[1]);
    }

    [Fact]
    public void Advisories_extension_key_matches_the_client_contract()
    {
        Assert.Equal("advisories", ValidationReportProblemMapper.AdvisoriesExtensionKey);
    }
}
