using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;

namespace Formidable.Blazor;

/// <summary>The JS-backed <see cref="IFormidableFocusService"/>: formidable.js scrolls the field's message list (or the element itself) into view, focuses the element carrying the field's id, and reports whether it took focus.</summary>
internal sealed class FormidableFocusService : FormidableJsBackedService, IFormidableFocusService
{
    public FormidableFocusService(IJSRuntime jsRuntime) : base(jsRuntime)
    {
    }

    public ValueTask<bool> FocusAsync(FieldIdentifier field) =>
        Module.InvokeAsync<bool>(
            "focusField",
            FormidableFieldId.For(field),
            FormidableFieldId.MessagesFor(field));

    /// <summary>Focuses the field's element as <see cref="FocusAsync"/> does, unless the latest first-attempt miss was this field's and, since it, the visitor pressed a key or a pointer button and focus sits on another control.</summary>
    /// <param name="field">The field whose first attempt missed.</param>
    /// <returns><see langword="true"/> when the element took focus; <see langword="false"/> when it did not, or when focus stays where the visitor moved it.</returns>
    // The second attempt a focus move makes after a FocusFallback answers true, told apart from a
    // first attempt so focus the visitor moved meanwhile stays put. Not on IFormidableFocusService:
    // the move reaches it through a type test on this class, so a consumer's own service, or one
    // that wraps this one, implements FocusAsync alone and gets a plain second FocusAsync.
    public ValueTask<bool> RetryFocusAsync(FieldIdentifier field) =>
        Module.InvokeAsync<bool>(
            "focusField",
            FormidableFieldId.For(field),
            FormidableFieldId.MessagesFor(field),
            true);
}
