using Microsoft.AspNetCore.Components.Rendering;

namespace Formidable.Blazor;

/// <summary>
/// Reference validated multiline text input: a plain <c>&lt;textarea&gt;</c> bound to a
/// <see cref="string"/> field, wired through <see cref="FormidableInputBase{TValue}"/> exactly
/// like <see cref="FormidableInputText"/> — registration, css class, aria output, pending state,
/// and identity all apply unchanged. Mirrors native <c>InputTextArea</c>'s markup and value
/// semantics (a plain string, no parsing); the only difference from <see cref="FormidableInputText"/>
/// is the element tag.
/// </summary>
/// <remarks>
/// The same consumer guarantees as <see cref="FormidableInputText"/> apply: a consumer-splatted
/// <c>class</c> merges with the computed state class, and a consumer-supplied <c>id</c> is
/// ignored in favour of the deterministic <see cref="FormidableFieldId"/>. <see cref="FormidableInputBase{TValue}.UpdateOn"/>
/// applies exactly as it does for <see cref="FormidableInputText"/>: <c>OnChange</c> (default)
/// commits on the element's <c>change</c> event, <c>OnInput</c> commits on every keystroke, and
/// <c>OnBlur</c> commits on <c>change</c> while deferring the engine notification to <c>blur</c>.
/// </remarks>
public sealed class FormidableInputTextArea : FormidableInputBase<string?>
{
    /// <inheritdoc />
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "textarea");
        builder.AddMultipleAttributes(1, AdditionalAttributes!);
        builder.AddAttribute(2, "id", ElementId);
        builder.AddAttribute(3, "class", CssClass);
        builder.AddMultipleAttributes(4, AriaAttributes!);
        builder.AddAttribute(5, "value", Value);
        AddValueBinding(builder, 6);
        builder.CloseElement();
    }
}
