using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace Formidable.Blazor;

/// <summary>A validated <c>&lt;input type="number"&gt;</c> for an <see cref="int"/>, <see cref="long"/>, <see cref="short"/>, <see cref="float"/>, <see cref="double"/> or <see cref="decimal"/> field, nullable or not, formatted and parsed under the invariant culture.</summary>
/// <typeparam name="TValue">The field's numeric value type.</typeparam>
/// <remarks>
/// Renders <c>step="any"</c> ahead of the splat, so a <c>step</c> of your own wins. An emptied
/// box commits <see langword="null"/> only for a nullable <typeparamref name="TValue"/>, else
/// nothing: model an optional number as <c>int?</c> or <c>decimal?</c> for a
/// <c>NotNull()</c> rule to judge.
/// </remarks>
public sealed class FormidableInputNumber<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TValue>
    : FormidableInputBase<TValue>
{
    [Inject]
    private IFormidableDomValueSync DomValueSync { get; set; } = default!;

    /// <summary><see langword="true"/>: a number box can display text it reports as empty (<c>e3</c>, a lone minus), so the model's value is written back on every blur.</summary>
    protected override bool SyncsDomValueOnBlur => true;

    /// <summary>Writes the invariant-formatted <see cref="FormidableInputBase{TValue}.Value"/> into the element.</summary>
    protected override ValueTask SyncDomValueAsync() =>
        DomValueSync.SyncValueAsync(ElementId, FormatValueAsString(Value));

    /// <summary>Checks <typeparamref name="TValue"/> once, before any instance exists, against the set native <c>InputNumber</c> supports.</summary>
    /// <exception cref="InvalidOperationException"><typeparamref name="TValue"/> is not <see cref="int"/>, <see cref="long"/>, <see cref="short"/>, <see cref="float"/>, <see cref="double"/>, <see cref="decimal"/> or a nullable form of one; the runtime surfaces it wrapped in a <see cref="TypeInitializationException"/> the first time the closed generic is used.</exception>
    static FormidableInputNumber()
    {
        var targetType = Nullable.GetUnderlyingType(typeof(TValue)) ?? typeof(TValue);
        if (targetType != typeof(int) &&
            targetType != typeof(long) &&
            targetType != typeof(short) &&
            targetType != typeof(float) &&
            targetType != typeof(double) &&
            targetType != typeof(decimal))
        {
            throw new InvalidOperationException(
                $"{FriendlyTypeName.Of(typeof(FormidableInputNumber<TValue>))} does not support the type '{FriendlyTypeName.Of(typeof(TValue))}'. " +
                "Supported types are int, long, short, float, double, decimal, and their nullable forms.");
        }
    }

    /// <summary>Renders the <c>&lt;input type="number"&gt;</c>: <c>step="any"</c> ahead of the splat, the shared attributes, <c>type</c>, <c>value</c>, then the parser-backed commit binding.</summary>
    /// <param name="builder">The render tree builder.</param>
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        var formattedValue = FormatValueAsString(Value);

        builder.OpenElement(0, "input");
        // step="any" sits before the splat, in the consumer-wins position, matching native
        // InputNumber. HTML's own default step is 1, which makes any fractional value a native
        // stepMismatch: novalidate switches off interactive validation but not the constraint
        // computation, so the field would still match :invalid and the spinner would snap to
        // whole numbers, and a form without novalidate (attach mode's EditForm, or a splat that
        // removed the default) would block the submit with the browser's own message in front of
        // FluentValidation's. step="any" retires the mismatch at the source in every one of those
        // forms; a consumer who splats their own step opts back into whatever their form's
        // novalidate answer leaves on.
        builder.AddAttribute(1, "step", "any");
        AddCommonAttributes(builder, 2);
        builder.AddAttribute(6, "type", "number");
        builder.AddAttribute(7, "value", formattedValue);
        AddValueBinding(builder, 8, formattedValue, TryParseValue);
        builder.CloseElement();
    }

    /// <summary>Formats <paramref name="value"/> under the invariant culture, as native <c>InputNumber</c> does, or <see langword="null"/> for <see langword="null"/>.</summary>
    /// <param name="value">The value to format.</param>
    /// <returns>The period-decimal string the element's <c>value</c> takes, or <see langword="null"/>.</returns>
    // Invariant because a native number input reports its value period-decimal whatever the
    // browser's locale; the typed overload's current-culture binder would misread "12.5" as 125
    // under a comma-decimal culture instead of failing loudly.
    private static string? FormatValueAsString(TValue? value) =>
        value is null ? null : (string?)BindConverter.FormatValue(value, CultureInfo.InvariantCulture);

    /// <summary>Parses <paramref name="value"/> under the invariant culture, as native <c>InputNumber</c> does; an empty string parses to <see langword="null"/> only for a nullable <typeparamref name="TValue"/>.</summary>
    /// <param name="value">The string the DOM committed.</param>
    /// <param name="result">The parsed value, or <see langword="null"/> for an emptied nullable field.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> parsed; <see langword="false"/> for text the converter rejects, an emptied non-nullable field included.</returns>
    private static bool TryParseValue(string? value, out TValue? result) =>
        BindConverter.TryConvertTo(value, CultureInfo.InvariantCulture, out result);
}
