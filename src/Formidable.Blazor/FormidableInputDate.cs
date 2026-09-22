using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace Formidable.Blazor;

/// <summary>
/// Reference validated date input: a plain <c>&lt;input type="date"&gt;</c> bound to a date
/// field, wired through <see cref="FormidableInputBase{TValue}"/> for registration, css class,
/// aria output, pending state, and identity — the same extras every kit input gets.
/// <typeparamref name="TValue"/> is constrained at static construction to
/// <see cref="DateTime"/>, <see cref="DateTimeOffset"/>, <see cref="DateOnly"/>, and their
/// nullable forms; any other <typeparamref name="TValue"/> fails before any instance is
/// created, the first time the closed generic type is used: the static constructor throws
/// <see cref="InvalidOperationException"/> naming the unsupported type, and the runtime wraps
/// it in a <see cref="TypeInitializationException"/> per standard .NET behaviour for a failing
/// type initializer.
/// </summary>
/// <remarks>
/// <para>
/// A native <c>&lt;input type="date"&gt;</c>'s <c>value</c> is always the fixed ISO form
/// <c>yyyy-MM-dd</c>, regardless of the browser's locale — not merely period-decimal like a
/// number input, but a specific calendar-and-format pair. This component formats and parses
/// through that exact format string under <see cref="CultureInfo.InvariantCulture"/> rather than
/// the <see cref="FormidableInputBase{TValue}.AddValueBinding(RenderTreeBuilder, int)"/>
/// overload's current-culture conversion: under a non-Gregorian-calendar culture such as Thai
/// (Buddhist calendar) that overload can silently read <c>"2024-01-15"</c> back as a different
/// year entirely, and under some cultures it fails outright. See
/// <see cref="FormidableInputBase{TValue}.AddValueBinding(RenderTreeBuilder, int, string, FormidableInputBase{TValue}.StringValueParser)"/>
/// for the mechanism.
/// </para>
/// <para>
/// A string that fails to parse — including an emptied box when
/// <typeparamref name="TValue"/> is not nullable — leaves the field uncommitted: the model is
/// unchanged, and no blur-delivered notification is armed under <see cref="InputUpdateMode.OnBlur"/>.
/// The box itself is reconciled on <c>blur</c>: a native date input can keep
/// displaying segments it reports as empty (a half-entered date), which no render-tree diff can
/// overwrite because the rendered value and the reported value already agree, so on every blur,
/// in every <see cref="FormidableInputBase{TValue}.UpdateOn"/> mode, the control writes the
/// model's formatted value into the element through <see cref="IFormidableDomValueSync"/> —
/// whatever the box showed, it ends up matching the model. Formidable has no separate
/// binder-message channel; every validation message stays FluentValidation's. Model an optional
/// date as <c>DateOnly?</c>/<c>DateTime?</c> etc. so an emptied box commits
/// <see langword="null"/> instead — a rule such as <c>NotNull()</c> can then say so.
/// </para>
/// <para>
/// Prefer <see cref="InputUpdateMode.OnBlur"/> for <see cref="FormidableInputBase{TValue}.UpdateOn"/>
/// on this component. Chromium fires a native date input's <c>change</c> event once per
/// keyboard-edited segment (day, month, year) rather than once per completed date, so under the
/// default <see cref="InputUpdateMode.OnChange"/> a live validation pass can run — and briefly
/// show a stale verdict — against a year the visitor has not finished typing. Under
/// <see cref="InputUpdateMode.OnBlur"/> the model still commits on every segment's <c>change</c>
/// (so a wrapping form always reads the field's current value), but the engine is notified only
/// once, on <c>blur</c>, once the visitor has moved on and the value has had a chance to settle
/// — and only because those segment commits armed it: tabbing through the field without
/// committing anything notifies nothing.
/// </para>
/// <para>
/// The same consumer guarantees as <see cref="FormidableInputText"/> apply: a consumer-splatted
/// <c>class</c> merges with the computed state class, a consumer-splatted
/// <c>aria-describedby</c> keeps its ids with the computed messages id appended after them while
/// the field has issues, and a consumer-supplied <c>id</c> is
/// ignored in favour of the deterministic <see cref="FormidableFieldId"/>.
/// </para>
/// </remarks>
/// <typeparam name="TValue">The field's date value type.</typeparam>
public sealed class FormidableInputDate<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TValue>
    : FormidableInputBase<TValue>
{
    private const string IsoDateFormat = "yyyy-MM-dd";

    [Inject]
    private IFormidableDomValueSync DomValueSync { get; set; } = default!;

    /// <inheritdoc />
    protected override bool SyncsDomValueOnBlur => true;

    /// <inheritdoc />
    protected override ValueTask SyncDomValueAsync() =>
        DomValueSync.SyncValueAsync(ElementId, FormatValueAsString(Value));

    static FormidableInputDate()
    {
        var targetType = Nullable.GetUnderlyingType(typeof(TValue)) ?? typeof(TValue);
        if (targetType != typeof(DateTime) &&
            targetType != typeof(DateTimeOffset) &&
            targetType != typeof(DateOnly))
        {
            throw new InvalidOperationException(
                $"{typeof(FormidableInputDate<TValue>)} does not support the type '{typeof(TValue)}'. " +
                "Supported types are DateTime, DateTimeOffset, DateOnly, and their nullable forms.");
        }
    }

    /// <inheritdoc />
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        var formattedValue = FormatValueAsString(Value);

        builder.OpenElement(0, "input");
        AddCommonAttributes(builder, 1);
        builder.AddAttribute(5, "type", "date");
        builder.AddAttribute(6, "value", formattedValue);
        AddValueBinding(builder, 7, formattedValue, TryParseValue);
        builder.CloseElement();
    }

    /// <summary>
    /// Formats <paramref name="value"/> as ISO <c>yyyy-MM-dd</c> under
    /// <see cref="CultureInfo.InvariantCulture"/> — the exact form a native
    /// <c>&lt;input type="date"&gt;</c> requires in its <c>value</c> attribute.
    /// </summary>
    private static string? FormatValueAsString(TValue? value)
    {
        if (value is null)
        {
            return null;
        }

        return value switch
        {
            DateTime dateTime => BindConverter.FormatValue(dateTime, IsoDateFormat, CultureInfo.InvariantCulture),
            DateTimeOffset dateTimeOffset => BindConverter.FormatValue(dateTimeOffset, IsoDateFormat, CultureInfo.InvariantCulture),
            DateOnly dateOnly => BindConverter.FormatValue(dateOnly, IsoDateFormat, CultureInfo.InvariantCulture),
            _ => value.ToString(),
        };
    }

    /// <summary>
    /// Parses a committed value string as ISO <c>yyyy-MM-dd</c> under
    /// <see cref="CultureInfo.InvariantCulture"/> — the exact form a native
    /// <c>&lt;input type="date"&gt;</c> sends. An empty string parses to
    /// <see langword="null"/> when <typeparamref name="TValue"/> is nullable, and fails
    /// (<see langword="false"/>) otherwise; anything that is not a well-formed
    /// <c>yyyy-MM-dd</c> date — including a partially typed one such as a half-entered year —
    /// also fails, leaving the field uncommitted.
    /// </summary>
    private static bool TryParseValue(string? value, out TValue? result)
    {
        var isNullable = Nullable.GetUnderlyingType(typeof(TValue)) is not null;

        if (string.IsNullOrEmpty(value))
        {
            result = default;
            return isNullable;
        }

        var targetType = Nullable.GetUnderlyingType(typeof(TValue)) ?? typeof(TValue);
        bool success;
        object parsed;

        if (targetType == typeof(DateTime))
        {
            success = BindConverter.TryConvertToDateTime(value, CultureInfo.InvariantCulture, IsoDateFormat, out var dateTime);
            parsed = dateTime;
        }
        else if (targetType == typeof(DateTimeOffset))
        {
            success = BindConverter.TryConvertToDateTimeOffset(value, CultureInfo.InvariantCulture, IsoDateFormat, out var dateTimeOffset);
            parsed = dateTimeOffset;
        }
        else
        {
            // DateOnly, the only remaining case per the static constructor's check.
            success = BindConverter.TryConvertToDateOnly(value, CultureInfo.InvariantCulture, IsoDateFormat, out var dateOnly);
            parsed = dateOnly;
        }

        result = success ? (TValue?)parsed : default;
        return success;
    }
}
