using Bunit;
using FluentValidation;
using Formidable.Blazor.Tests.Fixtures;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace Formidable.Blazor.Tests;

/// <summary>
/// Pins the DOM value sync the kit's number and date inputs perform on <c>blur</c>: a native
/// number or date input can display text it reports as empty (an unparseable <c>"e3"</c>, a
/// half-typed date), so no render-tree diff ever sees anything to overwrite and the box silently
/// disagrees with the model. Both controls therefore rewrite their DOM value to the model's
/// formatted value on every blur, in every <see cref="InputUpdateMode"/> — while the other kit
/// inputs, whose DOM never lies, stay out of it.
/// </summary>
public class FormidableInputDomSyncTests : BunitContext
{
    private readonly RecordingDomValueSync _domSync = new();

    public FormidableInputDomSyncTests()
    {
        Services.AddFormidable();
        Services.AddSingleton<IValidator<Booking>>(new BookingValidator());
        Services.AddSingleton<IValidator<Outing>>(new InlineValidator<Outing>());
        Services.AddSingleton<IFormidableDomValueSync>(_domSync);
    }

    private IRenderedComponent<FormidableForm<Booking>> RenderSeats(
        Booking booking, InputUpdateMode updateOn = InputUpdateMode.OnChange, params (string Name, object Value)[] attributes)
    {
        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<Booking>>(0);
            builder.AddComponentParameter(1, "Model", booking);
            builder.AddComponentParameter(2, "ChildContent", (RenderFragment)(inner =>
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

    private IRenderedComponent<FormidableForm<Booking>> RenderPrice(Booking booking)
    {
        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<Booking>>(0);
            builder.AddComponentParameter(1, "Model", booking);
            builder.AddComponentParameter(2, "ChildContent", (RenderFragment)(inner =>
            {
                inner.OpenComponent<FormidableInputNumber<decimal?>>(0);
                inner.AddComponentParameter(1, "For", (System.Linq.Expressions.Expression<Func<decimal?>>)(() => booking.Price));
                inner.AddComponentParameter(2, "Value", booking.Price);
                inner.AddComponentParameter(3, "ValueChanged",
                    EventCallback.Factory.Create<decimal?>(this, v => booking.Price = v));
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        });
        return cut.FindComponent<FormidableForm<Booking>>();
    }

    private IRenderedComponent<FormidableForm<Outing>> RenderOuting(
        Outing outing, InputUpdateMode updateOn = InputUpdateMode.OnChange, bool textInput = false)
    {
        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<Outing>>(0);
            builder.AddComponentParameter(1, "Model", outing);
            builder.AddComponentParameter(2, "ChildContent", (RenderFragment)(inner =>
            {
                if (textInput)
                {
                    inner.OpenComponent<FormidableInputText>(0);
                    inner.AddComponentParameter(1, "For", (System.Linq.Expressions.Expression<Func<string?>>)(() => outing.Notes));
                    inner.AddComponentParameter(2, "Value", outing.Notes);
                    inner.AddComponentParameter(3, "ValueChanged",
                        EventCallback.Factory.Create<string?>(this, v => outing.Notes = v));
                }
                else
                {
                    inner.OpenComponent<FormidableInputDate<DateOnly?>>(0);
                    inner.AddComponentParameter(1, "For", (System.Linq.Expressions.Expression<Func<DateOnly?>>)(() => outing.Day));
                    inner.AddComponentParameter(2, "Value", outing.Day);
                    inner.AddComponentParameter(3, "ValueChanged",
                        EventCallback.Factory.Create<DateOnly?>(this, v => outing.Day = v));
                    inner.AddComponentParameter(4, "UpdateOn", updateOn);
                }

                inner.CloseComponent();
            }));
            builder.CloseComponent();
        });
        return cut.FindComponent<FormidableForm<Outing>>();
    }

    [Fact]
    public void Number_blur_rewrites_the_dom_value_to_the_model()
    {
        var booking = new Booking { Seats = 3 };
        var form = RenderSeats(booking);

        // The browser reports an unparseable entry as "": the model keeps 3, but the box keeps
        // displaying what was typed. Blur is where the control writes the model's value back.
        form.Find("input").Change("not-a-number");
        form.Find("input").Blur();

        var expectedId = FormidableFieldId.For(new FieldIdentifier(booking, nameof(Booking.Seats)));
        form.WaitForAssertion(() => Assert.Equal([(expectedId, "3")], _domSync.Calls));
    }

    [Fact]
    public void Number_blur_syncs_null_as_the_cleared_value()
    {
        var booking = new Booking { Price = 12.5m };
        var form = RenderPrice(booking);

        // For a nullable field the reported-empty commit legitimately lands null — the sync
        // carries null so the ghost text is cleared rather than restored.
        form.Find("input").Change("");
        form.Find("input").Blur();

        var expectedId = FormidableFieldId.For(new FieldIdentifier(booking, nameof(Booking.Price)));
        form.WaitForAssertion(() => Assert.Equal([(expectedId, (string?)null)], _domSync.Calls));
    }

    [Fact]
    public void Date_blur_rewrites_the_dom_value_to_the_model()
    {
        var outing = new Outing { Day = new DateOnly(2024, 1, 15) };
        var form = RenderOuting(outing);

        form.Find("input").Blur();

        var expectedId = FormidableFieldId.For(new FieldIdentifier(outing, nameof(Outing.Day)));
        form.WaitForAssertion(() => Assert.Equal([(expectedId, "2024-01-15")], _domSync.Calls));
    }

    [Fact]
    public void Blur_mode_syncs_before_notifying_the_engine()
    {
        var outing = new Outing { Day = new DateOnly(2024, 1, 15) };
        var form = RenderOuting(outing, InputUpdateMode.OnBlur);

        var log = new List<string>();
        _domSync.OnSync = () => log.Add("sync");
        form.Instance.Engine!.EditContext.OnFieldChanged += (_, _) => log.Add("notify");

        form.Find("input").Blur();

        // The DOM is reconciled before the engine is told the field settled, so the live pass
        // renders against a box that already matches the model.
        form.WaitForAssertion(() => Assert.Equal(["sync", "notify"], log));
    }

    [Fact]
    public void A_splatted_onblur_runs_before_the_sync()
    {
        var log = new List<string>();
        _domSync.OnSync = () => log.Add("sync");

        var booking = new Booking { Seats = 3 };
        var form = RenderSeats(booking, attributes: ("onblur",
            EventCallback.Factory.Create<FocusEventArgs>(this, () => log.Add("consumer"))));

        form.Find("input").Blur();

        // Binding blur for the sync must not swallow a consumer's own handler: it chains first,
        // the same contract InputUpdateMode.OnBlur documents.
        form.WaitForAssertion(() => Assert.Equal(["consumer", "sync"], log));
    }

    [Fact]
    public void Text_input_binds_no_blur_outside_blur_mode()
    {
        var outing = new Outing();
        var form = RenderOuting(outing, textInput: true);

        // A text input's DOM never disagrees with what it reports, so the sync stays scoped to
        // the two controls whose DOM can lie — no blur handler is bound here.
        Assert.Throws<MissingEventHandlerException>(() => form.Find("input").Blur());
        Assert.Empty(_domSync.Calls);
    }
}

public sealed class Outing
{
    public DateOnly? Day { get; set; }
    public string? Notes { get; set; }
}
