using Bunit;
using Formidable.Blazor.Tests.Fixtures;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;

namespace Formidable.Blazor.Tests;

// The component's contract, pinned surface by surface: the model-level list persists empty on a
// clean form (id, class, no role), and carries a configured InlineMessageRole on that persistent
// element; the all-suppressed defensive gate — the reason the component exists — reaches the
// screen through it on a form that renders no summary; the incomplete-validation fault issue
// renders after the gate, in the order GetIssues documents; the same list renders under
// FormidableValidator as under FormidableForm; and the splat follows the shared message-list
// policy. Validators are registered per test rather than in the constructor, because the fault
// pin needs a validator whose live rule throws.
public class FormidableModelMessageTests : BunitContext
{
    public FormidableModelMessageTests()
    {
        Services.AddFormidable();
    }

    private IRenderedComponent<FormidableForm<EngineOrder>> RenderWithModelMessage(
        EngineOrder order,
        FormidableOptions? options = null,
        IReadOnlyDictionary<string, object>? splat = null)
    {
        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<EngineOrder>>(0);
            builder.AddComponentParameter(1, "Model", order);
            if (options is not null)
            {
                builder.AddComponentParameter(2, "Options", options);
            }

            builder.AddComponentParameter(3, "ChildContent", (RenderFragment<FormidableFormContext>)(_ => inner =>
            {
                inner.OpenComponent<FormidableModelMessage>(0);
                if (splat is not null)
                {
                    var sequence = 1;
                    foreach (var (name, value) in splat)
                    {
                        inner.AddComponentParameter(sequence++, name, value);
                    }
                }

                inner.CloseComponent();
            }));
            builder.CloseComponent();
        });
        return cut.FindComponent<FormidableForm<EngineOrder>>();
    }

    [Fact]
    public void The_model_level_list_persists_empty_on_a_clean_form()
    {
        Services.AddSingleton<FluentValidation.IValidator<EngineOrder>, EngineOrderValidator>();
        var order = new EngineOrder { Description = "ok", Customer = new EngineCustomer() };
        var form = RenderWithModelMessage(order);

        var list = form.Find("ul.formidable-message-list");
        Assert.Empty(list.Children);
        Assert.Null(list.GetAttribute("role"));
        Assert.Equal(
            FormidableFieldId.MessagesFor(new FieldIdentifier(order, string.Empty)),
            list.GetAttribute("id"));
    }

    [Fact]
    public void A_blocked_submit_whose_errors_are_all_suppressed_shows_the_gate_through_the_component()
    {
        Services.AddSingleton<FluentValidation.IValidator<EngineOrder>, EngineOrderValidator>();
        // Description empty and Customer null both fail the submit profile, and nothing on the
        // page renders either field, so every client error is suppressed — the exact case the
        // defensive gate exists for, on a form with no summary to carry its explanation.
        var order = new EngineOrder();
        var form = RenderWithModelMessage(order);

        // Captured before the submit and polled directly afterwards: the assertions below can
        // only pass if the gate's item entered THIS node — a list recreated around the item
        // would remove the captured node, and bUnit would throw on the stale reference.
        var list = form.Find("ul.formidable-message-list");
        Assert.Empty(list.Children);

        form.InvokeAsync(() => form.Instance.SubmitAsync());

        form.WaitForAssertion(() =>
        {
            var item = Assert.Single(list.Children);
            Assert.Equal("formidable-message formidable-message--error", item.GetAttribute("class"));
            Assert.Contains("information that is not currently displayed is invalid", item.TextContent);
        });
    }

    [Fact]
    public void The_fault_issue_renders_last_after_the_gate()
    {
        var validator = new FaultAndFieldErrorValidator();
        Services.AddSingleton<FluentValidation.IValidator<EngineOrder>>(validator);
        // The validator's submit ruleset fails Description, which nothing renders, so the
        // blocked submit arms the gate; the draft rule then throws on a live pass, filing the
        // form-level fault issue beside it.
        var order = new EngineOrder();
        var form = RenderWithModelMessage(order);

        form.InvokeAsync(() => form.Instance.SubmitAsync());
        form.WaitForAssertion(() => Assert.Single(form.FindAll("li.formidable-message--error")));

        validator.Throw = true;
        form.InvokeAsync(() => form.Instance.Engine!.EditContext.NotifyFieldChanged(
            new FieldIdentifier(order, nameof(EngineOrder.Description))));

        // GetIssues documents the fault last — it is about the pass, not the form's own verdict,
        // so it trails the gate's explanation rather than displacing it.
        form.WaitForAssertion(() =>
        {
            var items = form.FindAll("li.formidable-message--error");
            Assert.Equal(2, items.Count);
            Assert.Contains("information that is not currently displayed is invalid", items[0].TextContent);
            Assert.Contains("could not run to completion", items[1].TextContent);
        });
    }

    [Fact]
    public void InlineMessageRole_lands_on_the_persistent_model_level_list()
    {
        Services.AddSingleton<FluentValidation.IValidator<EngineOrder>, EngineOrderValidator>();
        var order = new EngineOrder { Description = "ok", Customer = new EngineCustomer() };
        var form = RenderWithModelMessage(order, new FormidableOptions { InlineMessageRole = "status" });

        // The role sits on the persistent element while it is still empty — the live-region
        // shape the component exists to give the gate on a summary-less form.
        var list = form.Find("ul.formidable-message-list");
        Assert.Empty(list.Children);
        Assert.Equal("status", list.GetAttribute("role"));
    }

    [Fact]
    public void The_component_renders_the_same_list_in_attach_mode()
    {
        Services.AddSingleton<FluentValidation.IValidator<EngineOrder>, EngineOrderValidator>();
        var order = new EngineOrder();
        var cut = Render(builder =>
        {
            builder.OpenComponent<Microsoft.AspNetCore.Components.Forms.EditForm>(0);
            builder.AddComponentParameter(1, "Model", order);
            builder.AddComponentParameter(2, "ChildContent", (RenderFragment<EditContext>)(_ => inner =>
            {
                inner.OpenComponent<FormidableValidator<EngineOrder>>(0);
                inner.AddComponentParameter(1, "ChildContent", (RenderFragment<FormidableFormContext>)(_ => content =>
                {
                    content.OpenComponent<FormidableModelMessage>(0);
                    content.CloseComponent();
                }));
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        });
        var validator = cut.FindComponent<FormidableValidator<EngineOrder>>();

        // The component depends only on the cascaded context, and FormidableValidator cascades
        // the same shape FormidableForm does — so the persistent list and the gate's explanation
        // behave identically inside a consumer's own EditForm.
        var list = cut.Find("ul.formidable-message-list");
        Assert.Empty(list.Children);

        cut.InvokeAsync(() => validator.Instance.ValidateForSubmitAsync());

        cut.WaitForAssertion(() => Assert.Contains(
            "information that is not currently displayed is invalid",
            cut.Find("li.formidable-message--error").TextContent));
    }

    [Fact]
    public void The_splat_follows_the_shared_message_list_policy()
    {
        Services.AddSingleton<FluentValidation.IValidator<EngineOrder>, EngineOrderValidator>();
        var order = new EngineOrder { Description = "ok", Customer = new EngineCustomer() };
        var form = RenderWithModelMessage(order, splat: new Dictionary<string, object>
        {
            ["class"] = "custom-model",
            ["id"] = "my-own-id",
            ["data-hook"] = "mine",
        });

        var list = form.Find("ul.formidable-message-list");
        Assert.Equal("custom-model formidable-message-list", list.GetAttribute("class"));
        Assert.Equal(
            FormidableFieldId.MessagesFor(new FieldIdentifier(order, string.Empty)),
            list.GetAttribute("id"));
        Assert.Equal("mine", list.GetAttribute("data-hook"));
    }
}
