using Formidable.Blazor.Tests.Fixtures;
using Formidable.Introspection;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Time.Testing;

namespace Formidable.Blazor.Tests;

public class FormValidationEngineValidatingScopeTests
{
    [Fact]
    public async Task Live_pass_scopes_the_pending_flag_to_the_triggering_field()
    {
        var order = new EngineOrder();
        var validator = new GatedValidator();
        var editContext = new EditContext(order);
        using var engine = new FormValidationEngine<EngineOrder>(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(validator),
            new ReflectionModelIntrospector(),
            // GatedValidator's async gate lives on the Submit ruleset; running the live pass under
            // it is what lets this test hold a live pass open long enough to assert mid-flight.
            new FormidableOptions { LiveProfile = ValidationProfile.Submit, DisclosureOverride = _ => true },
            new FakeTimeProvider());

        var fieldA = new FieldIdentifier(order, nameof(EngineOrder.Description));
        var fieldB = new FieldIdentifier(order, nameof(EngineOrder.Customer));

        editContext.NotifyFieldChanged(fieldA);

        Assert.True(engine.GetFieldState(fieldA).IsValidating);  // triggering field: scoped flag set
        Assert.False(engine.GetFieldState(fieldB).IsValidating); // untouched field: scope excludes it
        Assert.True(engine.IsValidating);                        // engine-level flag stays form-wide

        // Subscribe only now the pass is confirmed in flight — MarkTouched's own StateChanged
        // notification (fired before the pass flips IsValidating true) would otherwise resolve
        // quiescence prematurely.
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

        Assert.False(engine.GetFieldState(fieldA).IsValidating);
        Assert.False(engine.GetFieldState(fieldB).IsValidating);
    }

    [Fact]
    public async Task Submit_pass_marks_every_field_validating()
    {
        var order = new EngineOrder();
        var validator = new GatedValidator();
        using var engine = new FormValidationEngine<EngineOrder>(
            order, new EditContext(order),
            new FluentValidationModelValidator<EngineOrder>(validator),
            new ReflectionModelIntrospector(),
            new FormidableOptions { DisclosureOverride = _ => true },
            new FakeTimeProvider());

        var fieldA = new FieldIdentifier(order, nameof(EngineOrder.Description));
        var fieldB = new FieldIdentifier(order, nameof(EngineOrder.Customer));

        var pending = engine.ValidateForSubmitAsync();

        Assert.True(engine.GetFieldState(fieldA).IsValidating); // submit pass is form-wide
        Assert.True(engine.GetFieldState(fieldB).IsValidating); // ...even for a field the pass never touches

        validator.Gate.SetResult();
        await pending;

        Assert.False(engine.GetFieldState(fieldA).IsValidating);
        Assert.False(engine.GetFieldState(fieldB).IsValidating);
    }
}
