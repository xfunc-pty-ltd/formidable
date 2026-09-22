using Bunit;
using Formidable.Blazor.Tests.Fixtures;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;

namespace Formidable.Blazor.Tests;

public class FormidableFieldMessageTests : BunitContext
{
    public FormidableFieldMessageTests()
    {
        Services.AddFormidable();
        Services.AddSingleton<FluentValidation.IValidator<EngineOrder>, EngineOrderValidator>();
    }

    private IRenderedComponent<FormidableForm<EngineOrder>> RenderWithMessage(
        EngineOrder order, RenderFragment inner, FormidableOptions? options = null)
    {
        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<EngineOrder>>(0);
            builder.AddComponentParameter(1, "Model", order);
            builder.AddComponentParameter(2, "Options", options ?? new FormidableOptions { DisclosureOverride = _ => true });
            builder.AddComponentParameter(3, "ChildContent", (RenderFragment<FormidableFormContext>)(_ => inner));
            builder.CloseComponent();
        });
        return cut.FindComponent<FormidableForm<EngineOrder>>();
    }

    [Fact]
    public void The_messages_container_persists_when_empty()
    {
        var order = new EngineOrder { Description = "a-b" }; // warning rule fails on submit; NotEmpty passes
        var form = RenderWithMessage(order, inner =>
        {
            inner.OpenComponent<FormidableFieldMessage<string>>(0);
            inner.AddComponentParameter(1, "For", (System.Linq.Expressions.Expression<Func<string>>)(() => order.Description));
            inner.CloseComponent();
        });

        var list = form.Find("ul.formidable-message-list");
        Assert.Empty(list.Children);
        Assert.Null(list.GetAttribute("aria-live"));

        var liveOrder = new EngineOrder { Description = "a-b" };
        var withLive = RenderWithMessage(
            liveOrder,
            inner =>
            {
                inner.OpenComponent<FormidableFieldMessage<string>>(0);
                inner.AddComponentParameter(1, "For", (System.Linq.Expressions.Expression<Func<string>>)(() => liveOrder.Description));
                inner.CloseComponent();
            },
            new FormidableOptions { DisclosureOverride = _ => true, InlineMessageLive = "polite" });

        var liveList = withLive.Find("ul.formidable-message-list");
        Assert.Empty(liveList.Children);
        Assert.Equal("polite", liveList.GetAttribute("aria-live"));
    }

    [Fact]
    public void Messages_enter_and_leave_the_persistent_container()
    {
        var order = new EngineOrder(); // Description empty -> NotEmpty error on submit
        var form = RenderWithMessage(order, inner =>
        {
            inner.OpenComponent<FormidableFieldMessage<string>>(0);
            inner.AddComponentParameter(1, "For", (System.Linq.Expressions.Expression<Func<string>>)(() => order.Description));
            inner.CloseComponent();
        });

        form.InvokeAsync(() => form.Instance.SubmitAsync());
        form.WaitForAssertion(() => Assert.NotEmpty(form.FindAll("li.formidable-message--error")));

        // Captured once the container already holds an item, and polled directly (never
        // re-queried via Find) for the rest of the test — reaching this point does not depend on
        // the persistent-container contract at all, since a field with issues renders a list
        // either way. What the assertions below can only pass under is the SAME live node staying
        // current through a clear and a re-fail: a container recreated on clear would remove this
        // exact node from the tree, and bUnit tracks that — the clear-side wait would throw
        // ElementRemovedFromDomException on this reference rather than ever observe it empty.
        var container = form.Find("ul.formidable-message-list");
        Assert.NotEmpty(container.Children);
        Assert.NotNull(container.ParentElement);

        order.Description = "ok";
        form.InvokeAsync(() => form.Instance.SubmitAsync());

        form.WaitForAssertion(() => Assert.Empty(container.Children));
        Assert.NotNull(container.ParentElement);

        order.Description = "";
        form.InvokeAsync(() => form.Instance.SubmitAsync());

        form.WaitForAssertion(() => Assert.NotEmpty(container.Children));
        Assert.NotNull(container.ParentElement);
    }

    [Fact]
    public void Renders_severity_classed_items_on_submit()
    {
        var order = new EngineOrder { Description = "a-b" }; // warning rule fails on submit; NotEmpty passes
        var form = RenderWithMessage(order, inner =>
        {
            inner.OpenComponent<FormidableFieldMessage<string>>(0);
            inner.AddComponentParameter(1, "For", (System.Linq.Expressions.Expression<Func<string>>)(() => order.Description));
            inner.CloseComponent();
        });

        form.InvokeAsync(() => form.Instance.SubmitAsync());

        form.WaitForAssertion(() =>
        {
            var item = form.Find("li.formidable-message--warning");
            Assert.Contains("Avoid hyphens", item.TextContent);
            var list = form.Find("ul.formidable-message-list");
            Assert.Equal(
                $"{FormidableFieldId.For(new FieldIdentifier(order, nameof(EngineOrder.Description)))}-messages",
                list.GetAttribute("id"));
        });
    }

    [Fact]
    public void Default_options_render_no_aria_live_attribute_on_the_list()
    {
        var order = new EngineOrder { Description = "a-b" }; // warning rule fails on submit; NotEmpty passes
        var form = RenderWithMessage(order, inner =>
        {
            inner.OpenComponent<FormidableFieldMessage<string>>(0);
            inner.AddComponentParameter(1, "For", (System.Linq.Expressions.Expression<Func<string>>)(() => order.Description));
            inner.CloseComponent();
        });

        form.InvokeAsync(() => form.Instance.SubmitAsync());

        form.WaitForAssertion(() => Assert.Null(form.Find("ul.formidable-message-list").GetAttribute("aria-live")));
    }

    [Fact]
    public void InlineMessageLive_option_adds_aria_live_attribute_to_the_list()
    {
        var order = new EngineOrder { Description = "a-b" }; // warning rule fails on submit; NotEmpty passes
        var form = RenderWithMessage(
            order,
            inner =>
            {
                inner.OpenComponent<FormidableFieldMessage<string>>(0);
                inner.AddComponentParameter(1, "For", (System.Linq.Expressions.Expression<Func<string>>)(() => order.Description));
                inner.CloseComponent();
            },
            new FormidableOptions { DisclosureOverride = _ => true, InlineMessageLive = "polite" });

        form.InvokeAsync(() => form.Instance.SubmitAsync());

        form.WaitForAssertion(() => Assert.Equal("polite", form.Find("ul.formidable-message-list").GetAttribute("aria-live")));
    }

    [Fact]
    public void Error_issues_render_with_error_class()
    {
        var order = new EngineOrder();
        var form = RenderWithMessage(order, inner =>
        {
            inner.OpenComponent<FormidableFieldMessage<string>>(0);
            inner.AddComponentParameter(1, "For", (System.Linq.Expressions.Expression<Func<string>>)(() => order.Description));
            inner.CloseComponent();
        });

        form.InvokeAsync(() => form.Instance.SubmitAsync());

        form.WaitForAssertion(() => Assert.NotEmpty(form.FindAll("li.formidable-message--error")));
    }

    [Fact]
    public void Collection_message_registers_and_shows_collection_level_errors()
    {
        var order = new EngineOrder
        {
            Description = "ok",
            Customer = new EngineCustomer(),
            Items = [new() { Sku = "A" }, new() { Sku = "B" }, new() { Sku = "C" }, new() { Sku = "D" }]
        };
        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<EngineOrder>>(0);
            builder.AddComponentParameter(1, "Model", order);
            builder.AddComponentParameter(2, "ChildContent", (RenderFragment<FormidableFormContext>)(_ => inner =>
            {
                inner.OpenComponent<FormidableCollectionMessage<List<EngineItem>>>(0);
                inner.AddComponentParameter(1, "For", (System.Linq.Expressions.Expression<Func<List<EngineItem>>>)(() => order.Items));
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        });
        var form = cut.FindComponent<FormidableForm<EngineOrder>>();

        Assert.True(form.Instance.Engine!.Registry.IsRegistered(new FieldIdentifier(order, nameof(EngineOrder.Items))));
    }

    [Fact]
    public void Collection_message_shows_only_its_own_fields_issues()
    {
        var order = new EngineOrder
        {
            Description = "ok",
            Customer = new EngineCustomer(),
            Items = [new() { Sku = "A" }, new() { Sku = "B" }, new() { Sku = "C" }, new() { Sku = "D" }]
        };
        var form = RenderWithMessage(order, inner =>
        {
            inner.OpenComponent<FormidableCollectionMessage<List<EngineItem>>>(0);
            inner.AddComponentParameter(1, "For", (System.Linq.Expressions.Expression<Func<List<EngineItem>>>)(() => order.Items));
            inner.CloseComponent();
        });

        form.InvokeAsync(() => form.Instance.SubmitAsync());

        // The count rule is model-level (Path ""), so it does NOT land on Items — this
        // asserts the boundary: FormidableCollectionMessage shows only ITS field's issues.
        // Reading the issue off the engine first is what keeps the empty list from standing for
        // a submit that produced nothing at all, which would satisfy it for the wrong reason.
        form.WaitForAssertion(() =>
        {
            Assert.Contains(
                form.Instance.Engine!.GetVisibleIssues(),
                v => v.Issue.Message == "No more than 3 items");
            Assert.Empty(form.FindAll("li"));
        });
    }

    [Fact]
    public void A_splatted_class_merges_with_the_list_class()
    {
        var order = new EngineOrder { Description = "a-b" };
        var form = RenderWithMessage(order, inner =>
        {
            inner.OpenComponent<FormidableFieldMessage<string>>(0);
            inner.AddComponentParameter(1, "For", (System.Linq.Expressions.Expression<Func<string>>)(() => order.Description));
            inner.AddComponentParameter(2, "class", "custom-list");
            inner.AddComponentParameter(3, "data-hook", "mine");
            inner.CloseComponent();
        });

        // Exact equality pins the whole policy: the splat reached the element (data-hook), the
        // consumer's class survived, the structural class was appended after it, and neither
        // replaced the other.
        var list = form.Find("ul.formidable-message-list");
        Assert.Equal("custom-list formidable-message-list", list.GetAttribute("class"));
        Assert.Equal("mine", list.GetAttribute("data-hook"));
    }

    [Fact]
    public void A_splatted_id_is_ignored_in_favour_of_the_messages_id()
    {
        var order = new EngineOrder { Description = "a-b" };
        var form = RenderWithMessage(order, inner =>
        {
            inner.OpenComponent<FormidableFieldMessage<string>>(0);
            inner.AddComponentParameter(1, "For", (System.Linq.Expressions.Expression<Func<string>>)(() => order.Description));
            inner.AddComponentParameter(2, "id", "my-own-id");
            inner.CloseComponent();
        });

        // The rendered id IS the aria-describedby contract, so the computed value must win the
        // duplicate-attribute race — a splatted id landing here would strand every input that
        // describes itself by this list.
        var list = form.Find("ul.formidable-message-list");
        Assert.Equal(
            FormidableFieldId.MessagesFor(new FieldIdentifier(order, nameof(EngineOrder.Description))),
            list.GetAttribute("id"));
    }

    [Fact]
    public void A_configured_InlineMessageLive_wins_a_splatted_aria_live()
    {
        var order = new EngineOrder { Description = "a-b" };
        var withOption = RenderWithMessage(
            order,
            inner =>
            {
                inner.OpenComponent<FormidableFieldMessage<string>>(0);
                inner.AddComponentParameter(1, "For", (System.Linq.Expressions.Expression<Func<string>>)(() => order.Description));
                inner.AddComponentParameter(2, "aria-live", "assertive");
                inner.CloseComponent();
            },
            new FormidableOptions { DisclosureOverride = _ => true, InlineMessageLive = "polite" });

        Assert.Equal("polite", withOption.Find("ul.formidable-message-list").GetAttribute("aria-live"));

        // With the option unset the kit computes no aria-live, so the splatted one stands — the
        // documented boundary of the computed-wins policy, not an accident.
        var bare = new EngineOrder { Description = "a-b" };
        var withoutOption = RenderWithMessage(bare, inner =>
        {
            inner.OpenComponent<FormidableFieldMessage<string>>(0);
            inner.AddComponentParameter(1, "For", (System.Linq.Expressions.Expression<Func<string>>)(() => bare.Description));
            inner.AddComponentParameter(2, "aria-live", "assertive");
            inner.CloseComponent();
        });

        Assert.Equal("assertive", withoutOption.Find("ul.formidable-message-list").GetAttribute("aria-live"));
    }

    [Fact]
    public void Message_outside_a_form_names_the_component_without_its_generic_arity()
    {
        var order = new EngineOrder();
        var exception = Assert.ThrowsAny<Exception>(() => Render(builder =>
        {
            builder.OpenComponent<FormidableFieldMessage<string>>(0);
            builder.AddComponentParameter(1, "For", (System.Linq.Expressions.Expression<Func<string>>)(() => order.Description));
            builder.CloseComponent();
        }));

        Assert.Contains("FormidableFieldMessage must be placed", exception.Message);
        Assert.DoesNotContain("`", exception.Message); // not the CLR's "FormidableFieldMessage`1"
    }
}
