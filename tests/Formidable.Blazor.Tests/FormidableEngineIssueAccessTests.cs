using FluentValidation;
using Formidable.Blazor.Tests.Fixtures;
using Formidable.Introspection;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Time.Testing;

namespace Formidable.Blazor.Tests;

public class FormidableEngineIssueAccessTests
{
    private readonly EngineOrder _order = new();
    private readonly EditContext _editContext;
    private readonly FormidableEngine<EngineOrder> _engine;
    private readonly FormidableOptions _options = new() { DisclosureOverride = _ => true };

    public FormidableEngineIssueAccessTests()
    {
        _editContext = new EditContext(_order);
        _engine = new FormidableEngine<EngineOrder>(
            _order, _editContext,
            new FluentValidationModelValidator<EngineOrder>(new EngineOrderValidator()),
            new ReflectionModelIntrospector(), _options, new FakeTimeProvider());
    }

    [Fact]
    public void Options_are_exposed()
    {
        Assert.Same(_options, _engine.Options);
    }

    [Fact]
    public async Task GetIssues_returns_errors_and_warnings_for_a_field()
    {
        _order.Description = "a-b"; // passes NotEmpty, fails the Warning no-hyphen rule
        await _engine.ValidateForSubmitAsync();

        var issues = _engine.GetIssues(new FieldIdentifier(_order, nameof(EngineOrder.Description)));

        Assert.Contains(issues, i => i.Severity == ValidationSeverity.Warning && i.Message == "Avoid hyphens");
    }

    [Fact]
    public async Task GetVisibleIssues_spans_fields_and_model_level()
    {
        _order.Description = "ok";
        _order.Customer = new EngineCustomer();
        _order.Items = [new() { Sku = "A" }, new() { Sku = "B" }, new() { Sku = "C" }, new() { Sku = "D" }];
        await _engine.ValidateForSubmitAsync();

        var visible = _engine.GetVisibleIssues();

        Assert.Contains(visible, v => v.Field.Equals(new FieldIdentifier(_order, string.Empty))
            && v.Issue.Message == "No more than 3 items");
    }

    [Fact]
    public void GetVisibleIssues_deduplicates_live_against_submit_by_field_and_message()
    {
        _order.Description = new string('x', 11);
        _editContext.NotifyFieldChanged(new FieldIdentifier(_order, nameof(EngineOrder.Description)));

        var visible = _engine.GetVisibleIssues();
        var descriptionEntries = visible.Where(v =>
            v.Field.Equals(new FieldIdentifier(_order, nameof(EngineOrder.Description)))).ToList();

        Assert.Single(descriptionEntries);
    }

    [Fact]
    public void GetIssues_returns_empty_for_clean_field()
    {
        Assert.Empty(_engine.GetIssues(new FieldIdentifier(_order, nameof(EngineOrder.Description))));
    }

    [Fact]
    public void GetFieldState_reports_HasInfos_for_an_info_only_issue()
    {
        var field = new FieldIdentifier(_order, nameof(EngineOrder.Description));
        _engine.ApplyServerIssues([new ValidationIssue(nameof(EngineOrder.Description), "fyi", ValidationSeverity.Info)]);

        var state = _engine.GetFieldState(field);

        Assert.True(state.HasInfos);
        Assert.False(state.HasErrors);
        Assert.False(state.HasWarnings);
    }

    [Fact]
    public void GetFieldState_HasInfos_is_false_when_only_errors_exist()
    {
        var field = new FieldIdentifier(_order, nameof(EngineOrder.Description));
        _engine.ApplyServerIssues([new ValidationIssue(nameof(EngineOrder.Description), "required")]);

        var state = _engine.GetFieldState(field);

        Assert.True(state.HasErrors);
        Assert.False(state.HasInfos);
    }

    [Fact]
    public void One_message_failing_twice_in_the_live_channel_is_shown_once()
    {
        var order = new EngineOrder();
        var editContext = new EditContext(order);
        using var engine = new FormidableEngine<EngineOrder>(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(new RepeatedMessageValidator()),
            new ReflectionModelIntrospector(), new FormidableOptions(), new FakeTimeProvider());

        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));
        editContext.NotifyFieldChanged(description);

        Assert.Single(engine.GetIssues(description));
        Assert.Single(engine.GetVisibleIssues(), v => v.Field.Equals(description));
    }

    /// <summary>
    /// Two live rules on one field failing with the same words — the shape that makes the shadow
    /// rule's within-channel half reachable, since a reader has no use for the same sentence twice.
    /// </summary>
    private sealed class RepeatedMessageValidator : DraftSubmitValidator<EngineOrder>
    {
        protected override void ConfigureDraftRules()
        {
            RuleFor(x => x.Description).Must(_ => false).WithMessage("Description is not acceptable");
            RuleFor(x => x.Description).Must(_ => false).WithMessage("Description is not acceptable");
        }

        protected override void ConfigureSubmitRules()
        {
        }
    }
}
