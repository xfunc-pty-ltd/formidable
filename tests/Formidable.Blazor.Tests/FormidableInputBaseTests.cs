using Bunit;
using Formidable.Blazor.Tests.Fixtures;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;

namespace Formidable.Blazor.Tests;

public class FormidableInputBaseTests : BunitContext
{
    public FormidableInputBaseTests()
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

    // Pins the split OnBlur adds: the value commits on change alone, with no engine notification
    // riding along — so a rule that would fail (11 chars, past EngineOrderValidator's max) commits
    // to the model but starts no live pass, and the class stays exactly what an untouched field
    // renders (empty; see FormidableCss.Compute).
    [Fact]
    public void Blur_mode_commits_the_value_on_change_without_starting_a_live_pass()
    {
        var order = new EngineOrder();
        var form = RenderInput(order, InputUpdateMode.OnBlur);

        form.Find("input").Change(new string('x', 11));

        Assert.Equal(new string('x', 11), order.Description);
        Assert.Equal(string.Empty, form.Find("input").GetAttribute("class"));
    }

    // The other half: once the committed value above is followed by blur, the engine is notified
    // and the live pass that was withheld on change now runs.
    [Fact]
    public void Blur_mode_notifies_the_engine_on_blur()
    {
        var order = new EngineOrder();
        var form = RenderInput(order, InputUpdateMode.OnBlur);

        form.Find("input").Change(new string('x', 11));
        form.Find("input").Blur();

        form.WaitForAssertion(() => Assert.Contains("formidable-invalid", form.Find("input").GetAttribute("class")));
    }

    // Pins the commit gate on the blur-mode notification: blur delivers a pending commit
    // notification and never invents one, so focus-then-leave with no committed change fires no
    // OnFieldChanged, touches nothing, and paints no state class. An unconditional blur
    // notification breaks all three.
    [Fact]
    public void A_blur_with_no_committed_change_notifies_nothing()
    {
        var order = new EngineOrder();
        var form = RenderInput(order, InputUpdateMode.OnBlur);
        var notifications = 0;
        form.Instance.Engine!.EditContext.OnFieldChanged += (_, _) => notifications++;

        form.Find("input").Blur();

        Assert.Equal(0, notifications);
        Assert.False(form.Instance.Engine!.GetFieldState(
            new FieldIdentifier(order, nameof(EngineOrder.Description))).IsTouched);
        Assert.Equal(string.Empty, form.Find("input").GetAttribute("class"));
    }

    // Pins arm-and-consume: one commit arms exactly one notification, the first blur delivers
    // it, and the second blur — with nothing committed in between — delivers nothing. Notifying
    // per blur, or delivering without disarming, breaks the final count.
    [Fact]
    public void A_second_blur_after_one_commit_notifies_once()
    {
        var order = new EngineOrder();
        var form = RenderInput(order, InputUpdateMode.OnBlur);
        var notifications = 0;
        form.Instance.Engine!.EditContext.OnFieldChanged += (_, _) => notifications++;

        form.Find("input").Change(new string('x', 11));
        Assert.Equal(0, notifications);

        form.Find("input").Blur();
        Assert.Equal(1, notifications);

        form.Find("input").Blur();
        Assert.Equal(1, notifications);
    }

