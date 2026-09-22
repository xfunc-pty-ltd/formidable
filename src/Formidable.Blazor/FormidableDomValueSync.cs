using Microsoft.JSInterop;

namespace Formidable.Blazor;

/// <summary>
/// JS-module-backed implementation of <see cref="IFormidableDomValueSync"/>: forwards the id and
/// value to formidable.js, which writes the element's <c>value</c> property directly — the write
/// no render-tree diff can produce when the rendered value and the browser-reported value
/// already agree.
/// </summary>
internal sealed class FormidableDomValueSync : FormidableJsBackedService, IFormidableDomValueSync
{
    public FormidableDomValueSync(IJSRuntime jsRuntime) : base(jsRuntime)
    {
    }

    public ValueTask SyncValueAsync(string elementId, string? value) =>
        Module.InvokeVoidAsync("syncValue", elementId, value);
}
