using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace Formidable.Blazor;

/// <summary>A validated <c>&lt;input type="date"&gt;</c> for a <see cref="DateTime"/>, <see cref="DateTimeOffset"/> or <see cref="DateOnly"/> field, nullable or not, formatted and parsed as ISO <c>yyyy-MM-dd</c> under the invariant culture.</summary>
/// <typeparam name="TValue">The field's date value type.</typeparam>
/// <remarks>
/// Prefer <see cref="InputUpdateMode.OnBlur"/> for <see cref="FormidableInputBase{TValue}.UpdateOn"/>:
/// Chromium fires <c>change</c> once per typed segment (day, month, year), so under
/// <see cref="InputUpdateMode.OnChange"/> a check runs against a year the visitor has not finished.
/// An emptied box commits <see langword="null"/> only for a nullable <typeparamref name="TValue"/>,
/// else nothing: model an optional date as <c>DateOnly?</c> for a <c>NotNull()</c> rule to judge.
/// </remarks>
public sealed class FormidableInputDate<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TValue>
    : FormidableInputBase<TValue>
{
    private const string IsoDateFormat = "yyyy-MM-dd";

    [Inject]
    private IFormidableDomValueSync DomValueSync { get; set; } = default!;

    /// <summary><see langword="true"/>: a date box can display half-entered segments it reports as empty, so the model's value is written back on every blur.</summary>
    protected override bool SyncsDomValueOnBlur => true;

    /// <summary>Writes the ISO-formatted <see cref="FormidableInputBase{TValue}.Value"/> into the element.</summary>
    protected override ValueTask SyncDomValueAsync() =>
        DomValueSync.SyncValueAsync(ElementId, FormatValueAsString(Value));

    /// <summary>Checks <typeparamref name="TValue"/> once, before any instance exists.</summary>
    /// <exception cref="InvalidOperationException"><typeparamref name="TValue"/> is not <see cref="DateTime"/>, <see cref="DateTimeOffset"/>, <see cref="DateOnly"/> or a nullable form of one; the runtime surfaces it wrapped in a <see cref="TypeInitializationException"/> the first time the closed generic is used.</exception>
    static FormidableInputDate()
    {
        var targetType = Nullable.GetUnderlyingType(typeof(TValue)) ?? typeof(TValue);
        if (targetType != typeof(DateTime) &&
            targetType != typeof(DateTimeOffset) &&
            targetType != typeof(DateOnly))
        {
            throw new InvalidOperationException(
                $"{FriendlyTypeName.Of(typeof(FormidableInputDate<TValue>))} does not support the type '{FriendlyTypeName.Of(typeof(TValue))}'. " +
                "Supported types are DateTime, DateTimeOffset, DateOnly, and their nullable forms.");
        }
    }

    /// <summary>Renders the <c>&lt;input type="date"&gt;</c>: the shared attributes, <c>type</c>, <c>value</c>, then the parser-backed commit binding.</summary>
    /// <param name="builder">The render tree builder.</param>
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

    /// <summary>Formats <paramref name="value"/> as ISO <c>yyyy-MM-dd</c> under the invariant culture, the form a native date input's <c>value</c> takes, or <see langword="null"/> for <see langword="null"/>.</summary>
    /// <param name="value">The value to format.</param>
    /// <returns>The ISO date string, or <see langword="null"/>.</returns>
    // Invariant and format-exact because a native date input reports its value as ISO
    // yyyy-MM-dd whatever the browser's locale; the typed overload's current-culture binder can
    // read "2024-01-15" back as a different year under a non-Gregorian calendar culture such as
    // Thai, and fails outright under some others.
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

    /// <summary>Parses <paramref name="value"/> as ISO <c>yyyy-MM-dd</c> under the invariant culture; an empty string parses to <see langword="null"/> only for a nullable <typeparamref name="TValue"/>, and anything malformed or partial fails.</summary>
    /// <param name="value">The string the DOM committed.</param>
    /// <param name="result">The parsed value, or <see langword="null"/> for an emptied nullable field.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> parsed; <see langword="false"/> for a malformed or half-entered date, an emptied non-nullable field included.</returns>
    private static bool TryParseValue(string? value, out TValue? result)
    {
        var underlying = Nullable.GetUnderlyingType(typeof(TValue));

        if (string.IsNullOrEmpty(value))
        {
            result = default;
            return underlying is not null;
        }

        var targetType = underlying ?? typeof(TValue);
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
