using Formidable.Tests.Fixtures;

namespace Formidable.Tests;

public class ValidatorProfileExtensionsTests
{
    private readonly TestOrderValidator _validator = new();

    [Fact]
    public void Draft_profile_runs_default_rules_only()
    {
        var order = new TestOrder { Description = "" }; // empty: Submit-required, Draft-valid

        var result = _validator.Validate(order, ValidationProfile.Draft);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Draft_profile_reports_malformed_values()
    {
        var order = new TestOrder { Description = new string('x', 11) };

        var result = _validator.Validate(order, ValidationProfile.Draft);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == "Description");
    }

    [Fact]
    public void Submit_profile_runs_default_and_submit_rules()
    {
        var order = new TestOrder
        {
            Description = new string('x', 11),           // draft rule fails
            Customer = null,                             // submit rule fails
            LineItems = [new TestLineItem { Sku = "" }]  // submit per-item rule fails
        };

        var result = _validator.Validate(order, ValidationProfile.Submit);

        Assert.Contains(result.Errors, e => e.PropertyName == "Description");   // draft rule ran
        Assert.Contains(result.Errors, e => e.PropertyName == "Customer");      // submit rule ran
        Assert.Contains(result.Errors, e => e.PropertyName == "LineItems[0].Sku");
    }

    [Fact]
    public void Custom_profile_without_default_rules_skips_them()
    {
        var order = new TestOrder { Description = new string('x', 11), Customer = null };
        var submitOnly = ValidationProfile.Named("SubmitOnly", includeDefaultRules: false, "Submit");

        var result = _validator.Validate(order, submitOnly);

        Assert.DoesNotContain(result.Errors, e => e.PropertyName == "Description" && e.ErrorMessage.Contains("10"));
        Assert.Contains(result.Errors, e => e.PropertyName == "Customer");
    }

    [Fact]
    public async Task Async_matches_sync_behavior()
    {
        var order = new TestOrder { Customer = null };

        var result = await _validator.ValidateAsync(order, ValidationProfile.Submit, CancellationToken.None);

        Assert.Contains(result.Errors, e => e.PropertyName == "Customer");
    }
}
