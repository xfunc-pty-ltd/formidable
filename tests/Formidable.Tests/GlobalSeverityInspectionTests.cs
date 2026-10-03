using FluentValidation;

namespace Formidable.Tests;

/// <summary>
/// A presence rule with no <c>WithSeverity</c> fails with FluentValidation's global default
/// severity, so reading the rules grades it by that default. The default is an error unless an
/// app changes it; one that sets it to a warning has presence rules that never block a submit, and
/// the reading says so. A rule that states its own severity is graded by that severity instead.
/// </summary>
// The test writes FluentValidation's global default severity, which every validation in the
// process reads, so the class runs alone - see ExclusiveGlobalStateCollection.
[Collection(ExclusiveGlobalStateCollection.Name)]
public class GlobalSeverityInspectionTests
{
    private sealed class Ticket
    {
        public string? Impact { get; set; }
        public string? Reference { get; set; }
    }

    private sealed class TicketValidator : AbstractValidator<Ticket>
    {
        public TicketValidator()
        {
            RuleFor(t => t.Impact).NotEmpty();
            RuleFor(t => t.Reference).NotEmpty().WithSeverity(Severity.Error);
        }
    }

    // Mutations this breaks (each executed): read Error in place of the global default (Impact
    // reads Required while the run reports a warning); read the global default for every presence
    // rule, its own severity or not (Reference reads NotRequired while the run reports an error).
    [Fact]
    public async Task A_presence_rule_with_no_severity_of_its_own_follows_the_global_default()
    {
        var stock = ValidatorOptions.Global.Severity;
        try
        {
            ValidatorOptions.Global.Severity = Severity.Warning;
            var adapter = new FluentValidationModelValidator<Ticket>(new TicketValidator());

            // The run and the reading agree. The empty Impact fails as a warning, which never
            // blocks a submit, so it is not required and is still declared. Reference states its
            // severity, so the global default never reaches it: it fails as an error and is
            // required.
            var report = await adapter.ValidateAsync(new Ticket(), ValidationProfile.Submit);
            Assert.Equal(ValidationSeverity.Warning, Assert.Single(report.Issues, i => i.Path == "Impact").Severity);
            Assert.Equal(ValidationSeverity.Error, Assert.Single(report.Issues, i => i.Path == "Reference").Severity);
            Assert.Equal(
                ["Impact", "Reference"],
                adapter.GetDeclaredFieldPaths(ValidationProfile.Submit).Order(StringComparer.Ordinal));
            Assert.Equal(FieldRequirement.NotRequired, adapter.GetFieldRequirement("Impact", ValidationProfile.Submit));
            Assert.Equal(FieldRequirement.Required, adapter.GetFieldRequirement("Reference", ValidationProfile.Submit));
        }
        finally
        {
            ValidatorOptions.Global.Severity = stock;
        }
    }
}
