using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace Formidable.Blazor;

/// <summary>
/// Reference validated numeric input: a plain <c>&lt;input type="number"&gt;</c> bound to a
/// numeric field, wired through <see cref="FormidableInputBase{TValue}"/> for registration, css
/// class, aria output, pending state, and identity — the same extras every kit input gets.
/// <typeparamref name="TValue"/> is constrained at static construction to <see cref="int"/>,
/// <see cref="long"/>, <see cref="short"/>, <see cref="float"/>, <see cref="double"/>,
/// <see cref="decimal"/>, and their nullable forms — the same set native
/// <c>InputNumber&lt;TValue&gt;</c> supports; any other <typeparamref name="TValue"/> fails
/// before any instance is created, the first time the closed generic type is used: the static
/// constructor throws <see cref="InvalidOperationException"/> naming the unsupported type, and
/// the runtime wraps it in a <see cref="TypeInitializationException"/> per standard .NET
/// behaviour for a failing type initializer — the same shape native <c>InputNumber&lt;TValue&gt;</c>
/// fails with for an unsupported type of its own.
/// </summary>
/// <remarks>
/// <para>
/// Value conversion is culture-invariant, not culture-sensitive: a native <c>&lt;input
/// type="number"&gt;</c>'s <c>value</c> is always period-decimal (<c>"12.5"</c>, never
/// <c>"12,5"</c>) regardless of the browser's locale, so this component formats and parses
/// through <see cref="CultureInfo.InvariantCulture"/> rather than the
/// <see cref="FormidableInputBase{TValue}.AddValueBinding(RenderTreeBuilder, int)"/> overload's
/// current-culture conversion — using that overload here would silently misread <c>"12.5"</c> as
/// <c>125</c> under a comma-decimal culture instead of failing loudly. See
/// <see cref="FormidableInputBase{TValue}.AddValueBinding(RenderTreeBuilder, int, string, FormidableInputBase{TValue}.StringValueParser)"/>
/// for the mechanism.
/// </para>
/// <para>
/// Renders <c>step="any"</c> by default, in the consumer-wins position — before the splat,
/// unlike <c>type</c> (see
/// <see cref="FormidableInputBase{TValue}.AddCommonAttributes(RenderTreeBuilder, int)"/>) — so a
/// consumer's own splatted <c>step</c> overrides the default rather than losing the
/// duplicate-attribute race, matching native <c>InputNumber&lt;TValue&gt;</c>'s own handling.
/// HTML's own default <c>step</c> is <c>1</c>, which makes any fractional value a native
/// <c>stepMismatch</c>; since <c>FormidableForm</c> renders no <c>novalidate</c> (the framework's
/// <c>EditForm</c> does not add one), a <c>&lt;button type="submit"&gt;</c> against a
/// <c>stepMismatch</c> field never reaches Blazor's submit handler at all — the browser blocks
/// the submit event itself and shows its own native constraint-validation message, not
/// FluentValidation's. The default <c>step="any"</c> disables that native check so every
/// fractional value reaches the model and only FluentValidation's own rules judge it, matching
/// the "every validation message stays FluentValidation's" guarantee below; a consumer who
/// splats their own <c>step</c> opts back into the browser's native constraint UI for values
/// that mismatch it.
/// </para>
/// <para>
/// A string that fails to parse — including an emptied box when
/// <typeparamref name="TValue"/> is not nullable — leaves the field uncommitted: the model is
/// unchanged, and no blur-delivered notification is armed under <see cref="InputUpdateMode.OnBlur"/>.
/// The box itself is reconciled on <c>blur</c>: a native number input can keep
/// displaying text it reports as empty (<c>"e3"</c>, a lone minus — the characters the browser
/// itself admits for scientific notation), which no render-tree diff can overwrite because the
/// rendered value and the reported value already agree, so on every blur, in every
/// <see cref="FormidableInputBase{TValue}.UpdateOn"/> mode, the control writes the model's
/// formatted value into the element through <see cref="IFormidableDomValueSync"/> — whatever the
/// box showed, it ends up matching the model. Formidable has no separate binder-message channel;
/// every validation message stays FluentValidation's. Model an optional number as
/// <c>int?</c>/<c>decimal?</c> etc. so an emptied box commits <see langword="null"/> instead — a
/// rule such as <c>NotNull()</c> can then say so.
/// </para>
/// <para>
/// The same consumer guarantees as <see cref="FormidableInputText"/> apply: a consumer-splatted
/// <c>class</c> merges with the computed state class, and a consumer-supplied <c>id</c> is
/// ignored in favour of the deterministic <see cref="FormidableFieldId"/>.
/// <see cref="FormidableInputBase{TValue}.UpdateOn"/> is fully honoured, exactly as it is for
/// <see cref="FormidableInputText"/>.
/// </para>
/// </remarks>
/// <typeparam name="TValue">The field's numeric value type.</typeparam>
public sealed class FormidableInputNumber<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TValue>
    : FormidableInputBase<TValue>
{
    [Inject]
    private IFormidableDomValueSync DomValueSync { get; set; } = default!;

    private protected override bool SyncsDomValueOnBlur => true;

    private protected override ValueTask SyncDomValueAsync() =>
        DomValueSync.SyncValueAsync(ElementId, FormatValueAsString(Value));

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
                $"{typeof(FormidableInputNumber<TValue>)} does not support the type '{typeof(TValue)}'. " +
                "Supported types are int, long, short, float, double, decimal, and their nullable forms.");
        }
    }

    /// <inheritdoc />
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        var formattedValue = FormatValueAsString(Value);

        builder.OpenElement(0, "input");
        builder.AddAttribute(1, "step", "any");
        AddCommonAttributes(builder, 2);
        builder.AddAttribute(6, "type", "number");
        builder.AddAttribute(7, "value", formattedValue);
        AddValueBinding(builder, 8, formattedValue, TryParseValue);
        builder.CloseElement();
    }

    /// <summary>
    /// Formats <paramref name="value"/> the same way native <c>InputNumber&lt;TValue&gt;</c>
    /// does: <see cref="CultureInfo.InvariantCulture"/>, so the rendered string is always
    /// period-decimal regardless of the current thread's culture.
    /// </summary>
    private static string? FormatValueAsString(TValue? value) =>
        value is null ? null : (string?)BindConverter.FormatValue(value, CultureInfo.InvariantCulture);

    /// <summary>
    /// Parses a committed value string the same way native <c>InputNumber&lt;TValue&gt;</c>
    /// does: <see cref="BindConverter.TryConvertTo{T}(object?, CultureInfo?, out T)"/> under
    /// <see cref="CultureInfo.InvariantCulture"/>. Returns <see langword="true"/> with a
    /// <see langword="null"/> <paramref name="result"/> for an empty string when
    /// <typeparamref name="TValue"/> is nullable, and <see langword="false"/> for an empty
    /// string or unparseable text otherwise — the caller leaves the field uncommitted either way.
    /// </summary>
    private static bool TryParseValue(string? value, out TValue? result) =>
        BindConverter.TryConvertTo(value, CultureInfo.InvariantCulture, out result);
}
