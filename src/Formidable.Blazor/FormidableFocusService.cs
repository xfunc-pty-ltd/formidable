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
}
