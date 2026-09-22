using System.Globalization;
using Bunit;
using FluentValidation;
using Formidable.Blazor.Tests.Fixtures;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;

namespace Formidable.Blazor.Tests;

/// <summary>
/// Pins <see cref="FormidableInputDate{TValue}"/>'s value semantics: the base's five extras
/// (registration/id, css class, aria, pending, <c>UpdateOn</c>) flowing through unchanged, the
/// silent-revert contract for unparseable/emptied input, and — the reason this component exists
/// rather than reusing <see cref="FormidableInputBase{TValue}.AddValueBinding(RenderTreeBuilder, int)"/>
/// directly — ISO <c>yyyy-MM-dd</c> round-tripping under a non-Gregorian current culture.
/// </summary>
public class FormidableInputDateTests : BunitContext
{
    public FormidableInputDateTests()
    {
        Services.AddFormidable();
        Services.AddSingleton<IValidator<Trip>>(new TripValidator());
        // The component injects the DOM value sync; the recording fake keeps these tests off
        // JS interop (FormidableInputDomSyncTests owns the sync assertions).
        Services.AddSingleton<IFormidableDomValueSync>(new RecordingDomValueSync());
    }

    private IRenderedComponent<FormidableForm<Trip>> RenderReturnDate(
        Trip trip, InputUpdateMode updateOn = InputUpdateMode.OnChange, params (string Name, object Value)[] attributes)
    {
        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<Trip>>(0);
            builder.AddComponentParameter(1, "Model", trip);
            builder.AddComponentParameter(2, "ChildContent", (RenderFragment<FormidableFormContext>)(_ => inner =>
            {
                inner.OpenComponent<FormidableInputDate<DateTime>>(0);
                inner.AddComponentParameter(1, "For", (System.Linq.Expressions.Expression<Func<DateTime>>)(() => trip.ReturnDate));
                inner.AddComponentParameter(2, "Value", trip.ReturnDate);
                inner.AddComponentParameter(3, "ValueChanged",
                    EventCallback.Factory.Create<DateTime>(this, v => trip.ReturnDate = v));
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
        return cut.FindComponent<FormidableForm<Trip>>();
    }

    private IRenderedComponent<FormidableForm<Trip>> RenderDepartureDate(Trip trip)
    {
        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<Trip>>(0);
            builder.AddComponentParameter(1, "Model", trip);
            builder.AddComponentParameter(2, "ChildContent", (RenderFragment<FormidableFormContext>)(_ => inner =>
            {
                inner.OpenComponent<FormidableInputDate<DateOnly?>>(0);
                inner.AddComponentParameter(1, "For", (System.Linq.Expressions.Expression<Func<DateOnly?>>)(() => trip.DepartureDate));
                inner.AddComponentParameter(2, "Value", trip.DepartureDate);
                inner.AddComponentParameter(3, "ValueChanged",
                    EventCallback.Factory.Create<DateOnly?>(this, v => trip.DepartureDate = v));
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        });
        return cut.FindComponent<FormidableForm<Trip>>();
    }

    private IRenderedComponent<FormidableForm<Trip>> RenderBookedAt(Trip trip)
    {
        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<Trip>>(0);
            builder.AddComponentParameter(1, "Model", trip);
            builder.AddComponentParameter(2, "ChildContent", (RenderFragment<FormidableFormContext>)(_ => inner =>
            {
                inner.OpenComponent<FormidableInputDate<DateTimeOffset?>>(0);
                inner.AddComponentParameter(1, "For", (System.Linq.Expressions.Expression<Func<DateTimeOffset?>>)(() => trip.BookedAt));
                inner.AddComponentParameter(2, "Value", trip.BookedAt);
                inner.AddComponentParameter(3, "ValueChanged",
                    EventCallback.Factory.Create<DateTimeOffset?>(this, v => trip.BookedAt = v));
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        });
        return cut.FindComponent<FormidableForm<Trip>>();
    }

    [Fact]
    public void Renders_a_date_input_with_the_type_attribute_in_the_component_wins_position()
    {
        var trip = new Trip();
        var form = RenderReturnDate(trip, attributes: ("type", "text"));

        Assert.Equal("date", form.Find("input").GetAttribute("type"));
    }

    [Fact]
    public void Value_formats_as_iso_yyyy_mm_dd()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            // Thai's default calendar is Buddhist: formatting under it instead of invariant
            // would render a different year (2569, not 2026) — the format direction's half of
            // the same culture-invariance guarantee the parse-direction tests below pin.
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("th-TH");

            var trip = new Trip { ReturnDate = new DateTime(2026, 3, 7) };
            var form = RenderReturnDate(trip);

            Assert.Equal("2026-03-07", form.Find("input").GetAttribute("value"));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Change_updates_model_and_triggers_live_validation()
    {
        var trip = new Trip();
        var form = RenderReturnDate(trip);

        form.Find("input").Change("1999-01-01"); // fails TripValidator's GreaterThan rule

        Assert.Equal(new DateTime(1999, 1, 1), trip.ReturnDate);
        form.WaitForAssertion(() => Assert.Contains("formidable-invalid", form.Find("input").GetAttribute("class")));
    }

    [Fact]
    public void Element_id_matches_field_id_convention()
    {
        var trip = new Trip();
        var form = RenderReturnDate(trip);

        Assert.Equal(
            FormidableFieldId.For(new FieldIdentifier(trip, nameof(Trip.ReturnDate))),
            form.Find("input").GetAttribute("id"));
    }

    [Fact]
    public void Aria_attributes_reflect_error_state()
    {
        var trip = new Trip();
        var form = RenderReturnDate(trip);

        form.Find("input").Change("1999-01-01");

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
        var trip = new Trip();
        var form = RenderReturnDate(trip, attributes: ("class", "form-control"));

        form.Find("input").Change("1999-01-01");

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
        var trip = new Trip();
        var form = RenderReturnDate(trip, InputUpdateMode.OnInput);

        form.Find("input").Input("2026-05-05");

        Assert.Equal(new DateTime(2026, 5, 5), trip.ReturnDate);
    }

    // Pins the recommended pairing this component's own docs teach: the model commits on every
    // Chromium per-segment "change" event, but no live pass runs until blur — so a half-typed
    // date never flashes a stale verdict.
    [Fact]
    public void Blur_mode_commits_the_value_on_change_without_starting_a_live_pass()
    {
        var trip = new Trip();
        var form = RenderReturnDate(trip, InputUpdateMode.OnBlur);

        form.Find("input").Change("1999-01-01");

        Assert.Equal(new DateTime(1999, 1, 1), trip.ReturnDate);
        Assert.Equal(string.Empty, form.Find("input").GetAttribute("class"));
    }

    [Fact]
    public void Blur_mode_notifies_the_engine_on_blur()
    {
        var trip = new Trip();
        var form = RenderReturnDate(trip, InputUpdateMode.OnBlur);

        form.Find("input").Change("1999-01-01");
        form.Find("input").Blur();

        form.WaitForAssertion(() => Assert.Contains("formidable-invalid", form.Find("input").GetAttribute("class")));
    }

    [Fact]
    public async Task Pending_class_appears_during_the_pass_and_clears_after_it()
    {
        var trip = new Trip();
        var validator = new GatedTripValidator();

        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<Trip>>(0);
            builder.AddComponentParameter(1, "Model", trip);
            builder.AddComponentParameter(2, "Validator", new FluentValidationModelValidator<Trip>(validator));
            builder.AddComponentParameter(3, "Options", new FormidableOptions());
            builder.AddComponentParameter(4, "ChildContent", (RenderFragment<FormidableFormContext>)(_ => inner =>
            {
                inner.OpenComponent<FormidableInputDate<DateTime>>(0);
                inner.AddComponentParameter(1, "For", (System.Linq.Expressions.Expression<Func<DateTime>>)(() => trip.ReturnDate));
                inner.AddComponentParameter(2, "Value", trip.ReturnDate);
                inner.AddComponentParameter(3, "ValueChanged",
                    EventCallback.Factory.Create<DateTime>(this, v => trip.ReturnDate = v));
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        });
        var form = cut.FindComponent<FormidableForm<Trip>>();

        form.Find("input").Change("2026-05-05"); // starts the live pass; the gated async rule blocks on Gate

        form.WaitForAssertion(() => Assert.Contains("formidable-pending", form.Find("input").GetAttribute("class")));

        await cut.InvokeAsync(() => validator.Gate.SetResult());

        form.WaitForAssertion(() => Assert.DoesNotContain("formidable-pending", form.Find("input").GetAttribute("class")));
    }

    [Fact]
    public void Unparseable_value_leaves_the_model_unchanged_and_the_rendered_value_reverts()
    {
        var trip = new Trip { ReturnDate = new DateTime(2026, 3, 7) };
        var form = RenderReturnDate(trip);

        form.Find("input").Change("not-a-date");

        Assert.Equal(new DateTime(2026, 3, 7), trip.ReturnDate);

        form.Render(parameters => parameters.Add(p => p.Model, trip));

        Assert.Equal("2026-03-07", form.Find("input").GetAttribute("value"));
    }

    [Fact]
    public void Emptied_non_nullable_value_leaves_the_model_unchanged_and_the_rendered_value_reverts()
    {
        var trip = new Trip { ReturnDate = new DateTime(2026, 3, 7) };
        var form = RenderReturnDate(trip);

        form.Find("input").Change("");

        Assert.Equal(new DateTime(2026, 3, 7), trip.ReturnDate);

        form.Render(parameters => parameters.Add(p => p.Model, trip));

        Assert.Equal("2026-03-07", form.Find("input").GetAttribute("value"));
    }

    [Fact]
    public void Emptied_nullable_value_commits_null()
    {
        var trip = new Trip { DepartureDate = new DateOnly(2026, 3, 7) };
        var form = RenderDepartureDate(trip);

        form.Find("input").Change("");

        Assert.Null(trip.DepartureDate);
    }

    [Fact]
    public void DateOnly_conversion_is_culture_invariant_not_culture_sensitive()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            // Thai uses the Buddhist calendar: current-culture conversion would silently read
            // "2024-01-15" back as a different year entirely — the exact bug this component's
            // invariant, format-exact binding exists to avoid.
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("th-TH");

            var trip = new Trip();
            var form = RenderDepartureDate(trip);

            form.Find("input").Change("2024-01-15");

            Assert.Equal(new DateOnly(2024, 1, 15), trip.DepartureDate);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void BookedAt_value_formats_as_iso_yyyy_mm_dd()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            // Same format-direction guarantee as Value_formats_as_iso_yyyy_mm_dd above, pinned
            // for the third supported type: DateTimeOffset's own FormatValueAsString branch has
            // no coverage anywhere else in this file.
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("th-TH");

            var trip = new Trip { BookedAt = new DateTimeOffset(2026, 3, 7, 0, 0, 0, TimeSpan.Zero) };
            var form = RenderBookedAt(trip);

            Assert.Equal("2026-03-07", form.Find("input").GetAttribute("value"));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Emptied_nullable_DateTimeOffset_value_commits_null()
    {
        var trip = new Trip { BookedAt = new DateTimeOffset(2026, 3, 7, 0, 0, 0, TimeSpan.Zero) };
        var form = RenderBookedAt(trip);

        form.Find("input").Change("");

        Assert.Null(trip.BookedAt);
    }

    [Fact]
    public void DateTimeOffset_conversion_is_culture_invariant_not_culture_sensitive()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            // Thai uses the Buddhist calendar: current-culture conversion would silently read
            // "2024-01-15" back as a different year entirely — the exact bug this component's
            // invariant, format-exact binding exists to avoid. The expected offset is computed
            // rather than hardcoded: parsing a date with no offset in the string assigns the
            // local time zone's offset AS OF that date (DST-aware), which is what
            // BindConverter.TryConvertToDateTimeOffset does through the real binder — hardcoding
            // an offset such as UTC would make this test fail on any machine (or any date) where
            // the local zone's offset differs.
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("th-TH");

            var trip = new Trip();
            var form = RenderBookedAt(trip);

            form.Find("input").Change("2024-01-15");

            var expectedOffset = TimeZoneInfo.Local.GetUtcOffset(new DateTime(2024, 1, 15));
            Assert.Equal(new DateTimeOffset(2024, 1, 15, 0, 0, 0, expectedOffset), trip.BookedAt);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void A_half_typed_year_still_parses_as_a_valid_iso_date_the_page_recommends_OnBlur_for_this()
    {
        // Documents the known Chromium per-segment "change" firing behaviour: a syntactically
        // well-formed but implausible partial year is not this component's business to reject —
        // FormatString "yyyy-MM-dd" accepts it, exactly as native parsing would. UpdateOn.OnBlur
        // (pinned above) is the component's answer: the model may hold this value transiently,
        // but no live pass sees it until the visitor leaves the field with a settled value.
        var trip = new Trip();
        var form = RenderReturnDate(trip);

        form.Find("input").Change("0019-01-15");

        Assert.Equal(new DateTime(19, 1, 15), trip.ReturnDate);
    }

    [Fact]
    public void Unsupported_value_type_fails_from_the_static_constructor()
    {
        var trip = new Trip();

        var ex = Assert.Throws<TypeInitializationException>(() =>
        {
            Render(builder =>
            {
                builder.OpenComponent<FormidableForm<Trip>>(0);
                builder.AddComponentParameter(1, "Model", trip);
                builder.AddComponentParameter(2, "ChildContent", (RenderFragment<FormidableFormContext>)(_ => inner =>
                {
                    inner.OpenComponent<FormidableInputDate<int>>(0);
                    inner.CloseComponent();
                }));
                builder.CloseComponent();
            });
        });

        var inner = Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.Contains("does not support the type", inner.Message);
    }
}

public sealed class Trip
{
    public DateOnly? DepartureDate { get; set; }
    public DateTime ReturnDate { get; set; }
    public DateTimeOffset? BookedAt { get; set; }
}

public sealed class TripValidator : DraftSubmitValidator<Trip>
{
    protected override void ConfigureDraftRules() =>
        RuleFor(x => x.ReturnDate).GreaterThan(new DateTime(2000, 1, 1)).WithMessage("Return date looks wrong");

    protected override void ConfigureSubmitRules()
    {
    }
}

/// <summary>Draft validator whose <see cref="Trip.ReturnDate"/> rule blocks on <see cref="Gate"/> until released, letting tests observe an in-flight pass.</summary>
public sealed class GatedTripValidator : DraftSubmitValidator<Trip>
{
    public TaskCompletionSource Gate { get; private set; } = new();

    protected override void ConfigureDraftRules() =>
        RuleFor(x => x.ReturnDate).MustAsync(async (_, ct) =>
        {
            await Gate.Task.WaitAsync(ct);
            return true;
        });

    protected override void ConfigureSubmitRules()
    {
    }
}
