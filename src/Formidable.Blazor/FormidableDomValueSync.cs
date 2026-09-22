using Microsoft.JSInterop;

namespace Formidable.Blazor;

/// <summary>The JS-backed <see cref="IFormidableDomValueSync"/>: hands the element id and value to formidable.js, which writes the element's <c>value</c> property and does nothing for a missing element.</summary>
// Goes through the script because a render-tree diff produces no write here: when the rendered
// value and the value the browser reported already agree, the renderer has nothing to patch.
internal sealed class FormidableDomValueSync : FormidableJsBackedService, IFormidableDomValueSync
{
    public FormidableDomValueSync(IJSRuntime jsRuntime) : base(jsRuntime)
    {
    }

    public ValueTask SyncValueAsync(string elementId, string? value) =>
        Module.InvokeVoidAsync("syncValue", elementId, value);
}
