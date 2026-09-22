using FluentValidation;
using Formidable.Tests.Fixtures;

namespace Formidable.Tests;

public class FluentValidationModelValidatorTests
{
    private static readonly object DescriptionState = new();

    private readonly FluentValidationModelValidator<TestOrder> _adapter = new(new TestOrderValidator());

    private sealed class StatefulOrderValidator : AbstractValidator<TestOrder>
    {
        public StatefulOrderValidator() =>
            RuleFor(x => x.Description).NotEmpty().WithState(_ => DescriptionState);
    }

    [Fact]
    public void Valid_model_returns_empty_report()
    {
        var order = new TestOrder
        {
            Description = "ok",
            Customer = new TestCustomer { Name = "Jo" },
            LineItems = [new TestLineItem { Sku = "A1", Quantity = 1 }]
        };

        var report = _adapter.Validate(order, ValidationProfile.Submit);

        Assert.True(report.IsValid);
        Assert.Same(ValidationReport.Empty, report);
    }

    [Fact]
    public void Failures_map_path_message_code_and_display_name()
    {
        var report = _adapter.Validate(new TestOrder(), ValidationProfile.Submit);

        var descriptionIssue = Assert.Single(report.Errors, i => i.Path == "Description");
        Assert.Equal("DESC_REQUIRED", descriptionIssue.Code);
        Assert.Equal("Order description", descriptionIssue.DisplayName);
        Assert.False(string.IsNullOrWhiteSpace(descriptionIssue.Message));
    }

    [Fact]
    public void Warning_severity_maps_and_does_not_invalidate()
    {
        var order = new TestOrder
        {
            Description = "a-b",
            Customer = new TestCustomer(),
            LineItems = [new TestLineItem { Sku = "A1", Quantity = 1 }]
        };

        var report = _adapter.Validate(order, ValidationProfile.Submit);

        Assert.True(report.IsValid);
        var warning = Assert.Single(report.Warnings);
        Assert.Equal(ValidationSeverity.Warning, warning.Severity);
        Assert.Equal("Description", warning.Path);
    }

    [Fact]
    public void Indexed_collection_paths_come_through_verbatim()
    {
        var order = new TestOrder
        {
            Description = "ok",
            Customer = new TestCustomer(),
            LineItems = [new TestLineItem { Sku = "A1" }, new TestLineItem { Sku = "" }]
        };

        var report = _adapter.Validate(order, ValidationProfile.Submit);

        Assert.Contains(report.Errors, i => i.Path == "LineItems[1].Sku");
    }

    [Fact]
    public async Task Async_returns_equivalent_report()
    {
        var report = await _adapter.ValidateAsync(new TestOrder(), ValidationProfile.Submit);

        Assert.False(report.IsValid);
        Assert.Contains(report.Errors, i => i.Path == "Customer");
    }

    [Fact]
    public void Custom_state_lands_on_the_issue()
    {
        var adapter = new FluentValidationModelValidator<TestOrder>(new StatefulOrderValidator());

        var report = adapter.Validate(new TestOrder(), ValidationProfile.Submit);

        var issue = Assert.Single(report.Errors, i => i.Path == "Description");
        Assert.Same(DescriptionState, issue.State);
    }

    [Fact]
    public void A_failure_without_custom_state_carries_null()
    {
        var report = _adapter.Validate(new TestOrder(), ValidationProfile.Submit);

        Assert.NotEmpty(report.Issues);
        Assert.All(report.Issues, issue => Assert.Null(issue.State));
    }
}
