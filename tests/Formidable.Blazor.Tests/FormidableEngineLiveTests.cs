using Formidable.Blazor.Tests.Fixtures;
using Formidable.Introspection;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Time.Testing;
using static Formidable.Blazor.Tests.Fixtures.EngineTestSync;

namespace Formidable.Blazor.Tests;

public class FormidableEngineLiveTests
{
    private readonly EngineOrder _order = new();
    private readonly EditContext _editContext;
    private readonly FormidableEngine<EngineOrder> _engine;
    private readonly FakeTimeProvider _time = new();

    public FormidableEngineLiveTests()
    {
        _editContext = new EditContext(_order);
        _engine = new FormidableEngine<EngineOrder>(
            _order,
            _editContext,
            new FluentValidationModelValidator<EngineOrder>(new EngineOrderValidator()),
            new ReflectionModelIntrospector(),
            new FormidableOptions(),
            _time);
    }

    private FieldIdentifier DescriptionField => new(_order, nameof(EngineOrder.Description));

    [Fact]
    public void Field_change_applies_live_profile_issues_for_that_field_only()
    {
        _order.Description = new string('x', 11); // draft rule fails
        _order.Customer = null;                   // submit rule fails — but Customer is never engaged

        _editContext.NotifyFieldChanged(DescriptionField);

        var messages = _editContext.GetValidationMessages(DescriptionField).ToList();
        Assert.Single(messages);
        Assert.Contains("10", messages[0]);
        Assert.Empty(_editContext.GetValidationMessages(new FieldIdentifier(_order, nameof(EngineOrder.Customer))));
    }

    [Fact]
    public void Fixing_the_field_clears_its_live_messages()
    {
        _order.Description = new string('x', 11);
        _editContext.NotifyFieldChanged(DescriptionField);
        Assert.NotEmpty(_editContext.GetValidationMessages(DescriptionField));

        _order.Description = "ok";
        _editContext.NotifyFieldChanged(DescriptionField);

        Assert.Empty(_editContext.GetValidationMessages(DescriptionField));
    }

    [Fact]
    public void Field_change_marks_field_touched_and_raises_state_changed()
    {
        var raised = 0;
        _engine.StateChanged += (_, _) => raised++;

        _editContext.NotifyFieldChanged(DescriptionField);

        Assert.True(_engine.GetFieldState(DescriptionField).IsTouched);
        Assert.True(raised >= 1);
    }

    [Fact]
    public void Warning_issues_do_not_write_edit_context_messages_but_surface_in_field_state()
    {
        _order.Description = "a-b"; // warning-severity submit rule; also passes draft rules
        var options = new FormidableOptions { DisclosureOverride = _ => true };
        using var engine = new FormidableEngine<EngineOrder>(
            _order, new EditContext(_order),
            new FluentValidationModelValidator<EngineOrder>(new EngineOrderValidator()),
            new ReflectionModelIntrospector(), options, _time);

        engine.EditContext.NotifyFieldChanged(new FieldIdentifier(_order, nameof(EngineOrder.Description)));

        var state = engine.GetFieldState(new FieldIdentifier(_order, nameof(EngineOrder.Description)));
        Assert.True(state.HasWarnings);
        // Error-only store: the warning must not appear as an EditContext message.
        Assert.Empty(engine.EditContext.GetValidationMessages(new FieldIdentifier(_order, nameof(EngineOrder.Description))));
    }

    [Fact]
    public void Dispose_detaches_from_edit_context()
    {
        _engine.Dispose();
        _order.Description = new string('x', 11);

        _editContext.NotifyFieldChanged(DescriptionField);

        Assert.Empty(_editContext.GetValidationMessages(DescriptionField));
    }

    [Fact]
    public void LiveDebounce_defers_the_live_pass_until_the_window_closes()
    {
        var editContext = new EditContext(_order);
        using var engine = new FormidableEngine<EngineOrder>(
            _order, editContext,
            new FluentValidationModelValidator<EngineOrder>(new EngineOrderValidator()),
            new ReflectionModelIntrospector(),
            new FormidableOptions { LiveDebounce = TimeSpan.FromMilliseconds(400) },
            _time);

        _order.Description = new string('x', 11); // draft rule fails once the deferred pass runs

        editContext.NotifyFieldChanged(DescriptionField);
        Assert.False(engine.IsValidating); // nothing started yet - the window is open

        _time.Advance(TimeSpan.FromMilliseconds(399));
        Assert.False(engine.IsValidating);

        _time.Advance(TimeSpan.FromMilliseconds(1)); // window closes -> the deferred pass runs

        Assert.NotEmpty(editContext.GetValidationMessages(DescriptionField));
    }

    [Fact]
    public async Task LiveDebounce_edit_within_window_extends_it_and_widens_the_scope()
    {
        var order = new EngineOrder { Customer = new EngineCustomer() };
        var validator = new SlowLiveRuleValidator();
        var editContext = new EditContext(order);
        using var engine = new FormidableEngine<EngineOrder>(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(validator),
            new ReflectionModelIntrospector(),
            new FormidableOptions { LiveDebounce = TimeSpan.FromMilliseconds(400) },
            _time);

        var descriptionField = new FieldIdentifier(order, nameof(EngineOrder.Description));
        var customerNameField = new FieldIdentifier(order.Customer!, nameof(EngineCustomer.Name));

        editContext.NotifyFieldChanged(descriptionField);
        _time.Advance(TimeSpan.FromMilliseconds(300));
        editContext.NotifyFieldChanged(customerNameField); // re-arms and widens the pending scope
        _time.Advance(TimeSpan.FromMilliseconds(300));     // 600ms after the first edit: still deferred
        Assert.False(engine.IsValidating);

        _time.Advance(TimeSpan.FromMilliseconds(100)); // 400ms after the second edit: window closes

        // Both fields are scoped to the SAME pass - the second edit widened it rather than
        // starting a second one.
        Assert.True(engine.GetFieldState(descriptionField).IsValidating);
        Assert.True(engine.GetFieldState(customerNameField).IsValidating);

        var quiescent = Quiescence(engine);
        validator.Gate.SetResult();
        await quiescent;

        Assert.False(engine.GetFieldState(descriptionField).IsValidating);
        Assert.False(engine.GetFieldState(customerNameField).IsValidating);
    }

    [Fact]
    public void Null_LiveDebounce_keeps_the_immediate_live_pass()
    {
        _order.Description = new string('x', 11);

        _editContext.NotifyFieldChanged(DescriptionField);

        Assert.NotEmpty(_editContext.GetValidationMessages(DescriptionField));
    }
}
