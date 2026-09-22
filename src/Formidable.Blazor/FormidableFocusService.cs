using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;

namespace Formidable.Blazor;

/// <summary>JS-module-backed focus/scroll for fields, addressed by <see cref="FormidableFieldId"/>.</summary>
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
