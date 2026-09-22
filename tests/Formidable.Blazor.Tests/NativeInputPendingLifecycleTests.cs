using Bunit;
using Formidable.Blazor.Tests.Fixtures;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;

namespace Formidable.Blazor.Tests;

/// <summary>
/// A native InputBase re-renders on EditContext.OnValidationStateChanged, not on the engine's own
/// StateChanged event — unlike a Formidable input, which subscribes to StateChanged directly.
/// Renders a real native InputText inside a FormidableForm (going through the framework's own
/// render pipeline, not a direct string-computation check) to prove the Pending class both
/// appears while a pass is in flight and clears once it ends.
/// </summary>
public class NativeInputPendingLifecycleTests : BunitContext
{
    public NativeInputPendingLifecycleTests() => Services.AddFormidable();

    [Fact]
    public async Task Native_input_pending_class_appears_during_the_pass_and_clears_after_it()
    {
        var order = new EngineOrder();
        var validator = new GatedValidator();

        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<EngineOrder>>(0);
            builder.AddComponentParameter(1, "Model", order);
            builder.AddComponentParameter(2, "Validator", new FluentValidationModelValidator<EngineOrder>(validator));
            builder.AddComponentParameter(3, "Options", new FormidableOptions());
            builder.AddComponentParameter(4, "ChildContent", (RenderFragment)(inner =>
            {
                inner.OpenComponent<InputText>(0);
                inner.AddComponentParameter(1, "Value", order.Description);
                inner.AddComponentParameter(2, "ValueChanged",
                    EventCallback.Factory.Create<string?>(this, v => order.Description = v ?? string.Empty));
                inner.AddComponentParameter(3, "ValueExpression",
                    (System.Linq.Expressions.Expression<Func<string?>>)(() => order.Description));
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        });

        cut.Find("input").Change("hi"); // starts the live pass; GatedValidator's async rule blocks on Gate

        cut.WaitForAssertion(() =>
            Assert.Contains("formidable-pending", cut.Find("input").GetAttribute("class")));

        await cut.InvokeAsync(() => validator.Gate.SetResult());

        cut.WaitForAssertion(() =>
            Assert.DoesNotContain("formidable-pending", cut.Find("input").GetAttribute("class")));
    }
}
