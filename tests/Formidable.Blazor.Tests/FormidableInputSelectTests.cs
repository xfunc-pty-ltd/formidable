using Bunit;
using FluentValidation;
using Formidable.Blazor.Tests.Fixtures;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;

namespace Formidable.Blazor.Tests;

/// <summary>
/// Pins <see cref="FormidableInputSelect{TValue}"/>'s value semantics against native
/// <c>InputSelect&lt;TValue&gt;</c>: string and enum conversion parity, the unsupported-type
/// boundary, and the base's five extras (registration/id, css class, aria, pending,
/// <c>SetCurrentValueAsync</c>) flowing through unchanged. <see cref="EngineOrder"/> covers the
/// string-typed cases; the enum and unsupported-type fixtures below are local, matching the
/// derivation tests' precedent of not touching the shared validator's issue set.
/// </summary>
public class FormidableInputSelectTests : BunitContext
{
    public FormidableInputSelectTests()
    {
        Services.AddFormidable();
        Services.AddSingleton<IValidator<EngineOrder>, EngineOrderValidator>();
        Services.AddSingleton<IValidator<Ticket>, TicketValidator>();
        Services.AddSingleton<IValidator<Widget>, WidgetValidator>();
    }

    private static readonly RenderFragment ColourOptions = builder =>
    {
        builder.OpenElement(0, "option");
        builder.AddAttribute(1, "value", "");
        builder.AddContent(2, "Choose…");
        builder.CloseElement();
        builder.OpenElement(3, "option");
        builder.AddContent(4, "Red");
        builder.CloseElement();
        builder.OpenElement(5, "option");
        builder.AddContent(6, new string('x', 11));
        builder.CloseElement();
    };

    private static readonly RenderFragment PriorityOptions = builder =>
    {
        builder.OpenElement(0, "option");
        builder.AddAttribute(1, "value", "");
        builder.AddContent(2, "Choose…");
        builder.CloseElement();
        builder.OpenElement(3, "option");
        builder.AddContent(4, "Low");
        builder.CloseElement();
        builder.OpenElement(5, "option");
        builder.AddContent(6, "Medium");
        builder.CloseElement();
        builder.OpenElement(7, "option");
        builder.AddContent(8, "High");
        builder.CloseElement();
    };

    private static readonly RenderFragment GadgetOptions = builder =>
    {
        builder.OpenElement(0, "option");
        builder.AddContent(1, "x");
        builder.CloseElement();
    };

