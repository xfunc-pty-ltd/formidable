using Microsoft.JSInterop;

namespace Formidable.Blazor;

/// <summary>
/// JS-module-backed implementation of <see cref="IFormidableDomValueSync"/>: forwards the id and
/// value to formidable.js, which writes the element's <c>value</c> property directly — the write
/// no render-tree diff can produce when the rendered value and the browser-reported value
/// already agree.
/// </summary>
internal sealed class FormidableDomValueSync : IFormidableDomValueSync, IDisposable, IAsyncDisposable
{
    private readonly FormidableJsModule _module;

    public FormidableDomValueSync(IJSRuntime jsRuntime) => _module = new FormidableJsModule(jsRuntime);

    public ValueTask SyncValueAsync(string elementId, string? value) =>
        _module.InvokeVoidAsync("syncValue", elementId, value);

    // Both disposal shapes so either kind of container teardown releases the module - the
    // rationale and the semantics live with FormidableJsModule.
    public ValueTask DisposeAsync() => _module.DisposeAsync();

    public void Dispose() => _module.Dispose();
}
