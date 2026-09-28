using System.Text.Json;

namespace Formidable.Tests;

public class FormidableValidationProblemJsonContextTests
{
    private const string CamelCaseBody = """
        {
          "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1",
          "title": "One or more validation errors occurred.",
          "status": 400,
          "errors": {
            "Description": ["Required", "Too long"],
            "Items[0].Sku": ["Unknown SKU"]
          },
          "advisories": [
            { "path": "Description", "message": "Avoid hyphens", "severity": "Warning", "code": "HYPHENS", "displayName": "Description" },
            { "path": "Notes", "message": "FYI only", "severity": "Info" }
          ]
        }
        """;

    private const string PascalCaseBody = """
        {
          "Errors": {
            "Description": ["Required", "Too long"],
            "Items[0].Sku": ["Unknown SKU"]
          },
          "Advisories": [
            { "Path": "Description", "Message": "Avoid hyphens", "Severity": "Warning", "Code": "HYPHENS", "DisplayName": "Description" },
            { "Path": "Notes", "Message": "FYI only", "Severity": "Info" }
          ]
        }
        """;

    private const string UnknownMembersAndNullsBody = """
        {
          "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1",
          "traceId": "unused-by-this-model",
          "errors": {
            "Description": ["Required", null],
            "Items[0].Sku": null
          },
          "advisories": [
            null,
            { "path": "Notes", "message": "FYI", "severity": "Info", "extra": "ignored", "code": null, "displayName": null }
          ]
        }
        """;

    [Fact]
    public void The_generated_read_matches_the_web_defaults_read()
    {
        // Mutation: change FormidableValidationProblemJsonContext's attribute to
        // [JsonSourceGenerationOptions(JsonSerializerDefaults.General)]. The generated read then
        // matches camelCase and unknown-member bodies against the exact, case-sensitive member
        // names, finds none, and reads Errors and Advisories as empty, so this test fails.
        AssertIssuesMatch(CamelCaseBody);
        AssertIssuesMatch(PascalCaseBody);
        AssertIssuesMatch(UnknownMembersAndNullsBody);
    }

    [Fact]
    public void The_context_options_match_the_web_defaults()
    {
        // Mutation: change FormidableValidationProblemJsonContext's attribute to
        // [JsonSourceGenerationOptions(JsonSerializerDefaults.General)]. General leaves
        // PropertyNamingPolicy null, PropertyNameCaseInsensitive false, and NumberHandling at
        // Strict, so every assertion below fails.
        var generated = FormidableValidationProblemJsonContext.Default.Options;

        Assert.Equal(JsonSerializerOptions.Web.PropertyNamingPolicy, generated.PropertyNamingPolicy);
        Assert.Equal(JsonSerializerOptions.Web.PropertyNameCaseInsensitive, generated.PropertyNameCaseInsensitive);
        Assert.Equal(JsonSerializerOptions.Web.NumberHandling, generated.NumberHandling);
    }

    private static void AssertIssuesMatch(string body)
    {
        var reflected = JsonSerializer.Deserialize<FormidableValidationProblem>(body, JsonSerializerOptions.Web)!;
        var generated = JsonSerializer.Deserialize(body, FormidableValidationProblemJsonContext.Default.FormidableValidationProblem)!;

        Assert.Equal(reflected.ToIssues(), generated.ToIssues());
    }
}
