using System.Text.Json;

namespace Formidable.Tests;

public class FormidableValidationProblemTests
{
    [Fact]
    public void ToIssues_maps_error_entries_one_issue_per_message()
    {
        var problem = new FormidableValidationProblem
        {
            Errors = new Dictionary<string, string[]>
            {
                ["Description"] = ["Required", "Too long"],
                ["Items[1].Sku"] = ["Unknown SKU"]
            }
        };

        var issues = problem.ToIssues();

        Assert.Equal(3, issues.Count);
        Assert.All(issues, i => Assert.Equal(ValidationSeverity.Error, i.Severity));
        Assert.Contains(issues, i => i.Path == "Items[1].Sku" && i.Message == "Unknown SKU");
    }

    [Fact]
    public void ToIssues_maps_advisories_with_parsed_severity()
    {
        var problem = new FormidableValidationProblem
        {
            Advisories =
            [
                new ValidationProblemAdvisory("Description", "Avoid hyphens", "Warning", Code: "HYPHENS"),
                new ValidationProblemAdvisory("Notes", "FYI only", "info"),
                new ValidationProblemAdvisory("Notes", "Unknown tag", "Bogus"),
                new ValidationProblemAdvisory("Notes", "Never an error here", "Error")
            ]
        };

        var issues = problem.ToIssues();

        Assert.Equal(ValidationSeverity.Warning, issues[0].Severity);
        Assert.Equal("HYPHENS", issues[0].Code);
        Assert.Equal(ValidationSeverity.Info, issues[1].Severity);
        Assert.Equal(ValidationSeverity.Warning, issues[2].Severity);
        Assert.Equal(ValidationSeverity.Warning, issues[3].Severity);
    }

    [Fact]
    public void Deserializes_the_server_payload_shape_with_web_defaults()
    {
        const string body = """
            {
              "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1",
              "title": "One or more validation errors occurred.",
              "status": 400,
              "errors": { "Items[0].Sku": ["Required"] },
              "advisories": [
                { "path": "Description", "message": "Avoid hyphens", "severity": "Warning", "code": null, "displayName": "Description" }
              ]
            }
            """;

        var problem = JsonSerializer.Deserialize<FormidableValidationProblem>(
            body, JsonSerializerOptions.Web)!;

        var issues = problem.ToIssues();
        Assert.Equal(2, issues.Count);
        Assert.Contains(issues, i => i.Path == "Items[0].Sku" && i.Severity == ValidationSeverity.Error);
        Assert.Contains(issues, i => i.Path == "Description" && i.Severity == ValidationSeverity.Warning && i.DisplayName == "Description");
    }

    [Fact]
    public void ToIssues_is_empty_for_an_empty_payload()
    {
        Assert.Empty(new FormidableValidationProblem().ToIssues());
    }

    [Fact]
    public void ToIssues_tolerates_a_null_message_array_for_a_key()
    {
        const string body = """
            { "errors": { "X": null } }
            """;

        var problem = JsonSerializer.Deserialize<FormidableValidationProblem>(
            body, JsonSerializerOptions.Web)!;

        Assert.Empty(problem.ToIssues());
    }

    [Fact]
    public void ToIssues_tolerates_null_errors_and_advisories_collections()
    {
        const string body = """
            { "errors": null, "advisories": null }
            """;

        var problem = JsonSerializer.Deserialize<FormidableValidationProblem>(
            body, JsonSerializerOptions.Web)!;

        Assert.Empty(problem.ToIssues());
    }
}
