using FluentValidation;
using Formidable.Tests.Fixtures;

namespace Formidable.Tests;

public class DraftSubmitValidatorTests
{
    private sealed class ApprovableOrderValidator : DraftSubmitValidator<TestOrder>
    {
        protected override void ConfigureDraftRules() =>
            RuleFor(x => x.Description).MaximumLength(10);

        protected override void ConfigureSubmitRules() =>
            RuleFor(x => x.Description).NotEmpty();

        protected override void ConfigureAdditionalProfiles() =>
            Profile("Approve", () => RuleFor(x => x.Customer).NotNull());
    }

    [Fact]
    public void Draft_profile_runs_draft_rules_only()
    {
        var validator = new ApprovableOrderValidator();

        Assert.True(validator.Validate(new TestOrder(), ValidationProfile.Draft).IsValid);
        Assert.False(validator.Validate(new TestOrder { Description = new string('x', 11) }, ValidationProfile.Draft).IsValid);
    }

    [Fact]
    public void Submit_profile_adds_submit_rules()
    {
        var validator = new ApprovableOrderValidator();

        var result = validator.Validate(new TestOrder(), ValidationProfile.Submit);

        Assert.Contains(result.Errors, e => e.PropertyName == "Description");
        Assert.DoesNotContain(result.Errors, e => e.PropertyName == "Customer"); // Approve rule must not run
    }

    [Fact]
    public void Custom_profile_composes_registered_rulesets()
    {
        var validator = new ApprovableOrderValidator();
        var approve = ValidationProfile.Named("Approve", includeDefaultRules: true, ValidationProfile.SubmitRuleSetName, "Approve");

        var result = validator.Validate(new TestOrder { Customer = null }, approve);

        Assert.Contains(result.Errors, e => e.PropertyName == "Description"); // submit ruleset included
        Assert.Contains(result.Errors, e => e.PropertyName == "Customer");    // approve ruleset included
    }

    [Fact]
    public async Task Async_profile_validation_works()
    {
        var validator = new ApprovableOrderValidator();

        var result = await validator.ValidateAsync(new TestOrder(), ValidationProfile.Submit);

        Assert.False(result.IsValid);
    }
}
