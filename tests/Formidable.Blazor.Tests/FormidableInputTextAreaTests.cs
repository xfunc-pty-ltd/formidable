using Bunit;
using Formidable.Blazor.Tests.Fixtures;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;

namespace Formidable.Blazor.Tests;

/// <summary>
/// Pins <see cref="FormidableInputTextArea"/> against the same behaviours
/// <see cref="FormidableInputBaseTests"/> pins for <see cref="FormidableInputText"/> — the two
/// differ only in element tag, so this file mirrors that one's shape.
/// </summary>
public class FormidableInputTextAreaTests : BunitContext
{
    public FormidableInputTextAreaTests()
    {
        Services.AddFormidable();
        Services.AddSingleton<FluentValidation.IValidator<EngineOrder>, EngineOrderValidator>();
    }

    private IRenderedComponent<FormidableForm<EngineOrder>> RenderTextArea(
        EngineOrder order, InputUpdateMode updateOn = InputUpdateMode.OnChange)
    {
        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<EngineOrder>>(0);
            builder.AddComponentParameter(1, "Model", order);
            builder.AddComponentParameter(2, "ChildContent", (RenderFragment)(inner =>
            {
                inner.OpenComponent<FormidableInputTextArea>(0);
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

    private IRenderedComponent<FormidableForm<EngineOrder>> RenderTextAreaWithAttributes(
        EngineOrder order, params (string Name, object Value)[] attributes)
    {
        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<EngineOrder>>(0);
            builder.AddComponentParameter(1, "Model", order);
            builder.AddComponentParameter(2, "ChildContent", (RenderFragment)(inner =>
            {
                inner.OpenComponent<FormidableInputTextArea>(0);
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
        var form = RenderTextArea(order);

        form.Find("textarea").Change(new string('x', 11));

        Assert.Equal(new string('x', 11), order.Description);
        form.WaitForAssertion(() => Assert.Contains("formidable-invalid", form.Find("textarea").GetAttribute("class")));
    }

    [Fact]
    public void Input_mode_binds_oninput()
    {
        var order = new EngineOrder();
        var form = RenderTextArea(order, InputUpdateMode.OnInput);

        form.Find("textarea").Input("hello");

        Assert.Equal("hello", order.Description);
    }

    [Fact]
    public void Aria_attributes_reflect_error_state()
    {
        var order = new EngineOrder { Description = new string('x', 11) };
        var form = RenderTextArea(order);

        form.Find("textarea").Change(order.Description); // same value; still triggers validation

        form.WaitForAssertion(() =>
        {
            var textarea = form.Find("textarea");
            Assert.Equal("true", textarea.GetAttribute("aria-invalid"));
            Assert.Equal($"{textarea.GetAttribute("id")}-messages", textarea.GetAttribute("aria-describedby"));
        });
    }

    [Fact]
    public void Element_id_matches_field_id_convention()
    {
        var order = new EngineOrder();
        var form = RenderTextArea(order);

        Assert.Equal(
            FormidableFieldId.For(new FieldIdentifier(order, nameof(EngineOrder.Description))),
            form.Find("textarea").GetAttribute("id"));
    }

    [Fact]
    public void Splatted_class_merges_with_the_computed_state_class()
    {
        var order = new EngineOrder();
        var form = RenderTextAreaWithAttributes(order, ("class", "form-control"));

        form.Find("textarea").Change(new string('x', 11));

        form.WaitForAssertion(() =>
        {
            var css = form.Find("textarea").GetAttribute("class");
            Assert.Contains("form-control", css);
            Assert.Contains("formidable-invalid", css);
        });
    }

    [Fact]
    public void Splatted_id_does_not_override_the_deterministic_field_id()
    {
        var order = new EngineOrder();
        var form = RenderTextAreaWithAttributes(order, ("id", "consumer-supplied-id"));

        Assert.Equal(
            FormidableFieldId.For(new FieldIdentifier(order, nameof(EngineOrder.Description))),
            form.Find("textarea").GetAttribute("id"));
    }

    [Fact]
    public void Unrelated_splatted_attributes_pass_through()
    {
        var order = new EngineOrder();
        var form = RenderTextAreaWithAttributes(order, ("placeholder", "Notes"));

        Assert.Equal("Notes", form.Find("textarea").GetAttribute("placeholder"));
    }

    [Fact]
    public async Task Pending_class_appears_during_the_pass_and_clears_after_it()
    {
        var order = new EngineOrder();
        var validator = new GatedValidator();

        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<EngineOrder>>(0);
            builder.AddComponentParameter(1, "Model", order);
            builder.AddComponentParameter(2, "Validator", new FluentValidationModelValidator<EngineOrder>(validator));
            builder.AddComponentParameter(3, "Options", new FormidableOptions { LiveProfile = ValidationProfile.Submit });
            builder.AddComponentParameter(4, "ChildContent", (RenderFragment)(inner =>
            {
                inner.OpenComponent<FormidableInputTextArea>(0);
                inner.AddComponentParameter(1, "For", (System.Linq.Expressions.Expression<Func<string?>>)(() => order.Description));
                inner.AddComponentParameter(2, "Value", order.Description);
                inner.AddComponentParameter(3, "ValueChanged",
                    EventCallback.Factory.Create<string?>(this, v => order.Description = v ?? string.Empty));
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        });
        var form = cut.FindComponent<FormidableForm<EngineOrder>>();

        form.Find("textarea").Change("hi"); // starts the live pass; GatedValidator's async rule blocks on Gate

        form.WaitForAssertion(() => Assert.Contains("formidable-pending", form.Find("textarea").GetAttribute("class")));

        await cut.InvokeAsync(() => validator.Gate.SetResult());

        form.WaitForAssertion(() => Assert.DoesNotContain("formidable-pending", form.Find("textarea").GetAttribute("class")));
    }
}
