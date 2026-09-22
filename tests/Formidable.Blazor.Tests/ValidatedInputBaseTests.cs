using Bunit;
using Formidable.Blazor.Tests.Fixtures;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;

namespace Formidable.Blazor.Tests;

public class ValidatedInputBaseTests : BunitContext
{
    public ValidatedInputBaseTests()
    {
        Services.AddFormidable();
        Services.AddSingleton<FluentValidation.IValidator<EngineOrder>, EngineOrderValidator>();
    }

    private IRenderedComponent<FormidableForm<EngineOrder>> RenderInput(
        EngineOrder order, InputUpdateMode updateOn = InputUpdateMode.OnChange)
    {
        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<EngineOrder>>(0);
            builder.AddComponentParameter(1, "Model", order);
            builder.AddComponentParameter(2, "ChildContent", (RenderFragment)(inner =>
            {
                inner.OpenComponent<FormidableInputText>(0);
                inner.AddComponentParameter(1, "For", (System.Linq.Expressions.Expression<Func<string?>>)(() => order.Description));
                inner.AddComponentParameter(2, "Value", order.Description);
                inner.AddComponentParameter(3, "ValueChanged",
                    EventCallback.Factory.Create<string?>(this, v => order.Description = v ?? string.Empty));
                inner.AddComponentParameter(4, "UpdateOn", updateOn);
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        });
        return cut.FindComponent<FormidableForm<EngineOrder>>();
    }

    private IRenderedComponent<FormidableForm<EngineOrder>> RenderInputWithAttributes(
        EngineOrder order, params (string Name, object Value)[] attributes)
    {
        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<EngineOrder>>(0);
            builder.AddComponentParameter(1, "Model", order);
            builder.AddComponentParameter(2, "ChildContent", (RenderFragment)(inner =>
            {
                inner.OpenComponent<FormidableInputText>(0);
                inner.AddComponentParameter(1, "For", (System.Linq.Expressions.Expression<Func<string?>>)(() => order.Description));
                inner.AddComponentParameter(2, "Value", order.Description);
                inner.AddComponentParameter(3, "ValueChanged",
                    EventCallback.Factory.Create<string?>(this, v => order.Description = v ?? string.Empty));
                var sequence = 4;
                foreach (var (name, value) in attributes)
                {
                    inner.AddComponentParameter(sequence++, name, value);
                }

                inner.CloseComponent();
            }));
            builder.CloseComponent();
        });
        return cut.FindComponent<FormidableForm<EngineOrder>>();
    }

    [Fact]
    public void Change_updates_model_and_triggers_live_validation()
    {
        var order = new EngineOrder();
        var form = RenderInput(order);

        form.Find("input").Change(new string('x', 11));

        Assert.Equal(new string('x', 11), order.Description);
        form.WaitForAssertion(() => Assert.Contains("formidable-invalid", form.Find("input").GetAttribute("class")));
    }

    [Fact]
    public void Input_mode_binds_oninput()
    {
        var order = new EngineOrder();
        var form = RenderInput(order, InputUpdateMode.OnInput);

        form.Find("input").Input("hello");

        Assert.Equal("hello", order.Description);
    }

    [Fact]
    public void Aria_attributes_reflect_error_state()
    {
        var order = new EngineOrder { Description = new string('x', 11) };
        var form = RenderInput(order);

        form.Find("input").Change(order.Description); // same value; still triggers validation

        form.WaitForAssertion(() =>
        {
            var input = form.Find("input");
            Assert.Equal("true", input.GetAttribute("aria-invalid"));
            Assert.Equal($"{input.GetAttribute("id")}-messages", input.GetAttribute("aria-describedby"));
        });
    }

    [Fact]
    public void Input_registers_for_disclosure()
    {
        var order = new EngineOrder();
        var form = RenderInput(order);

        Assert.True(form.Instance.Engine!.Registry.IsRevealed(new FieldIdentifier(order, nameof(EngineOrder.Description))));
    }

    [Fact]
    public void Element_id_matches_field_id_convention()
    {
        var order = new EngineOrder();
        var form = RenderInput(order);

        Assert.Equal(
            FormidableFieldId.For(new FieldIdentifier(order, nameof(EngineOrder.Description))),
            form.Find("input").GetAttribute("id"));
    }

    // The three tests below pin attribute precedence between the consumer's splat and the values
    // the component computes. A splatted `class` must MERGE with the state class (clobbering it
    // would silently kill the invalid/valid styling); a splatted `id` must LOSE, because messages,
    // aria-describedby and the focus service all address the field by its deterministic id.
    [Fact]
    public void Splatted_class_merges_with_the_computed_state_class()
    {
        var order = new EngineOrder();
        var form = RenderInputWithAttributes(order, ("class", "form-control"));

        form.Find("input").Change(new string('x', 11));

        form.WaitForAssertion(() =>
        {
            var css = form.Find("input").GetAttribute("class");
            Assert.Contains("form-control", css);
            Assert.Contains("formidable-invalid", css);
        });
    }

    [Fact]
    public void Splatted_id_does_not_override_the_deterministic_field_id()
    {
        var order = new EngineOrder();
        var form = RenderInputWithAttributes(order, ("id", "consumer-supplied-id"));

        Assert.Equal(
            FormidableFieldId.For(new FieldIdentifier(order, nameof(EngineOrder.Description))),
            form.Find("input").GetAttribute("id"));
    }

    [Fact]
    public void Unrelated_splatted_attributes_pass_through()
    {
        var order = new EngineOrder();
        var form = RenderInputWithAttributes(order, ("placeholder", "Order description"));

        Assert.Equal("Order description", form.Find("input").GetAttribute("placeholder"));
    }

    // Regression test mirroring FormidableFieldTests.Field_rebinds_when_the_cascaded_context_is_replaced_without_a_host_remount:
    // ValidatedInputBase must rebind its registration AND its engine StateChanged subscription
    // when the cascaded FormidableFormContext instance changes, independent of a host remount.
    // Cascading the context directly (no EditForm underneath) isolates this from EditForm's own
    // subtree-recreation behaviour on EditContext swap (see the sibling test's remarks for why
    // a test built on top of FormidableForm cannot distinguish fixed from unfixed component code).
    [Fact]
    public void Input_rebinds_when_the_cascaded_context_is_replaced_without_a_host_remount()
    {
        var order = new EngineOrder();
        var field = new FieldIdentifier(order, nameof(EngineOrder.Description));
        RenderFragment inputFragment = inner =>
        {
            inner.OpenComponent<FormidableInputText>(0);
            inner.AddComponentParameter(1, "For", (System.Linq.Expressions.Expression<Func<string?>>)(() => order.Description));
            inner.AddComponentParameter(2, "Value", order.Description);
            inner.AddComponentParameter(3, "ValueChanged",
                EventCallback.Factory.Create<string?>(this, v => order.Description = v ?? string.Empty));
            inner.CloseComponent();
        };

        var firstEngine = CreateEngine(order);
        var firstContext = new FormidableFormContext(firstEngine);
        var cut = Render(builder =>
        {
            builder.OpenComponent<CascadedContextHost>(0);
            builder.AddComponentParameter(1, nameof(CascadedContextHost.Context), firstContext);
            builder.AddComponentParameter(2, nameof(CascadedContextHost.ChildContent), inputFragment);
            builder.CloseComponent();
        });

        Assert.True(firstEngine.Registry.IsRevealed(field));

        var secondEngine = CreateEngine(order);
        var secondContext = new FormidableFormContext(secondEngine);
        var host = cut.FindComponent<CascadedContextHost>();
        host.Render(parameters =>
        {
            parameters.Add(p => p.Context, secondContext);
            parameters.Add(p => p.ChildContent, inputFragment);
        });

        Assert.True(secondEngine.Registry.IsRevealed(field));
        Assert.False(firstEngine.Registry.IsRevealed(field));

        // The rendered input must now reflect the NEW engine: a live-pass validation on the new
        // engine's EditContext should reach it. If the StateChanged subscription were still wired
        // to the disposed first engine, this would never update and the WaitForAssertion below
        // would time out.
        order.Description = new string('x', 11);
        cut.InvokeAsync(() => secondEngine.EditContext.NotifyFieldChanged(field));

        cut.WaitForAssertion(() =>
        {
            var input = cut.Find("input");
            Assert.Contains("formidable-invalid", input.GetAttribute("class"));
        });
    }

    private static FormValidationEngine<EngineOrder> CreateEngine(EngineOrder order) =>
        new(order, new EditContext(order),
            new FluentValidationModelValidator<EngineOrder>(new EngineOrderValidator()),
            new Formidable.Introspection.ReflectionModelIntrospector(),
            new FormidableOptions());

    /// <summary>Test-only host that cascades a FormidableFormContext directly, with no EditForm underneath (see the rebind test's remarks).</summary>
    private sealed class CascadedContextHost : ComponentBase
    {
        [Parameter]
        public FormidableFormContext? Context { get; set; }

        [Parameter]
        public RenderFragment? ChildContent { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<CascadingValue<FormidableFormContext>>(0);
            builder.AddComponentParameter(1, "Value", Context);
            builder.AddComponentParameter(2, "ChildContent", ChildContent);
            builder.CloseComponent();
        }
    }
}
