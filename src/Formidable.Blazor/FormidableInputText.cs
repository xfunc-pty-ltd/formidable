using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace Formidable.Blazor;

/// <summary>
/// Reference validated text input: a plain <c>&lt;input&gt;</c> bound to a <see cref="string"/>
/// field, wired through <see cref="ValidatedInputBase{TValue}"/> for registration, css class, and
/// aria output. A working example for wrapper authors of how little markup the base class leaves
/// to write — including the attribute ordering: unmatched attributes are splatted first so every
/// value the component computes below wins the duplicate-attribute race (Blazor applies
/// last-write-wins).
/// </summary>
/// <remarks>
/// A consumer-supplied <c>id</c> is ignored: the rendered id is always the deterministic
/// <see cref="FormidableFieldId"/> for the bound field, because the message list's
/// <c>aria-describedby</c> target and <see cref="IFormidableFocusService"/> both address the field
/// by it. A consumer-supplied <c>class</c> is honoured — it is merged with the computed state class
/// rather than replaced (see <see cref="ValidatedInputBase{TValue}.CssClass"/>).
/// </remarks>
public sealed class FormidableInputText : ValidatedInputBase<string?>
{
    /// <inheritdoc />
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "input");
        builder.AddMultipleAttributes(1, AdditionalAttributes!);
        builder.AddAttribute(2, "id", ElementId);
        builder.AddAttribute(3, "class", CssClass);
        builder.AddMultipleAttributes(4, AriaAttributes!);
        builder.AddAttribute(5, "value", Value);
        builder.AddAttribute(
            6,
            UpdateOn == InputUpdateMode.OnInput ? "oninput" : "onchange",
            EventCallback.Factory.CreateBinder<string?>(this, v => SetCurrentValueAsync(v), Value));
        builder.CloseElement();
    }
}
