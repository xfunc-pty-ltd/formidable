using Bunit;
using Formidable.Blazor.Tests.Fixtures;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;

namespace Formidable.Blazor.Tests;

public class FormidableFormComponentTests : BunitContext
{
    public FormidableFormComponentTests()
    {
        Services.AddFormidable();
        Services.AddSingleton<FluentValidation.IValidator<EngineOrder>, EngineOrderValidator>();
    }

    private IRenderedComponent<FormidableForm<EngineOrder>> RenderForm(
        EngineOrder order,
        Action<SubmitOutcome>? onInvalid = null,
        Action? onValid = null)
    {
        var container = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<EngineOrder>>(0);
            builder.AddComponentParameter(1, nameof(FormidableForm<EngineOrder>.Model), order);
            builder.AddComponentParameter(
                2,
                nameof(FormidableForm<EngineOrder>.Options),
                new FormidableOptions { DisclosureOverride = _ => true });
            builder.AddComponentParameter(
                3,
                nameof(FormidableForm<EngineOrder>.OnInvalidSubmit),
                EventCallback.Factory.Create<SubmitOutcome>(this, outcome => onInvalid?.Invoke(outcome)));
            builder.AddComponentParameter(
                4,
                nameof(FormidableForm<EngineOrder>.OnValidSubmit),
                EventCallback.Factory.Create(this, () => onValid?.Invoke()));
            builder.AddComponentParameter(
                5,
                nameof(FormidableForm<EngineOrder>.ChildContent),
                (RenderFragment)(inner => inner.AddMarkupContent(0, "<button type=\"submit\">Go</button>")));
            builder.CloseComponent();
        });

        return container.FindComponent<FormidableForm<EngineOrder>>();
    }

    [Fact]
    public void Renders_a_form_element_with_child_content()
    {
        var cut = RenderForm(new EngineOrder());

        Assert.NotNull(cut.Find("form"));
        Assert.NotNull(cut.Find("button[type=submit]"));
    }

    [Fact]
    public void Invalid_submit_invokes_callback_with_outcome_and_shows_messages()
    {
        SubmitOutcome? outcome = null;
        var cut = RenderForm(new EngineOrder(), onInvalid: o => outcome = o);

        cut.Find("form").Submit();

        cut.WaitForAssertion(() =>
        {
            Assert.NotNull(outcome);
            Assert.False(outcome!.CanProceed);
            Assert.Contains("Order description", outcome.VisibleErrorSummary);
        });
    }

    [Fact]
    public void Valid_submit_invokes_valid_callback()
    {
        var valid = false;
        var order = new EngineOrder { Description = "ok", Customer = new EngineCustomer() };
        var cut = RenderForm(order, onValid: () => valid = true);

        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.True(valid));
    }

    [Fact]
    public void Model_swap_rebuilds_edit_context_and_resets_state()
    {
        var first = new EngineOrder();
        var cut = RenderForm(first);
        cut.Find("form").Submit();
        cut.WaitForAssertion(() => Assert.True(cut.Instance.Engine!.HasSubmitted));

        var second = new EngineOrder();
        cut.Render(parameters => parameters.Add(p => p.Model, second));

        Assert.False(cut.Instance.Engine!.HasSubmitted);
        Assert.Same(second, cut.Instance.Engine.EditContext.Model);
    }

    // Safety verification for the form's IsFixed="true" cascade: a Model swap must still reach a
    // BARE kit input (no FormidableField wrapper) with the new engine, even though the cascade no
    // longer notifies subscribers on every unrelated re-render. It does, because the cascade's own
    // region is keyed on the form's context — the same context a Model swap replaces — so the swap
    // destroys the cascade itself and everything below it, and the fresh instances that replace
    // them (the input included) are mounted for the first time, reading the new context on their
    // own first render rather than depending on a notification they never needed.
    [Fact]
    public void Model_swap_still_rebinds_a_bare_input_to_the_new_engine()
    {
        var first = new EngineOrder();
        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<EngineOrder>>(0);
            builder.AddComponentParameter(1, "Model", first);
            builder.AddComponentParameter(2, "ChildContent", InputBoundTo(first, this));
            builder.CloseComponent();
        });

        var form = cut.FindComponent<FormidableForm<EngineOrder>>();
        var firstEngine = form.Instance.Engine!;
        Assert.True(firstEngine.Registry.IsRevealed(new FieldIdentifier(first, nameof(EngineOrder.Description))));

        var second = new EngineOrder();
        form.Render(parameters => parameters
            .Add(p => p.Model, second)
            .Add(p => p.ChildContent, InputBoundTo(second, this)));

        var secondEngine = form.Instance.Engine!;
        Assert.NotSame(firstEngine, secondEngine);
        var secondField = new FieldIdentifier(second, nameof(EngineOrder.Description));
        Assert.True(secondEngine.Registry.IsRevealed(secondField));

        // The rebuilt input's live pass must reach the NEW engine — if the input were still
        // wired to the disposed first engine, this would never turn invalid and the
        // WaitForAssertion below would time out.
        second.Description = new string('x', 11);
        cut.InvokeAsync(() => secondEngine.EditContext.NotifyFieldChanged(secondField));

        cut.WaitForAssertion(() => Assert.Contains("formidable-invalid", cut.Find("input").GetAttribute("class")));
    }

    private static RenderFragment InputBoundTo(EngineOrder order, object receiver) => inner =>
    {
        inner.OpenComponent<FormidableInputText>(0);
        inner.AddComponentParameter(1, "For", (System.Linq.Expressions.Expression<Func<string?>>)(() => order.Description));
        inner.AddComponentParameter(2, "Value", order.Description);
        inner.AddComponentParameter(3, "ValueChanged",
            EventCallback.Factory.Create<string?>(receiver, v => order.Description = v ?? string.Empty));
        inner.CloseComponent();
    };

    [Fact]
    public async Task SubmitAsync_is_available_programmatically()
    {
        var cut = RenderForm(new EngineOrder());

        SubmitOutcome? outcome = null;
        await cut.InvokeAsync(async () => outcome = await cut.Instance.SubmitAsync());

        Assert.NotNull(outcome);
        Assert.False(outcome!.CanProceed);
    }

    [Fact]
    public void Form_element_carries_the_model_level_id_and_tabindex()
    {
        var order = new EngineOrder();
        var cut = RenderForm(order);

        var form = cut.Find("form");
        Assert.Equal(FormidableFieldId.For(new FieldIdentifier(order, string.Empty)), form.GetAttribute("id"));
        Assert.Equal("-1", form.GetAttribute("tabindex"));
    }

    [Fact]
    public void Consumer_supplied_id_and_tabindex_are_ignored()
    {
        // Mirrors FormidableInputBase<TValue>'s own policy: the deterministic id is what the
        // focus service and the all-suppressed gate address the form by, so a consumer-splatted
        // id/tabindex loses the duplicate-attribute race rather than winning it.
        var order = new EngineOrder();
        var container = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<EngineOrder>>(0);
            builder.AddComponentParameter(1, nameof(FormidableForm<EngineOrder>.Model), order);
            builder.AddAttribute(2, "id", "consumer-id");
            builder.AddAttribute(3, "tabindex", "3");
            builder.AddComponentParameter(
                4,
                nameof(FormidableForm<EngineOrder>.ChildContent),
                (RenderFragment)(inner => inner.AddMarkupContent(0, "<button type=\"submit\">Go</button>")));
            builder.CloseComponent();
        });

        var form = container.Find("form");
        Assert.Equal(FormidableFieldId.For(new FieldIdentifier(order, string.Empty)), form.GetAttribute("id"));
        Assert.Equal("-1", form.GetAttribute("tabindex"));
    }
}
