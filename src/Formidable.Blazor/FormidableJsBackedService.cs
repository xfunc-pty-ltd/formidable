using Microsoft.JSInterop;

namespace Formidable.Blazor;

/// <summary>
/// Common base for a service backed by <see cref="FormidableJsModule"/>: holds the module and
/// offers both disposal shapes so either kind of container teardown releases it — the rationale
/// and the semantics live with <see cref="FormidableJsModule"/> itself.
/// </summary>
internal abstract class FormidableJsBackedService : IDisposable, IAsyncDisposable
{
    private protected FormidableJsModule Module { get; }

    protected FormidableJsBackedService(IJSRuntime jsRuntime) => Module = new FormidableJsModule(jsRuntime);

    public ValueTask DisposeAsync() => Module.DisposeAsync();

    public void Dispose() => Module.Dispose();
}