    private IRenderedComponent<FormidableForm<EngineOrder>> RenderColourSelect(
        EngineOrder order, InputUpdateMode updateOn = InputUpdateMode.OnChange, params (string Name, object Value)[] attributes)
    {
        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<EngineOrder>>(0);
            builder.AddComponentParameter(1, "Model", order);
            builder.AddComponentParameter(2, "ChildContent", (RenderFragment)(inner =>
            {
                inner.OpenComponent<FormidableInputSelect<string?>>(0);
                inner.AddComponentParameter(1, "For", (System.Linq.Expressions.Expression<Func<string?>>)(() => order.Description));
                inner.AddComponentParameter(2, "Value", order.Description);
                inner.AddComponentParameter(3, "ValueChanged",
                    EventCallback.Factory.Create<string?>(this, v => order.Description = v ?? string.Empty));
                inner.AddComponentParameter(4, "UpdateOn", updateOn);
                inner.AddComponentParameter(5, "ChildContent", ColourOptions);
                var sequence = 6;
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
        var form = RenderColourSelect(order);

        form.Find("select").Change(new string('x', 11));

        Assert.Equal(new string('x', 11), order.Description);
        form.WaitForAssertion(() => Assert.Contains("formidable-invalid", form.Find("select").GetAttribute("class")));
    }

    [Fact]
    public void Element_id_matches_field_id_convention()
    {
        var order = new EngineOrder();
        var form = RenderColourSelect(order);

        Assert.Equal(
            FormidableFieldId.For(new FieldIdentifier(order, nameof(EngineOrder.Description))),
            form.Find("select").GetAttribute("id"));
    }

    [Fact]
    public void Aria_attributes_reflect_error_state()
    {
        var order = new EngineOrder();
        var form = RenderColourSelect(order);

        form.Find("select").Change(new string('x', 11));

        form.WaitForAssertion(() =>
        {
            var select = form.Find("select");
            Assert.Equal("true", select.GetAttribute("aria-invalid"));
            Assert.Equal($"{select.GetAttribute("id")}-messages", select.GetAttribute("aria-describedby"));
        });
    }

    [Fact]
    public void Splatted_class_merges_with_the_computed_state_class()
    {
        var order = new EngineOrder();
        var form = RenderColourSelect(order, attributes: ("class", "form-select"));

        form.Find("select").Change(new string('x', 11));

        form.WaitForAssertion(() =>
        {
            var css = form.Find("select").GetAttribute("class");
            Assert.Contains("form-select", css);
            Assert.Contains("formidable-invalid", css);
        });
    }

    [Fact]
    public void Value_formats_as_the_selected_option()
    {
        var order = new EngineOrder { Description = "Red" };
        var form = RenderColourSelect(order);

        Assert.Equal("Red", form.Find("select").GetAttribute("value"));
    }

    [Fact]
    public void ChildContent_renders_the_option_elements()
    {
        var order = new EngineOrder();
        var form = RenderColourSelect(order);

        var options = form.FindAll("option");
        Assert.Equal(3, options.Count);
        Assert.Equal("Choose…", options[0].TextContent);
        Assert.Equal("Red", options[1].TextContent);
    }

    [Fact]
    public void Input_mode_coerces_to_change_and_still_triggers_live_validation()
    {
        var order = new EngineOrder();
        var form = RenderColourSelect(order, InputUpdateMode.OnInput);

        // A <select> has no meaningful "input" event distinct from "change"; OnInput coerces to
        // OnChange, so firing the DOM "change" event both commits the value and starts the live
        // pass immediately, exactly as the default mode does.
        form.Find("select").Change(new string('x', 11));

        Assert.Equal(new string('x', 11), order.Description);
        form.WaitForAssertion(() => Assert.Contains("formidable-invalid", form.Find("select").GetAttribute("class")));
    }

    // Pins the split OnBlur adds: the value commits on change alone, with no engine notification
    // riding along — so a value that fails the live rule (11 chars, past EngineOrderValidator's
    // max) commits to the model but starts no live pass, and the class stays exactly what an
    // untouched field renders (empty; see FormidableCss.Compute).
    [Fact]
    public void Blur_mode_commits_the_value_on_change_without_starting_a_live_pass()
    {
        var order = new EngineOrder();
        var form = RenderColourSelect(order, InputUpdateMode.OnBlur);

        form.Find("select").Change(new string('x', 11));

        Assert.Equal(new string('x', 11), order.Description);
        Assert.Equal(string.Empty, form.Find("select").GetAttribute("class"));
    }

    // The other half: once the committed value above is followed by blur, the engine is notified
    // and the live pass that was withheld on change now runs.
    [Fact]
    public void Blur_mode_notifies_the_engine_on_blur()
    {
        var order = new EngineOrder();
        var form = RenderColourSelect(order, InputUpdateMode.OnBlur);

        form.Find("select").Change(new string('x', 11));
        form.Find("select").Blur();

        form.WaitForAssertion(() => Assert.Contains("formidable-invalid", form.Find("select").GetAttribute("class")));
    }

    // Pins the commit gate through the string-projected binding: a blur with no committed
    // change delivers nothing — no OnFieldChanged, no touch, no state class. An unconditional
    // blur notification breaks all three.
    [Fact]
    public void A_select_blur_with_no_committed_change_notifies_nothing()
    {
        var order = new EngineOrder();
        var form = RenderColourSelect(order, InputUpdateMode.OnBlur);
        var notifications = 0;
        form.Instance.Engine!.EditContext.OnFieldChanged += (_, _) => notifications++;

        form.Find("select").Blur();

        Assert.Equal(0, notifications);
        Assert.False(form.Instance.Engine!.GetFieldState(
            new FieldIdentifier(order, nameof(EngineOrder.Description))).IsTouched);
        Assert.Equal(string.Empty, form.Find("select").GetAttribute("class"));
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
            builder.AddComponentParameter(3, "Options", new FormidableOptions());
            builder.AddComponentParameter(4, "ChildContent", (RenderFragment)(inner =>
            {
                inner.OpenComponent<FormidableInputSelect<string?>>(0);
                inner.AddComponentParameter(1, "For", (System.Linq.Expressions.Expression<Func<string?>>)(() => order.Description));
                inner.AddComponentParameter(2, "Value", order.Description);
                inner.AddComponentParameter(3, "ValueChanged",
                    EventCallback.Factory.Create<string?>(this, v => order.Description = v ?? string.Empty));
                inner.AddComponentParameter(4, "ChildContent", ColourOptions);
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        });
        var form = cut.FindComponent<FormidableForm<EngineOrder>>();

        form.Find("select").Change("Red"); // starts the live pass; GatedValidator's async rule blocks on Gate

        form.WaitForAssertion(() => Assert.Contains("formidable-pending", form.Find("select").GetAttribute("class")));

        await cut.InvokeAsync(() => validator.Gate.SetResult());

        form.WaitForAssertion(() => Assert.DoesNotContain("formidable-pending", form.Find("select").GetAttribute("class")));
    }

    [Fact]
    public void Enum_conversion_parity_with_native_InputSelect()
    {
        var ticket = new Ticket();
        var form = RenderPrioritySelect(ticket);

        form.Find("select").Change("Medium");

        Assert.Equal(Priority.Medium, ticket.Priority);
    }

    [Fact]
    public void Blank_option_parses_to_null_for_a_nullable_enum()
    {
        var ticket = new Ticket { Priority = Priority.High };
        var form = RenderPrioritySelect(ticket);

        form.Find("select").Change("");

        Assert.Null(ticket.Priority);
    }

    [Fact]
    public void Unparseable_value_leaves_the_model_unchanged()
    {
        var ticket = new Ticket { Priority = Priority.Low };
        var form = RenderPrioritySelect(ticket);

        form.Find("select").Change("NotAPriority");

        Assert.Equal(Priority.Low, ticket.Priority);
    }

    [Fact]
    public void Unsupported_value_type_throws_like_native_InputSelect()
    {
        var widget = new Widget();
        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<Widget>>(0);
            builder.AddComponentParameter(1, "Model", widget);
            builder.AddComponentParameter(2, "ChildContent", (RenderFragment)(inner =>
            {
                inner.OpenComponent<FormidableInputSelect<UnsupportedGadget?>>(0);
                inner.AddComponentParameter(1, "For", (System.Linq.Expressions.Expression<Func<UnsupportedGadget?>>)(() => widget.Gadget));
                inner.AddComponentParameter(2, "Value", widget.Gadget);
                inner.AddComponentParameter(3, "ValueChanged",
                    EventCallback.Factory.Create<UnsupportedGadget?>(this, v => widget.Gadget = v));
                inner.AddComponentParameter(4, "ChildContent", GadgetOptions);
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        });

        var ex = Assert.Throws<InvalidOperationException>(() =>
        {
            cut.Find("select").Change("x");
        });
        Assert.Contains("does not support the type", ex.Message);
    }

    private IRenderedComponent<FormidableForm<Ticket>> RenderPrioritySelect(Ticket ticket)
    {
        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<Ticket>>(0);
            builder.AddComponentParameter(1, "Model", ticket);
            builder.AddComponentParameter(2, "ChildContent", (RenderFragment)(inner =>
            {
                inner.OpenComponent<FormidableInputSelect<Priority?>>(0);
                inner.AddComponentParameter(1, "For", (System.Linq.Expressions.Expression<Func<Priority?>>)(() => ticket.Priority));
                inner.AddComponentParameter(2, "Value", ticket.Priority);
                inner.AddComponentParameter(3, "ValueChanged",
                    EventCallback.Factory.Create<Priority?>(this, v => ticket.Priority = v));
                inner.AddComponentParameter(4, "ChildContent", PriorityOptions);
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        });
        return cut.FindComponent<FormidableForm<Ticket>>();
    }
}

public enum Priority
{
    Low,
    Medium,
    High,
}

public sealed class Ticket
{
    public Priority? Priority { get; set; }
}

public sealed class TicketValidator : DraftSubmitValidator<Ticket>
{
    protected override void ConfigureDraftRules()
    {
    }

    protected override void ConfigureSubmitRules() =>
        RuleFor(x => x.Priority).NotNull().WithMessage("Priority is required");
}

/// <summary>A type <see cref="BindConverter"/> cannot convert from a string — no <see cref="System.ComponentModel.TypeConverter"/>, not an enum.</summary>
public sealed class UnsupportedGadget
{
}

public sealed class Widget
{
    public UnsupportedGadget? Gadget { get; set; }
}

public sealed class WidgetValidator : DraftSubmitValidator<Widget>
{
    protected override void ConfigureDraftRules()
    {
    }

    protected override void ConfigureSubmitRules()
    {
    }
}
