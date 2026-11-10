#if NET11_0_OR_GREATER
using System.Reflection;
using Bunit;
using FluentValidation;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;

namespace Formidable.Blazor.Tests;

/// <summary>
/// On .NET 11 the engine listens to its <c>EditContext</c>'s validation-state notification, and lets
/// go of it when the engine goes: a <see cref="FormidableForm{TModel}"/> disposed, and an attach-mode
/// <see cref="FormidableValidator{TModel}"/> moved to another <c>EditContext</c> or disposed. An
/// <c>EditContext</c> that outlives the engine (a page's own, under attach mode) then holds no
/// reference to it.
/// </summary>
// The handler also stands down once the engine is disposed, so a kept subscription changes no
// answer; it would hold the engine for the EditContext's lifetime. So the pins count the engine's
// handlers on the EditContext's event itself. Mutation that must break both: drop the
// unsubscription from the engine's Dispose.
public class EngineValidationStateSubscriptionTests : BunitContext
{
    public EngineValidationStateSubscriptionTests() => Services.AddFormidable();

    [Fact]
    public async Task A_disposed_form_lets_go_of_its_EditContext_s_notification()
    {
        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<Profile>>(0);
            builder.AddComponentParameter(1, "Model", new Profile());
            builder.AddComponentParameter(2, "Validator", new FluentValidationModelValidator<Profile>(new ProfileValidator()));
            builder.AddComponentParameter(3, "ChildContent", (RenderFragment<FormidableFormContext>)(_ => _ => { }));
            builder.CloseComponent();
        });
        var editContext = cut.FindComponent<FormidableForm<Profile>>().Instance.Engine!.EditContext;
        Assert.Equal(1, EngineHandlers(editContext));

        await DisposeComponentsAsync();

        Assert.Equal(0, EngineHandlers(editContext));
    }

    [Fact]
    public async Task An_attached_validator_lets_go_of_each_EditContext_it_leaves()
    {
        var first = new EditContext(new Profile());
        var second = new EditContext(new Profile());
        var cut = Render<AttachHost>(p => p.Add(h => h.EditContext, first));
        Assert.Equal(1, EngineHandlers(first));

        cut.Render(p => p.Add(h => h.EditContext, second));
        Assert.Equal(0, EngineHandlers(first));
        Assert.Equal(1, EngineHandlers(second));

        await DisposeComponentsAsync();
        Assert.Equal(0, EngineHandlers(second));
    }

    // The handlers an engine put on the event: a field-like event keeps its delegate in a field of
    // the event's own name.
    private static int EngineHandlers(EditContext editContext)
    {
        var field = typeof(EditContext).GetField(nameof(EditContext.OnValidationStateChanged), BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("EditContext keeps OnValidationStateChanged's handlers elsewhere.");
        var handlers = (Delegate?)field.GetValue(editContext);
        return handlers?.GetInvocationList().Count(handler => handler.Target is IFormidableEngine) ?? 0;
    }

    public sealed class Profile
    {
        public string? Nickname { get; set; }
    }

    private sealed class ProfileValidator : AbstractValidator<Profile>
    {
        public ProfileValidator() => RuleFor(x => x.Nickname).MaximumLength(40);
    }

    /// <summary>An <see cref="EditForm"/> over the given <c>EditContext</c> with a validator attached.</summary>
    private sealed class AttachHost : ComponentBase
    {
        [Parameter]
        public EditContext EditContext { get; set; } = default!;

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<EditForm>(0);
            builder.AddComponentParameter(1, nameof(EditForm.EditContext), EditContext);
            builder.AddComponentParameter(2, nameof(EditForm.ChildContent), (RenderFragment<EditContext>)(_ => inner =>
            {
                inner.OpenComponent<FormidableValidator<Profile>>(0);
                inner.AddComponentParameter(1, nameof(FormidableValidator<Profile>.Validator),
                    new FluentValidationModelValidator<Profile>(new ProfileValidator()));
                inner.AddComponentParameter(2, nameof(FormidableValidator<Profile>.ChildContent), (RenderFragment<FormidableFormContext>)(_ => _ => { }));
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        }
    }
}
#endif
