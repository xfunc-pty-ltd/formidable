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

    [Fact]
    public void ToErrorDictionary_keeps_paths_that_differ_only_by_case_apart()
    {
        // Both server adapters put this dictionary into the response as built, so its keying
        // IS the wire's: two declared properties C# permits, or a display-name override
        // colliding with a property, must each keep their own messages. A case-insensitive
        // comparer here would fold them, and the client would land one message on the wrong
        // field.
        var report = new ValidationReport([
            new ValidationIssue("Id", "lower id"),
            new ValidationIssue("ID", "upper id")
        ]);

        var errors = ValidationReportProblemMapper.ToErrorDictionary(report);

        Assert.Equal(["Id", "ID"], errors.Keys);
        Assert.Equal(["lower id"], errors["Id"]);
        Assert.Equal(["upper id"], errors["ID"]);
    }

    [Fact]
    public void ToErrorDictionary_reads_a_null_path_as_the_model_level_path()
    {
        // An IModelValidator is a consumer-implementable seam, so a hand-rolled one can hand
        // back an issue whose Path is null however the type is annotated. The client's ToIssues
        // reads that as "" -- the model-level path -- and the server grouping it as a
        // dictionary key threw instead, turning one nullable-reference slip into a 500 on every
        // rejected request.
        var report = new ValidationReport([new ValidationIssue(null!, "Something is wrong")]);

        var errors = ValidationReportProblemMapper.ToErrorDictionary(report);

        Assert.Equal(["Something is wrong"], errors[string.Empty]);
    }

    [Fact]
    public void ToAdvisories_reads_a_null_path_and_message_the_way_the_client_does()
    {
        // Same tolerance on the advisory half, and for the same reason: "" is the model-level
        // path on both sides of the wire, and a null message has nothing to show.
        var report = new ValidationReport(
            [new ValidationIssue(null!, null!, ValidationSeverity.Warning)]);

        var advisory = Assert.Single(ValidationReportProblemMapper.ToAdvisories(report));

        Assert.Equal(string.Empty, advisory.Path);
        Assert.Equal(string.Empty, advisory.Message);
    }

    [Fact]
    public void ToAdvisories_refuses_a_severity_no_member_defines()
    {
        // Severity is the wire's member NAME, and an undefined value has none: ToString would
        // write "99", which the client reads as a warning, so the consumer's own bug is
        // relabelled rather than reported. Nothing the library ships can produce one -- the
        // FluentValidation adapter maps exhaustively -- so this is a cast in a hand-rolled
        // validator, and naming it is the only way the author learns of it.
        var report = new ValidationReport(
            [new ValidationIssue("Description", "Odd", (ValidationSeverity)99)]);

        var exception = Assert.Throws<ArgumentException>(
            () => ValidationReportProblemMapper.ToAdvisories(report));

        Assert.Equal("report", exception.ParamName);
        Assert.Contains("99", exception.Message);
        Assert.Contains("Description", exception.Message);
    }
}
