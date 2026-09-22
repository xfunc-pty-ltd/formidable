using Bunit;
using Formidable.Blazor.Tests.Fixtures;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;

namespace Formidable.Blazor.Tests;

/// <summary>
/// Where a native <c>InputBase</c>'s state class gets its NAMES from. The kit reads
/// <c>Engine.Options.CssClasses</c> at each render, so the two surfaces agree only if the
/// provider the engine installs for native inputs reads the same property at each computation
/// rather than keeping whatever instance it was constructed with. Renders a real native
/// <c>InputText</c> beside a Formidable one, both bound to the same field, so a divergence shows
/// up as the two elements disagreeing about one field's state.
/// </summary>
public class NativeInputCssClassSourceTests : BunitContext
{
    public NativeInputCssClassSourceTests() => Services.AddFormidable();

    /// <summary>
    /// Replacing the <c>CssClasses</c> instance reaches native inputs, not just kit ones.
    /// Mutation that must break it: hold the instance in the provider — a <c>_classes</c> field
    /// assigned from a constructor parameter — and the native input keeps painting
    /// <c>formidable-invalid</c> while the Formidable one beside it moves to <c>is-invalid</c>.
    /// </summary>
    [Fact]
    public void Replacing_the_class_map_reaches_a_native_input_and_a_kit_one_together()
    {
        var order = new EngineOrder();
        var options = new FormidableOptions();

        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<EngineOrder>>(0);
            builder.AddComponentParameter(1, "Model", order);
            builder.AddComponentParameter(2, "Validator",
                new FluentValidationModelValidator<EngineOrder>(new EngineOrderValidator()));
            builder.AddComponentParameter(3, "Options", options);
            builder.AddComponentParameter(4, "ChildContent", (RenderFragment<FormidableFormContext>)(_ => inner =>
            {
                inner.OpenComponent<InputText>(0);
                inner.AddComponentParameter(1, "Value", order.Description);
                inner.AddComponentParameter(2, "ValueChanged",
                    EventCallback.Factory.Create<string?>(this, v => order.Description = v ?? string.Empty));
                inner.AddComponentParameter(3, "ValueExpression",
                    (System.Linq.Expressions.Expression<Func<string?>>)(() => order.Description));
                inner.CloseComponent();

                inner.OpenComponent<FormidableInputText>(4);
                inner.AddComponentParameter(5, "Value", order.Description);
                inner.AddComponentParameter(6, "ValueChanged",
                    EventCallback.Factory.Create<string?>(this, v => order.Description = v ?? string.Empty));
                inner.AddComponentParameter(7, "ValueExpression",
                    (System.Linq.Expressions.Expression<Func<string?>>)(() => order.Description));
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        });

        // EngineOrderValidator's draft rule caps Description at ten characters, so this fails
        // both inputs at once and synchronously.
        cut.FindAll("input")[0].Change("far more than ten characters");

        cut.WaitForAssertion(() =>
        {
            var inputs = cut.FindAll("input");
            Assert.Contains("formidable-invalid", inputs[0].GetAttribute("class"));
            Assert.Contains("formidable-invalid", inputs[1].GetAttribute("class"));
        });

        // A whole new instance, which is what options.md's own app-wide example assigns, and the
        // case a provider holding its construction instance could not follow.
        options.CssClasses = new FormidableCssClasses { Invalid = "is-invalid" };

        cut.FindAll("input")[0].Change("still far more than ten characters");

        cut.WaitForAssertion(() =>
        {
            var inputs = cut.FindAll("input");
            Assert.Contains("is-invalid", inputs[0].GetAttribute("class"));
            Assert.DoesNotContain("formidable-invalid", inputs[0].GetAttribute("class"));
            Assert.Contains("is-invalid", inputs[1].GetAttribute("class"));
        });
    }
}
