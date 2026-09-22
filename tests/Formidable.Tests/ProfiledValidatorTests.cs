using FluentValidation;
using Formidable.Tests.Fixtures;

namespace Formidable.Tests;

public class ProfiledValidatorTests
{
    private sealed class StepValidator : ProfiledValidator<TestOrder>
    {
        protected override void ConfigureCommonRules() =>
            RuleFor(x => x.Description).MaximumLength(10);

        protected override void ConfigureProfiles()
        {
            Profile("Step1", () => RuleFor(x => x.Description).NotEmpty());
            Profile("Step2", () => RuleFor(x => x.Customer).NotNull());
        }
    }

    private sealed class EmptyValidator : ProfiledValidator<TestOrder>
    {
    }

    [Fact]
    public void Common_rules_run_under_any_profile_including_default_rules()
    {
        var validator = new StepValidator();
        var profile = ValidationProfile.Named("Step1", includeDefaultRules: true, "Step1");

        var result = validator.Validate(new TestOrder { Description = new string('x', 11) }, profile);

        Assert.Contains(result.Errors, e => e.PropertyName == "Description");
    }

    [Fact]
    public void Named_profile_runs_only_its_rulesets_when_default_rules_excluded()
    {
        var validator = new StepValidator();
        var profile = ValidationProfile.Named("Step2Only", includeDefaultRules: false, "Step2");

        var result = validator.Validate(new TestOrder { Description = new string('x', 11) }, profile);

        Assert.Contains(result.Errors, e => e.PropertyName == "Customer");
        Assert.DoesNotContain(result.Errors, e => e.PropertyName == "Description");
    }

    [Fact]
    public void Profiles_compose_multiple_rulesets()
    {
        var validator = new StepValidator();
        var profile = ValidationProfile.Named("Final", includeDefaultRules: true, "Step1", "Step2");

        var result = validator.Validate(new TestOrder(), profile);

        Assert.Contains(result.Errors, e => e.PropertyName == "Description");
        Assert.Contains(result.Errors, e => e.PropertyName == "Customer");
    }

    [Fact]
    public void Base_with_no_overrides_validates_clean()
    {
        var validator = new EmptyValidator();

        var result = validator.Validate(new TestOrder(), ValidationProfile.Draft);

        Assert.True(result.IsValid);
    }
}
