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

    [Fact]
    public void ToIssues_skips_a_null_advisory_entry_and_keeps_its_siblings()
    {
        // A `null` element inside the advisories array deserializes to a null list entry; it
        // carries no path, message, or severity to show, so it is skipped rather than thrown on.
        const string body = """
            { "advisories": [ null, { "path": "Notes", "message": "FYI", "severity": "Info" } ] }
            """;

        var problem = JsonSerializer.Deserialize<FormidableValidationProblem>(
            body, JsonSerializerOptions.Web)!;

        var issue = Assert.Single(problem.ToIssues());
        Assert.Equal("Notes", issue.Path);
        Assert.Equal("FYI", issue.Message);
        Assert.Equal(ValidationSeverity.Info, issue.Severity);
    }

    [Fact]
    public void ToIssues_maps_a_missing_advisory_path_to_the_model_level_path()
    {
        // An advisory object with no "path" key deserializes with Path = null (the record has no
        // required members; the deserializer supplies the default). It maps to "", the
        // model-level path convention, rather than carrying a null into the engine.
        const string body = """
            { "advisories": [ { "message": "General note", "severity": "Warning" } ] }
            """;

        var problem = JsonSerializer.Deserialize<FormidableValidationProblem>(
            body, JsonSerializerOptions.Web)!;

        var issue = Assert.Single(problem.ToIssues());
        Assert.Equal(string.Empty, issue.Path);
        Assert.Equal("General note", issue.Message);
    }

    [Fact]
    public void ToIssues_coalesces_a_null_message_to_empty_in_both_loops()
    {
        // A null element inside an error-message array and an advisory with no "message" key
        // both map to an empty message, never a null one.
        const string body = """
            {
              "errors": { "Name": [ null, "Real" ] },
              "advisories": [ { "path": "Notes", "severity": "Info" } ]
            }
            """;

        var problem = JsonSerializer.Deserialize<FormidableValidationProblem>(
            body, JsonSerializerOptions.Web)!;

        var issues = problem.ToIssues();

        Assert.Equal(3, issues.Count);
        Assert.Contains(issues, i => i.Path == "Name" && i.Message == string.Empty && i.Severity == ValidationSeverity.Error);
        Assert.Contains(issues, i => i.Path == "Name" && i.Message == "Real");
        Assert.Contains(issues, i => i.Path == "Notes" && i.Message == string.Empty && i.Severity == ValidationSeverity.Info);
    }

    [Fact]
    public void ToIssues_maps_a_well_formed_body_unchanged()
    {
        // The tolerance for malformed entries must not touch a well-formed payload: every field
        // of every entry comes through exactly as sent.
        const string body = """
            {
              "errors": { "Description": [ "Required", "Too long" ], "Items[0].Sku": [ "Unknown SKU" ] },
              "advisories": [
                { "path": "Description", "message": "Avoid hyphens", "severity": "Warning", "code": "HYPHENS", "displayName": "Description" }
              ]
            }
            """;

        var problem = JsonSerializer.Deserialize<FormidableValidationProblem>(
            body, JsonSerializerOptions.Web)!;

        var issues = problem.ToIssues();

        Assert.Equal(4, issues.Count);
        Assert.Contains(issues, i => i.Path == "Description" && i.Message == "Required" && i.Severity == ValidationSeverity.Error);
        Assert.Contains(issues, i => i.Path == "Description" && i.Message == "Too long" && i.Severity == ValidationSeverity.Error);
        Assert.Contains(issues, i => i.Path == "Items[0].Sku" && i.Message == "Unknown SKU" && i.Severity == ValidationSeverity.Error);
        Assert.Contains(issues, i =>
            i.Path == "Description" && i.Message == "Avoid hyphens" && i.Severity == ValidationSeverity.Warning
            && i.Code == "HYPHENS" && i.DisplayName == "Description");
    }
}
