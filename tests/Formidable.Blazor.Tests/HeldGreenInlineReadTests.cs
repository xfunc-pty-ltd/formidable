using Formidable.Blazor.Tests.Fixtures;
using Formidable.Introspection;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Time.Testing;

namespace Formidable.Blazor.Tests;

/// <summary>
/// A page that reads field state inside its <see cref="IFormidableEngine.StateChanged"/> handler
/// (the live-indicator pattern) reads at the first-touch publish of an edit, before the edit has
/// armed anything. A sibling wearing green on the held vouch keeps it: whether a re-answer is on
/// its way is asked again at the next read, once the arm exists, rather than the declined answer
/// standing until a landing. Engine-level on a fake clock, under TrackFormValidity and a live
/// window that never closes, so the only re-answer is the validity check the edit arms after its
/// first-touch publish.
/// </summary>
public class HeldGreenInlineReadTests
{
    private static readonly TimeSpan Refresh = new FormidableOptions().RefreshDebounce;

    // Mutation: drop the promise re-check for a declined serve in EnsureSubmitCoverageCurrent. The
    // handler's read then caches the declined answer, and the name reads false until the check
    // lands.
    [Fact]
    public void A_green_field_keeps_green_when_a_handler_reads_it_at_another_fields_first_touch()
    {
        using var form = GreenNameForm.Build();
        form.Engine.StateChanged += (_, _) => _ = form.Engine.GetFieldState(form.Name).WouldPassSubmit;

        form.Order.Description = "still ok";
        form.EditContext.NotifyFieldChanged(form.Description);

        Assert.True(form.Engine.GetFieldState(form.Name).WouldPassSubmit);
    }

    // Control: with nothing reading inside the publish, the first read after the edit comes after
    // the arm, and the held answer is served on the check's promise.
    [Fact]
    public void A_green_field_keeps_green_at_another_fields_first_touch_with_no_reader()
    {
        using var form = GreenNameForm.Build();

        form.Order.Description = "still ok";
        form.EditContext.NotifyFieldChanged(form.Description);

        Assert.True(form.Engine.GetFieldState(form.Name).WouldPassSubmit);
    }

    /// <summary>An engine whose customer name wears green on a landed validity check, and whose description has never been touched.</summary>
    private sealed class GreenNameForm : IDisposable
    {
        public required FormidableEngine<EngineOrder> Engine { get; init; }
        public required EditContext EditContext { get; init; }
        public required EngineOrder Order { get; init; }
        public required FieldIdentifier Name { get; init; }
        public required FieldIdentifier Description { get; init; }

        public static GreenNameForm Build()
        {
            var order = new EngineOrder { Description = "ok", Customer = new EngineCustomer { Name = "Bo" } };
            var editContext = new EditContext(order);
            var time = new FakeTimeProvider();
            var engine = new FormidableEngine<EngineOrder>(
                order,
                editContext,
                new FluentValidationModelValidator<EngineOrder>(new DeclarationOrderValidator()),
                new ReflectionModelIntrospector(),
                new FormidableOptions { TrackFormValidity = true, LiveDebounce = Timeout.InfiniteTimeSpan },
                time);
            var name = new FieldIdentifier(order.Customer!, nameof(EngineCustomer.Name));

            // The name's own edit arms the validity check; its landing files the answer the name
            // wears green on, held at this edit stamp.
            order.Customer!.Name = "Bea";
            editContext.NotifyFieldChanged(name);
            time.Advance(Refresh);
            Assert.True(engine.GetFieldState(name).WouldPassSubmit, "setup: the name is not green after the check lands");

            return new GreenNameForm
            {
                Engine = engine,
                EditContext = editContext,
                Order = order,
                Name = name,
                Description = new FieldIdentifier(order, nameof(EngineOrder.Description)),
            };
        }

        public void Dispose() => Engine.Dispose();
    }
}
