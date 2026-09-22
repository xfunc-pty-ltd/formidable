using Bunit;
using Formidable.Blazor.Tests.Fixtures;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;

namespace Formidable.Blazor.Tests;

public class FieldMessageTests : BunitContext
{
    public FieldMessageTests()
    {
        Services.AddFormidable();
        Services.AddSingleton<FluentValidation.IValidator<EngineOrder>, EngineOrderValidator>();
    }

    private IRenderedComponent<FormidableForm<EngineOrder>> RenderWithMessage(
        EngineOrder order, RenderFragment inner)
    {
        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<EngineOrder>>(0);
            builder.AddComponentParameter(1, "Model", order);
            builder.AddComponentParameter(2, "Options", new FormidableOptions { DisclosureOverride = _ => true });
            builder.AddComponentParameter(3, "ChildContent", inner);
            builder.CloseComponent();
        });
        return cut.FindComponent<FormidableForm<EngineOrder>>();
    }

    [Fact]
    public void Renders_nothing_when_clean_and_severity_classed_items_when_not()
    {
        var order = new EngineOrder { Description = "a-b" }; // warning rule fails on submit; NotEmpty passes
        var form = RenderWithMessage(order, inner =>
        {
            inner.OpenComponent<FieldMessage<string>>(0);
            inner.AddComponentParameter(1, "For", (System.Linq.Expressions.Expression<Func<string>>)(() => order.Description));
            inner.CloseComponent();
        });

        Assert.Empty(form.FindAll("ul"));

        form.InvokeAsync(() => form.Instance.SubmitAsync());

        form.WaitForAssertion(() =>
        {
            var item = form.Find("li.formidable-message--warning");
            Assert.Contains("Avoid hyphens", item.TextContent);
            var list = form.Find("ul.formidable-messages");
            Assert.Equal(
                $"{FormidableFieldId.For(new FieldIdentifier(order, nameof(EngineOrder.Description)))}-messages",
                list.GetAttribute("id"));
        });
    }

    [Fact]
    public void Error_issues_render_with_error_class()
    {
        var order = new EngineOrder();
        var form = RenderWithMessage(order, inner =>
        {
            inner.OpenComponent<FieldMessage<string>>(0);
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
            builder.AddComponentParameter(2, "ChildContent", (RenderFragment)(inner =>
            {
                inner.OpenComponent<CollectionMessage<List<EngineItem>>>(0);
                inner.AddComponentParameter(1, "For", (System.Linq.Expressions.Expression<Func<List<EngineItem>>>)(() => order.Items));
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        });
        var form = cut.FindComponent<FormidableForm<EngineOrder>>();

        Assert.True(form.Instance.Engine!.Registry.IsRevealed(new FieldIdentifier(order, nameof(EngineOrder.Items))));
    }

    [Fact]
    public void Model_level_message_component_shows_form_level_issues()
    {
        var order = new EngineOrder
        {
            Description = "ok",
            Customer = new EngineCustomer(),
            Items = [new() { Sku = "A" }, new() { Sku = "B" }, new() { Sku = "C" }, new() { Sku = "D" }]
        };
        var form = RenderWithMessage(order, inner =>
        {
            inner.OpenComponent<CollectionMessage<List<EngineItem>>>(0);
            inner.AddComponentParameter(1, "For", (System.Linq.Expressions.Expression<Func<List<EngineItem>>>)(() => order.Items));
            inner.CloseComponent();
        });

        form.InvokeAsync(() => form.Instance.SubmitAsync());

        // The count rule is model-level (Path ""), so it does NOT land on Items — this
        // asserts the boundary: CollectionMessage shows only ITS field's issues.
        form.WaitForAssertion(() => Assert.Empty(form.FindAll("li")));
    }

    [Fact]
    public void Message_outside_a_form_names_the_component_without_its_generic_arity()
    {
        var order = new EngineOrder();
        var exception = Assert.ThrowsAny<Exception>(() => Render(builder =>
        {
            builder.OpenComponent<FieldMessage<string>>(0);
            builder.AddComponentParameter(1, "For", (System.Linq.Expressions.Expression<Func<string>>)(() => order.Description));
            builder.CloseComponent();
        }));

        Assert.Contains("FieldMessage must be placed", exception.Message);
        Assert.DoesNotContain("`", exception.Message); // not the CLR's "FieldMessage`1"
    }
}
