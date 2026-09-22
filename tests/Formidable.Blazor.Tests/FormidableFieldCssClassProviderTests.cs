using Formidable.Blazor.Tests.Fixtures;
using Formidable.Introspection;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Time.Testing;

namespace Formidable.Blazor.Tests;

/// <summary>
/// Pins the class the engine's installed <c>FieldCssClassProvider</c> hands a native
/// <c>InputBase</c> (read via <c>EditContext.FieldCssClass</c>, the same surface
/// <c>InputBase.CssClass</c> itself reads) so it stays observably unchanged, then covers the
/// added Pending class. Going through this surface rather than constructing the provider
/// directly means these assertions hold regardless of how the provider is wired internally.
/// </summary>
public class FormidableFieldCssClassProviderTests
{
    [Fact]
    public void Untouched_unmodified_error_free_field_gets_no_class()
    {
        var order = new EngineOrder();
        using var engine = CreateEngine(order, new EngineOrderValidator());
        var field = new FieldIdentifier(order, nameof(EngineOrder.Description));

        Assert.Equal(string.Empty, engine.EditContext.FieldCssClass(field));
    }

    [Fact]
    public void Modified_error_free_field_gets_the_valid_class()
    {
        var order = new EngineOrder();
        using var engine = CreateEngine(order, new EngineOrderValidator());
        var field = new FieldIdentifier(order, nameof(EngineOrder.Description));

        order.Description = "short"; // draft rule (MaximumLength(10)) passes
        engine.EditContext.NotifyFieldChanged(field);

        Assert.True(engine.EditContext.IsModified(field));
        Assert.Empty(engine.EditContext.GetValidationMessages(field));
        Assert.Equal("formidable-valid", engine.EditContext.FieldCssClass(field));
    }

    [Fact]
    public void Errored_field_gets_the_invalid_class_even_though_it_is_also_modified()
    {
        var order = new EngineOrder();
        using var engine = CreateEngine(order, new EngineOrderValidator());
        var field = new FieldIdentifier(order, nameof(EngineOrder.Description));

        order.Description = new string('x', 11); // draft rule (MaximumLength(10)) fails
        engine.EditContext.NotifyFieldChanged(field);

        Assert.True(engine.EditContext.IsModified(field)); // errors win despite Modified also being true
        Assert.Equal("formidable-invalid", engine.EditContext.FieldCssClass(field));
    }

    // The provider reads only EditContext.IsModified for the Valid decision;
    // FormidableCss.Compute's IsTouched||IsModified branch stays out of the native path, so
    // engine-level touch alone — with no EditContext modification — earns no class.
    [Fact]
    public void Engine_level_touched_alone_does_not_earn_the_valid_class()
    {
        var order = new EngineOrder();
        using var engine = CreateEngine(order, new EngineOrderValidator());
        var field = new FieldIdentifier(order, nameof(EngineOrder.Description));

        engine.MarkTouched(field);

        Assert.True(engine.GetFieldState(field).IsTouched);
        Assert.False(engine.EditContext.IsModified(field));
        Assert.Equal(string.Empty, engine.EditContext.FieldCssClass(field));
    }

    [Fact]
    public async Task Field_validating_gets_the_pending_class_alongside_whatever_else_currently_applies()
    {
        var order = new EngineOrder();
        var validator = new GatedValidator();
        using var engine = CreateEngine(order, validator, new FormidableOptions { LiveProfile = ValidationProfile.Submit });
        var field = new FieldIdentifier(order, nameof(EngineOrder.Description));

        engine.EditContext.NotifyFieldChanged(field); // starts the live pass; GatedValidator's async rule blocks on Gate

        Assert.True(engine.GetFieldState(field).IsValidating);
        Assert.Equal("formidable-valid formidable-pending", engine.EditContext.FieldCssClass(field));

        var quiescent = new TaskCompletionSource();
        engine.StateChanged += () =>
        {
            if (!engine.IsValidating)
            {
                quiescent.TrySetResult();
            }
        };

        validator.Gate.SetResult();
        await quiescent.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Pending clears once the pass ends; the async rule's failure wins over the earlier Valid.
        Assert.Equal("formidable-invalid", engine.EditContext.FieldCssClass(field));
    }

    private static FormValidationEngine<EngineOrder> CreateEngine(
        EngineOrder order,
        FluentValidation.IValidator<EngineOrder> validator,
        FormidableOptions? options = null) =>
        new(order, new EditContext(order),
            new FluentValidationModelValidator<EngineOrder>(validator),
            new ReflectionModelIntrospector(),
            options ?? new FormidableOptions(),
            new FakeTimeProvider());
}
