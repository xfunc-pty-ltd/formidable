using System.Linq.Expressions;
using Bunit;
using Formidable.Blazor.Tests.Fixtures;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;

namespace Formidable.Blazor.Tests;

/// <summary>
/// What one edit costs every OTHER component on the form. A pass has two things to say — it
/// started (pending lights) and it ended (the verdict lands and pending clears) — and every kit
/// component answers a notification round with an unconditional re-render, so a third round is a
/// render per component per keystroke carrying nothing the second one did not already show.
/// </summary>
/// <remarks>
/// The count under test is rounds, not renders. A round is exactly what the engine raises, while
/// the renders a round produces are the renderer's business: one round can land as two render
/// batches depending on where the scheduler resumes an async pass, so a render count would pin the
/// host rather than the engine.
/// </remarks>
public class FormValidationEnginePassNotificationTests : BunitContext
{
    public FormValidationEnginePassNotificationTests() => Services.AddFormidable();

    [Fact]
    public async Task An_async_live_pass_costs_the_form_two_notification_rounds()
    {
        var order = new EngineOrder { Customer = new EngineCustomer() };
        var validator = new SlowLiveRuleValidator();

        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<EngineOrder>>(0);
            builder.AddComponentParameter(1, "Model", order);
            builder.AddComponentParameter(2, "Validator", new FluentValidationModelValidator<EngineOrder>(validator));
            builder.AddComponentParameter(3, "ChildContent", (RenderFragment)(inner =>
            {
                inner.OpenComponent<FormidableInputText>(0);
                inner.AddComponentParameter(1, "For", (Expression<Func<string?>>)(() => order.Description));
                inner.AddComponentParameter(2, "Value", order.Description);
                inner.AddComponentParameter(3, "ValueChanged",
                    EventCallback.Factory.Create<string?>(this, v => order.Description = v ?? string.Empty));
                inner.CloseComponent();

                inner.OpenComponent<FormidableInputText>(4);
                inner.AddComponentParameter(5, "For", (Expression<Func<string?>>)(() => order.Customer!.Name));
                inner.AddComponentParameter(6, "Value", order.Customer!.Name);
                inner.AddComponentParameter(7, "ValueChanged",
                    EventCallback.Factory.Create<string?>(this, v => order.Customer!.Name = v ?? string.Empty));
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        });

        var engine = cut.FindComponent<FormidableForm<EngineOrder>>().Instance.Engine!;

        // The second input is never edited and never validating, so every render it does is a round
        // the edited field's pass raised — the cost this pins is the one it pays for nothing.
        var bystander = cut.FindComponents<FormidableInputText>()[1];

        // Touch the field up front rather than by typing into it: a field's first touch raises a
        // round of its own, and counting that alongside the pass's would measure the touch.
        await cut.InvokeAsync(() =>
            engine.MarkTouched(new FieldIdentifier(order, nameof(EngineOrder.Description))));

        var rounds = 0;
        engine.StateChanged += () => rounds++;
        var rendersBefore = bystander.RenderCount;

        cut.Find("input").Change("typed");
        Assert.True(engine.IsValidating, "the edit should have started a pass");

        // Quiescence is taken from a StateChanged handler rather than from the rendered markup or
        // the flag alone: the flag is cleared a moment before the round that publishes it, so a
        // settle that polls anything else can return between the two and miss the round it is
        // counting. Subscribed only now the pass is confirmed in flight, so the rounds raised
        // before it cannot resolve quiescence early.
        var quiescent = new TaskCompletionSource();
        void OnStateChanged()
        {
            if (!engine.IsValidating)
            {
                quiescent.TrySetResult();
            }
        }

        engine.StateChanged += OnStateChanged;
        await cut.InvokeAsync(() => validator.Gate.SetResult());
        await quiescent.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, rounds);
        cut.WaitForAssertion(() => Assert.True(
            bystander.RenderCount > rendersBefore,
            "a field the keystroke never touched should still have re-rendered on the pass's rounds"));
    }
}
