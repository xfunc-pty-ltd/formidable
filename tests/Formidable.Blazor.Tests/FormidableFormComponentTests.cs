using Bunit;
using Formidable.Blazor.Tests.Fixtures;
using Microsoft.AspNetCore.Components;
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

    [Fact]
    public async Task SubmitAsync_is_available_programmatically()
    {
        var cut = RenderForm(new EngineOrder());

        SubmitOutcome? outcome = null;
        await cut.InvokeAsync(async () => outcome = await cut.Instance.SubmitAsync());

        Assert.NotNull(outcome);
        Assert.False(outcome!.CanProceed);
    }
}
