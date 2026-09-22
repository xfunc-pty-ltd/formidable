using System.Globalization;
using Bunit;
using FluentValidation;
using Formidable.Blazor.Tests.Fixtures;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;

namespace Formidable.Blazor.Tests;

/// <summary>
/// Pins <see cref="FormidableInputNumber{TValue}"/>'s value semantics: the base's five extras
/// (registration/id, css class, aria, pending, <c>UpdateOn</c>) flowing through unchanged, the
/// silent-revert contract for unparseable/emptied input, and — the reason this component exists
/// rather than reusing <see cref="FormidableInputBase{TValue}.AddValueBinding(RenderTreeBuilder, int)"/>
/// directly — culture-invariant conversion under a non-invariant current culture.
/// </summary>
public class FormidableInputNumberTests : BunitContext
{
    public FormidableInputNumberTests()
    {
        Services.AddFormidable();
        Services.AddSingleton<IValidator<Booking>>(new BookingValidator());
        // The component injects the DOM value sync; the recording fake keeps these tests off
        // JS interop (FormidableInputDomSyncTests owns the sync assertions).
        Services.AddSingleton<IFormidableDomValueSync>(new RecordingDomValueSync());
    }

    private IRenderedComponent<FormidableForm<Booking>> RenderSeats(
        Booking booking, InputUpdateMode updateOn = InputUpdateMode.OnChange, params (string Name, object Value)[] attributes)
    {
        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<Booking>>(0);
            builder.AddComponentParameter(1, "Model", booking);
            builder.AddComponentParameter(2, "ChildContent", (RenderFragment<FormidableFormContext>)(_ => inner =>
            {
                inner.OpenComponent<FormidableInputNumber<int>>(0);
                inner.AddComponentParameter(1, "For", (System.Linq.Expressions.Expression<Func<int>>)(() => booking.Seats));
                inner.AddComponentParameter(2, "Value", booking.Seats);
                inner.AddComponentParameter(3, "ValueChanged",
                    EventCallback.Factory.Create<int>(this, v => booking.Seats = v));
                inner.AddComponentParameter(4, "UpdateOn", updateOn);
                var sequence = 5;
                foreach (var (name, value) in attributes)
                {
                    inner.AddComponentParameter(sequence++, name, value);
                }

                inner.CloseComponent();
            }));
            builder.CloseComponent();
        });
        return cut.FindComponent<FormidableForm<Booking>>();
    }

    private IRenderedComponent<FormidableForm<Booking>> RenderPrice(
        Booking booking, params (string Name, object Value)[] attributes)
    {
        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<Booking>>(0);
            builder.AddComponentParameter(1, "Model", booking);
            builder.AddComponentParameter(2, "ChildContent", (RenderFragment<FormidableFormContext>)(_ => inner =>
            {
                inner.OpenComponent<FormidableInputNumber<decimal?>>(0);
                inner.AddComponentParameter(1, "For", (System.Linq.Expressions.Expression<Func<decimal?>>)(() => booking.Price));
                inner.AddComponentParameter(2, "Value", booking.Price);
                inner.AddComponentParameter(3, "ValueChanged",
                    EventCallback.Factory.Create<decimal?>(this, v => booking.Price = v));
                var sequence = 4;
                foreach (var (name, value) in attributes)
                {
                    inner.AddComponentParameter(sequence++, name, value);
                }

                inner.CloseComponent();
            }));
            builder.CloseComponent();
        });
        return cut.FindComponent<FormidableForm<Booking>>();
    }

    [Fact]
    public void Renders_a_number_input_with_the_type_attribute_in_the_component_wins_position()
    {
        var booking = new Booking();
        var form = RenderSeats(booking, attributes: ("type", "text"));

        // The component's own "number" wins the duplicate-attribute race even against a
        // consumer's splatted "type" — the same policy the base documents for id/class.
        Assert.Equal("number", form.Find("input").GetAttribute("type"));
    }

    // Without step="any", the HTML default (step=1) makes a fractional decimal value a native
    // stepMismatch. FormidableForm's novalidate keeps a mismatch from blocking the submit there,
    // but the field would still match :invalid and the spinner would snap to whole numbers — and
    // in a form without novalidate (attach mode's consumer-owned EditForm, or a splat that
    // removed the form's default) the browser would still block a real submit and front its own
    // constraint tooltip instead of FluentValidation's message. step="any" retires the mismatch
    // at the source.
    [Fact]
    public void Decimal_field_renders_step_any()
    {
        var booking = new Booking();
        var form = RenderPrice(booking);

        Assert.Equal("any", form.Find("input").GetAttribute("step"));
    }

    [Fact]
    public void Splatted_step_overrides_the_default_step()
    {
        var booking = new Booking();
        var form = RenderPrice(booking, ("step", "0.01"));

        // Unlike "type", the default "any" renders before the splat: a consumer's own "step"
        // wins the duplicate-attribute race, matching native InputNumber's own handling.
        Assert.Equal("0.01", form.Find("input").GetAttribute("step"));
    }

    [Fact]
    public void Change_updates_model_and_triggers_live_validation()
    {
        var booking = new Booking();
        var form = RenderSeats(booking);

        form.Find("input").Change("20");

        Assert.Equal(20, booking.Seats);
        form.WaitForAssertion(() => Assert.Contains("formidable-invalid", form.Find("input").GetAttribute("class")));
    }

    [Fact]
    public void Element_id_matches_field_id_convention()
    {
        var booking = new Booking();
        var form = RenderSeats(booking);

        Assert.Equal(
            FormidableFieldId.For(new FieldIdentifier(booking, nameof(Booking.Seats))),
            form.Find("input").GetAttribute("id"));
    }

    [Fact]
    public void Aria_attributes_reflect_error_state()
    {
        var booking = new Booking();
        var form = RenderSeats(booking);

        form.Find("input").Change("20");

        form.WaitForAssertion(() =>
        {
            var input = form.Find("input");
            Assert.Equal("true", input.GetAttribute("aria-invalid"));
            Assert.Equal($"{input.GetAttribute("id")}-messages", input.GetAttribute("aria-describedby"));
        });
    }

    [Fact]
    public void Splatted_class_merges_with_the_computed_state_class()
    {
        var booking = new Booking();
        var form = RenderSeats(booking, attributes: ("class", "form-control"));

        form.Find("input").Change("20");

        form.WaitForAssertion(() =>
        {
            var css = form.Find("input").GetAttribute("class");
            Assert.Contains("form-control", css);
            Assert.Contains("formidable-invalid", css);
        });
    }

    [Fact]
    public void Input_mode_binds_oninput()
    {
        var booking = new Booking();
        var form = RenderSeats(booking, InputUpdateMode.OnInput);

        form.Find("input").Input("4");

        Assert.Equal(4, booking.Seats);
    }

    [Fact]
    public void Blur_mode_commits_the_value_on_change_without_starting_a_live_pass()
    {
        var booking = new Booking();
        var form = RenderSeats(booking, InputUpdateMode.OnBlur);

        form.Find("input").Change("20");

        Assert.Equal(20, booking.Seats);
        Assert.Equal(string.Empty, form.Find("input").GetAttribute("class"));
    }

    [Fact]
    public void Blur_mode_notifies_the_engine_on_blur()
    {
        var booking = new Booking();
        var form = RenderSeats(booking, InputUpdateMode.OnBlur);

        form.Find("input").Change("20");
        form.Find("input").Blur();

        form.WaitForAssertion(() => Assert.Contains("formidable-invalid", form.Find("input").GetAttribute("class")));
    }

    // Pins that a failed parse is not a commit: the change commits nothing (the silent-revert
    // contract), so the following blur syncs the box back to the model without delivering any
    // notification. Arming the blur-time notification on a failed parse breaks the count.
    [Fact]
    public void An_unparseable_change_then_blur_syncs_without_notifying()
    {
        var booking = new Booking { Seats = 3 };
        var form = RenderSeats(booking, InputUpdateMode.OnBlur);
        var domSync = (RecordingDomValueSync)Services.GetRequiredService<IFormidableDomValueSync>();
        var notifications = 0;
        form.Instance.Engine!.EditContext.OnFieldChanged += (_, _) => notifications++;

        form.Find("input").Change("not-a-number");
        form.Find("input").Blur();

        var expectedId = FormidableFieldId.For(new FieldIdentifier(booking, nameof(Booking.Seats)));
        Assert.Equal([(expectedId, "3")], domSync.Calls);
        Assert.Equal(0, notifications);
    }

    [Fact]
    public async Task Pending_class_appears_during_the_pass_and_clears_after_it()
    {
        var booking = new Booking();
        var validator = new GatedBookingValidator();

        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<Booking>>(0);
            builder.AddComponentParameter(1, "Model", booking);
            builder.AddComponentParameter(2, "Validator", new FluentValidationModelValidator<Booking>(validator));
            builder.AddComponentParameter(3, "Options", new FormidableOptions());
            builder.AddComponentParameter(4, "ChildContent", (RenderFragment<FormidableFormContext>)(_ => inner =>
            {
                inner.OpenComponent<FormidableInputNumber<int>>(0);
                inner.AddComponentParameter(1, "For", (System.Linq.Expressions.Expression<Func<int>>)(() => booking.Seats));
                inner.AddComponentParameter(2, "Value", booking.Seats);
                inner.AddComponentParameter(3, "ValueChanged",
                    EventCallback.Factory.Create<int>(this, v => booking.Seats = v));
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        });
        var form = cut.FindComponent<FormidableForm<Booking>>();

        form.Find("input").Change("4"); // starts the live pass; the gated async rule blocks on Gate

        form.WaitForAssertion(() => Assert.Contains("formidable-pending", form.Find("input").GetAttribute("class")));

        await cut.InvokeAsync(() => validator.Gate.SetResult());

        form.WaitForAssertion(() => Assert.DoesNotContain("formidable-pending", form.Find("input").GetAttribute("class")));
    }

    [Fact]
    public void Unparseable_value_leaves_the_model_unchanged_and_the_rendered_value_reverts()
    {
        var booking = new Booking { Seats = 3 };
        var form = RenderSeats(booking);

        form.Find("input").Change("not-a-number");

        Assert.Equal(3, booking.Seats);

        // Force a re-render (mirroring an unrelated state change on the same page) to observe the
        // rendered value settle back to the model's actual, unchanged value.
        form.Render(parameters => parameters.Add(p => p.Model, booking));

        Assert.Equal("3", form.Find("input").GetAttribute("value"));
    }

    [Fact]
    public void Emptied_non_nullable_value_leaves_the_model_unchanged_and_the_rendered_value_reverts()
    {
        var booking = new Booking { Seats = 3 };
        var form = RenderSeats(booking);

        form.Find("input").Change("");

        Assert.Equal(3, booking.Seats);

        form.Render(parameters => parameters.Add(p => p.Model, booking));

        Assert.Equal("3", form.Find("input").GetAttribute("value"));
    }

    [Fact]
    public void Emptied_nullable_value_commits_null()
    {
        var booking = new Booking { Price = 12.5m };
        var form = RenderPrice(booking);

        form.Find("input").Change("");

        Assert.Null(booking.Price);
    }

    [Fact]
    public void Decimal_conversion_is_culture_invariant_not_culture_sensitive()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            // Under a comma-decimal culture, current-culture conversion would misread "12.5" as
            // 125 (thousands separator) instead of failing loudly — the exact bug this
            // component's invariant-culture binding exists to avoid.
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");

            var booking = new Booking();
            var form = RenderPrice(booking);

            form.Find("input").Change("12.5");

            Assert.Equal(12.5m, booking.Price);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    // The string-projected-but-UpdateOn-honouring AddValueBinding overload shares the base's
    // HandleBlurAsync rather than reimplementing blur-chaining — this pins that the
    // splatted-handler-runs-before-the-engine-notifies contract (see
    // FormidableInputBindingTests) survives through this overload too. A committed change
    // precedes the blur so a notification is pending for the chain's library half to deliver.
    [Fact]
    public void A_splatted_onblur_runs_before_the_library_notifies_the_engine()
    {
        var booking = new Booking();
        var log = new List<string>();
        var form = RenderSeats(booking, InputUpdateMode.OnBlur, ("onblur", EventCallback.Factory.Create<Microsoft.AspNetCore.Components.Web.FocusEventArgs>(this, () => log.Add("consumer"))));

        form.Instance.Engine!.EditContext.OnFieldChanged += (_, _) => log.Add("library");

        form.Find("input").Change("4");
        form.Find("input").Blur();

        form.WaitForAssertion(() => Assert.Equal(["consumer", "library"], log));
    }

    [Fact]
    public void Unsupported_value_type_fails_from_the_static_constructor()
    {
        var booking = new Booking();

        // The static constructor throws InvalidOperationException before any instance is
        // created; the runtime wraps it in TypeInitializationException per standard .NET
        // behaviour for a failing type initializer — the same shape a real consumer sees.
        var ex = Assert.Throws<TypeInitializationException>(() =>
        {
            Render(builder =>
            {
                builder.OpenComponent<FormidableForm<Booking>>(0);
                builder.AddComponentParameter(1, "Model", booking);
                builder.AddComponentParameter(2, "ChildContent", (RenderFragment<FormidableFormContext>)(_ => inner =>
                {
                    inner.OpenComponent<FormidableInputNumber<bool>>(0);
                    inner.CloseComponent();
                }));
                builder.CloseComponent();
            });
        });

        var inner = Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.Contains("does not support the type", inner.Message);
    }
}

public sealed class Booking
{
    public int Seats { get; set; }
    public decimal? Price { get; set; }
}

public sealed class BookingValidator : DraftSubmitValidator<Booking>
{
    protected override void ConfigureDraftRules()
    {
        RuleFor(x => x.Seats).InclusiveBetween(1, 10).WithMessage("Seats must be between 1 and 10");
        RuleFor(x => x.Price).GreaterThan(0).WithMessage("Price must be positive").When(x => x.Price.HasValue);
    }

    protected override void ConfigureSubmitRules()
    {
    }
}

/// <summary>Draft validator whose <see cref="Booking.Seats"/> rule blocks on <see cref="Gate"/> until released, letting tests observe an in-flight pass.</summary>
public sealed class GatedBookingValidator : DraftSubmitValidator<Booking>
{
    public TaskCompletionSource Gate { get; private set; } = new();

    protected override void ConfigureDraftRules() =>
        RuleFor(x => x.Seats).MustAsync(async (_, ct) =>
        {
            await Gate.Task.WaitAsync(ct);
            return true;
        });

    protected override void ConfigureSubmitRules()
    {
    }
}
