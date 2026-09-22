using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace Formidable.Blazor;

/// <summary>A validated <c>&lt;select&gt;</c> for a field of any type native <c>InputSelect</c> converts (<see cref="string"/>, <see cref="bool"/>, an enum, anything <see cref="BindConverter"/> reads from a string); its <c>&lt;option&gt;</c> elements are <see cref="ChildContent"/>.</summary>
/// <typeparam name="TValue">The field's value type.</typeparam>
/// <remarks>
/// An option's <c>value</c> must be the string this component formats for
/// <typeparamref name="TValue"/> (<see cref="object.ToString"/>, with <see cref="bool"/> as
/// <c>"true"</c> or <c>"false"</c>); a blank option clears a <c>bool?</c> to
/// <see langword="null"/>, where native formats <see langword="null"/> as <c>"false"</c>. An
/// array-typed <typeparamref name="TValue"/> (multi-select) is out of scope.
/// </remarks>
public sealed class FormidableInputSelect<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TValue>
    : FormidableInputBase<TValue>
{
    /// <summary>The <c>&lt;option&gt;</c> elements to render inside the select.</summary>
    [Parameter]
    public RenderFragment? ChildContent { get; set; }

    /// <summary>Renders the <c>&lt;select&gt;</c>: the shared attributes, the formatted <c>value</c>, the string-projected commit binding, then <see cref="ChildContent"/>.</summary>
    /// <param name="builder">The render tree builder.</param>
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        var formattedValue = FormatValueAsString(Value);

        builder.OpenElement(0, "select");
        AddCommonAttributes(builder, 1);
        builder.AddAttribute(5, "value", formattedValue);
        AddValueBinding(builder, 6, formattedValue, TryCommitAsync);
        builder.AddContent(8, ChildContent);
        builder.CloseElement();
    }

    /// <summary>The commit step: parses the option string and commits it, or commits nothing for a string that does not parse.</summary>
    /// <param name="value">The option string the DOM committed.</param>
    /// <returns><see langword="true"/> when the string parsed and the value was committed.</returns>
    /// <exception cref="InvalidOperationException"><typeparamref name="TValue"/> has no conversion from a string, as <see cref="TryParseValue"/> reports.</exception>
    private async Task<bool> TryCommitAsync(string? value)
    {
        if (!TryParseValue(value, out var parsed))
        {
            return false;
        }

        await CommitValueAsync(parsed);
        return true;
    }

    /// <summary>Formats <paramref name="value"/> as native <c>InputSelect</c> does: <see cref="bool"/> as <c>"true"</c> or <c>"false"</c>, everything else by <see cref="object.ToString"/>, and <see langword="null"/> as <see langword="null"/>.</summary>
    /// <param name="value">The value to format.</param>
    /// <returns>The string the selected option's <c>value</c> must match, or <see langword="null"/> for no selection.</returns>
    // Bools are spelled out because BindConverter reserves boolean conversion for conditional
    // HTML attributes, not form values.
    private static string? FormatValueAsString(TValue? value) =>
        value switch
        {
            bool boolValue => boolValue ? "true" : "false",
            _ => value?.ToString(),
        };

    /// <summary>Parses an option string as native <c>InputSelect</c> does: <see cref="bool"/> and <c>bool?</c> special-cased, everything else through <see cref="BindConverter.TryConvertTo{T}(object, CultureInfo, out T)"/> under the current culture.</summary>
    /// <param name="value">The option string the DOM committed.</param>
    /// <param name="result">The parsed value; <see langword="null"/> for an empty string on a <c>bool?</c> field.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> parsed; <see langword="false"/> leaves the model untouched.</returns>
    /// <exception cref="InvalidOperationException"><typeparamref name="TValue"/> has no conversion from a string; the message names the component and the type, the converter's own exception inside it.</exception>
    private static bool TryParseValue(string? value, out TValue? result)
    {
        try
        {
            if (typeof(TValue) == typeof(bool))
            {
                if (bool.TryParse(value, out var boolValue))
                {
                    result = (TValue)(object)boolValue;
                    return true;
                }
            }
            else if (typeof(TValue) == typeof(bool?))
            {
                if (string.IsNullOrEmpty(value))
                {
                    result = default;
                    return true;
                }

                if (bool.TryParse(value, out var boolValue))
                {
                    result = (TValue)(object)boolValue;
                    return true;
                }
            }
            else if (BindConverter.TryConvertTo<TValue>(value, CultureInfo.CurrentCulture, out var parsedValue))
            {
                result = parsedValue;
                return true;
            }

            result = default;
            return false;
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(
                $"{FriendlyTypeName.Of(typeof(FormidableInputSelect<TValue>))} does not support the type '{FriendlyTypeName.Of(typeof(TValue))}'.", ex);
        }
    }
}