    // Pins coalescing: two commits between blurs — the shape a date input's per-segment change
    // events produce — arm one notification, delivered once at the next blur. Notifying per
    // commit breaks the count.
    [Fact]
    public void Two_commits_before_one_blur_notify_once()
    {
        var order = new EngineOrder();
        var form = RenderInput(order, InputUpdateMode.OnBlur);
        var notifications = 0;
        form.Instance.Engine!.EditContext.OnFieldChanged += (_, _) => notifications++;

        form.Find("input").Change("first");
        form.Find("input").Change("second");
        Assert.Equal(0, notifications);

        form.Find("input").Blur();

        Assert.Equal(1, notifications);
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

        Assert.True(form.Instance.Engine!.Registry.IsRegistered(new FieldIdentifier(order, nameof(EngineOrder.Description))));
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

    // Pins a stale-cascade regression: a bare kit input (no FormidableField wrapper — wrapping it
    // would mask the symptom, since FormidableField is itself a cascade subscriber notified BEFORE
    // the input in subscription order, so its own re-render would overwrite the input's parameters
    // with the fresh value first) sits behind a NON-fixed CascadingValue<FormidableFormContext>
    // carrying the SAME context instance throughout the test — no replacement, unlike the rebind
    // test below. A host that re-renders as a normal consequence of ValueChanged (exactly what
    // @bind-Value does on a real page) causes Blazor to re-notify the cascade's subscribers with a
    // stale direct-parameters snapshot taken before the keystroke — so the input can be handed
    // back the value it just replaced, one render after committing the new one. This fails at
    // IsFixed=false (the sequence regresses to the pre-commit value after the fresh commit render)
    // and passes once the cascade is IsFixed=true.
    [Fact]
    public void A_bare_input_is_never_handed_back_the_value_it_just_replaced()
    {
        var order = new EngineOrder();
        var cut = Render(builder =>
        {
            builder.OpenComponent<DescriptionHost>(0);
            builder.AddComponentParameter(1, nameof(DescriptionHost.Order), order);
            builder.CloseComponent();
        });

        var tracker = cut.FindComponent<ValueTrackingInput>().Instance;
        tracker.RenderedValues.Clear(); // only the renders caused by the commit below matter

        cut.Find("input").Change("hello");

        cut.WaitForAssertion(() => Assert.Contains("hello", tracker.RenderedValues));

        var committedAt = tracker.RenderedValues.IndexOf("hello");
        for (var i = committedAt + 1; i < tracker.RenderedValues.Count; i++)
        {
            Assert.True(
                tracker.RenderedValues[i] == "hello",
                $"render {i} handed the input '{tracker.RenderedValues[i]}' after it had already committed 'hello' (full sequence: {string.Join(", ", tracker.RenderedValues)})");
        }
    }

    /// <summary>
    /// Renders <see cref="ValueTrackingInput"/> bare inside a <c>FormidableForm</c>, with
    /// <c>ValueChanged</c> bound to THIS component — mirroring a real consumer page, where
    /// committing a value auto-renders the owning page (the same mechanism <c>@bind-Value</c>
    /// relies on) and that page's re-render re-supplies <c>FormidableForm</c>'s ChildContent.
    /// </summary>
    private sealed class DescriptionHost : ComponentBase
    {
        [Parameter]
        public EngineOrder Order { get; set; } = default!;

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<FormidableForm<EngineOrder>>(0);
            builder.AddComponentParameter(1, "Model", Order);
            builder.AddComponentParameter(2, "ChildContent", (RenderFragment)(inner =>
            {
                inner.OpenComponent<ValueTrackingInput>(0);
                inner.AddComponentParameter(1, "For", (System.Linq.Expressions.Expression<Func<string?>>)(() => Order.Description));
                inner.AddComponentParameter(2, "Value", Order.Description);
                inner.AddComponentParameter(3, "ValueChanged",
                    EventCallback.Factory.Create<string?>(this, v => Order.Description = v ?? string.Empty));
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        }
    }

    /// <summary>Records the <c>Value</c> it renders on every <c>BuildRenderTree</c> call, in order.</summary>
    private sealed class ValueTrackingInput : FormidableInputBase<string?>
    {
        public List<string?> RenderedValues { get; } = new();

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            RenderedValues.Add(Value);
            builder.OpenElement(0, "input");
            AddCommonAttributes(builder, 1);
            builder.AddAttribute(5, "value", Value);
            AddValueBinding(builder, 6);
            builder.CloseElement();
        }
    }

    // Regression test mirroring FormidableFieldTests.Field_rebinds_when_the_cascaded_context_is_replaced_without_a_host_remount:
    // FormidableInputBase must rebind its registration AND its engine StateChanged subscription
    // when the cascaded FormidableFormContext instance is REPLACED BY A GENUINELY DIFFERENT
    // INSTANCE, independent of a host remount — distinct from the stale-re-supply test above,
    // where the instance never changes at all. Cascading the context directly (no EditForm
    // underneath) isolates this from EditForm's own subtree-recreation behaviour on EditContext
    // swap (see the sibling test's remarks for why a test built on top of FormidableForm cannot
    // distinguish fixed from unfixed component code).
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

        Assert.True(firstEngine.Registry.IsRegistered(field));

        var secondEngine = CreateEngine(order);
        var secondContext = new FormidableFormContext(secondEngine);
        var host = cut.FindComponent<CascadedContextHost>();
        host.Render(parameters =>
        {
            parameters.Add(p => p.Context, secondContext);
            parameters.Add(p => p.ChildContent, inputFragment);
        });

        Assert.True(secondEngine.Registry.IsRegistered(field));
        Assert.False(firstEngine.Registry.IsRegistered(field));

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
