using Microsoft.AspNetCore.Components.Rendering;

namespace Formidable.Blazor;

/// <summary>
/// Reference validated text input: a plain <c>&lt;input&gt;</c> bound to a <see cref="string"/>
/// field, wired through <see cref="FormidableInputBase{TValue}"/> for registration, ids, css
/// class, aria output, and value binding — the five extras the base class provides. A working
/// example for wrapper authors of how little markup the base class leaves to write: the element,
/// its value, and the base's two calls. The attribute ordering the kit's guarantees rest on —
/// unmatched attributes splatted first, computed values after, so those win the
/// duplicate-attribute race (Blazor applies last-write-wins) — belongs to
/// <see cref="FormidableInputBase{TValue}.AddCommonAttributes"/> rather than to the frames a
/// control writes out for itself when it needs them somewhere that call cannot put them.
/// </summary>
/// <remarks>
/// A consumer-supplied <c>id</c> is ignored: the rendered id is always the deterministic
/// <see cref="FormidableFieldId"/> for the bound field, because the message list's
/// <c>aria-describedby</c> target and <see cref="IFormidableFocusService"/> both address the field
/// by it. A consumer-supplied <c>class</c> is honoured — it is merged with the computed state class
/// rather than replaced (see <see cref="FormidableInputBase{TValue}.CssClass"/>) — and so is a
/// consumer-supplied <c>aria-describedby</c>: while the field has issues, the computed messages
/// id is appended after the splatted ids rather than replacing them.
/// </remarks>
public sealed class FormidableInputText : FormidableInputBase<string?>
{
    /// <inheritdoc />
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "input");
        AddCommonAttributes(builder, 1);
        builder.AddAttribute(5, "value", Value);
        AddValueBinding(builder, 6);
        builder.CloseElement();
    }
}
