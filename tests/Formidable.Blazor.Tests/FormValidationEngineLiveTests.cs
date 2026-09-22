using Formidable.Blazor.Tests.Fixtures;
using Formidable.Introspection;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Time.Testing;

namespace Formidable.Blazor.Tests;

public class FormValidationEngineLiveTests
{
    private readonly EngineOrder _order = new();
    private readonly EditContext _editContext;
    private readonly FormValidationEngine<EngineOrder> _engine;
    private readonly FakeTimeProvider _time = new();

    public FormValidationEngineLiveTests()
    {
        _editContext = new EditContext(_order);
        _engine = new FormValidationEngine<EngineOrder>(
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
        _order.Customer = null;                   // submit rule fails — but live profile is Draft

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
        _engine.StateChanged += () => raised++;

        _editContext.NotifyFieldChanged(DescriptionField);

        Assert.True(_engine.GetFieldState(DescriptionField).IsTouched);
        Assert.True(raised >= 1);
    }

    [Fact]
    public void Warning_issues_do_not_write_edit_context_messages_but_surface_in_field_state()
    {
        _order.Description = "a-b"; // warning-severity submit rule; also passes draft rules
        var options = new FormidableOptions { LiveProfile = ValidationProfile.Submit, DisclosureOverride = _ => true };
        using var engine = new FormValidationEngine<EngineOrder>(
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
}
