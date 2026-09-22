using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace Formidable.Blazor;

/// <summary>
/// Reference validated select input: a plain <c>&lt;select&gt;</c> bound to a field of type
/// <typeparamref name="TValue"/>, wired through <see cref="FormidableInputBase{TValue}"/> for
/// registration, css class, aria output, pending state, and identity — the same extras every kit
/// input gets. Value conversion mirrors Blazor's own <c>InputSelect&lt;TValue&gt;</c> closely:
/// <see cref="string"/>, <see cref="bool"/>, enum, and every other
/// <see cref="Microsoft.AspNetCore.Components.BindConverter"/>-supported <typeparamref name="TValue"/>
/// (numeric and date/time types, and their nullable forms) convert the same way native
/// <c>InputSelect</c> converts them — no broader. A <typeparamref name="TValue"/>
/// <see cref="Microsoft.AspNetCore.Components.BindConverter"/> cannot convert at all throws
/// <see cref="InvalidOperationException"/> the first time a change is committed, exactly as
/// native <c>InputSelect</c> does; a committed option string that fails to parse (should not
/// happen when every <c>&lt;option&gt;</c> was formatted the same way) leaves the model
/// unchanged rather than surfacing a parse error, since Formidable has no native-parse-error
/// channel the way <c>InputBase&lt;TValue&gt;</c> does — FluentValidation is the only source of
/// validation truth here. Multi-select (an array-typed <typeparamref name="TValue"/>) is out of
/// scope; native's array-typed <c>multiple</c> mode is not mirrored. One divergence from native:
/// a null <c>bool?</c> formats as no selection, not the <c>"false"</c> string native formats it
/// as, so a blank <c>&lt;option&gt;</c> can clear a <c>bool?</c> field back to unanswered.
/// </summary>
/// <remarks>
/// <para>
/// Render <c>&lt;option&gt;</c> elements as <see cref="ChildContent"/>. An option's <c>value</c>
/// must be the same string this component would itself format for that
/// <typeparamref name="TValue"/> (plain <see cref="object.ToString"/>, except <see cref="bool"/>/
/// <c>bool?</c> which render as the literal <c>"true"</c>/<c>"false"</c>) for the round trip to
/// land back on the right value.
/// </para>
/// <para>
/// <see cref="FormidableInputBase{TValue}.UpdateOn"/> is honoured, with one coercion: a
/// <c>&lt;select&gt;</c> commits on its <c>change</c> event only — there is no meaningful "input"
/// event distinct from it, the way there is for a text box — so
/// <see cref="InputUpdateMode.OnInput"/> behaves exactly like <see cref="InputUpdateMode.OnChange"/>
/// (the default): both bind <c>onchange</c> and notify the engine the moment a value commits.
/// <see cref="InputUpdateMode.OnBlur"/> still commits the value on <c>change</c>, but the
/// validation notification the commit arms defers to <c>blur</c> instead — a blur with no
/// committed change delivers nothing — the same commit/notify split every
/// other kit input gives that mode. That is the string-projected
/// <see cref="FormidableInputBase{TValue}.AddValueBinding(RenderTreeBuilder, int, string, Func{string, Task{bool}})"/>
/// overload's own contract, honoured here rather than worked around.
/// </para>
/// <para>
/// The same consumer guarantees as <see cref="FormidableInputText"/> apply otherwise: a
/// consumer-splatted <c>class</c> merges with the computed state class, and a consumer-supplied
/// <c>id</c> is ignored in favour of the deterministic <see cref="FormidableFieldId"/>.
/// </para>
/// </remarks>
/// <typeparam name="TValue">The field's value type.</typeparam>
public sealed class FormidableInputSelect<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TValue>
    : FormidableInputBase<TValue>
{
    /// <summary>The <c>&lt;option&gt;</c> elements to render inside the select.</summary>
    [Parameter]
    public RenderFragment? ChildContent { get; set; }

    /// <inheritdoc />
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

    private async Task<bool> TryCommitAsync(string? value)
    {
        if (!TryParseValue(value, out var parsed))
        {
            return false;
        }

        await CommitValueAsync(parsed);
        return true;
    }

    /// <summary>
    /// Formats <paramref name="value"/> the same way native <c>InputSelect&lt;TValue&gt;</c>
    /// does: <see cref="bool"/>/<c>bool?</c> render as the literal strings <c>"true"</c>/
    /// <c>"false"</c> (<see cref="Microsoft.AspNetCore.Components.BindConverter"/> reserves boolean
    /// conversion for conditional HTML attributes, not form values), and every other
    /// <typeparamref name="TValue"/> renders via <see cref="object.ToString"/>.
    /// </summary>
    private static string? FormatValueAsString(TValue? value) =>
        value switch
        {
            bool boolValue => boolValue ? "true" : "false",
            _ => value?.ToString(),
        };

    /// <summary>
    /// Parses a committed option string the same way native <c>InputSelect&lt;TValue&gt;</c>
    /// does: <see cref="bool"/>/<c>bool?</c> special-cased, everything else through
    /// <see cref="Microsoft.AspNetCore.Components.BindConverter.TryConvertTo{T}"/>. Returns
    /// <see langword="false"/> for a string that fails to parse (the caller leaves the model
    /// untouched); throws <see cref="InvalidOperationException"/>, exactly as native does, for a
    /// <typeparamref name="TValue"/> that has no conversion path at all.
    /// </summary>
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
                $"{typeof(FormidableInputSelect<TValue>)} does not support the type '{typeof(TValue)}'.", ex);
        }
    }
}
