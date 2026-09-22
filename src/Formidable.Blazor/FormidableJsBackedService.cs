using Microsoft.JSInterop;

namespace Formidable.Blazor;

/// <summary>The base of the three JS-backed services: it holds the <see cref="FormidableJsModule"/> and releases it under either disposal shape.</summary>
internal abstract class FormidableJsBackedService : IDisposable, IAsyncDisposable
{
    private protected FormidableJsModule Module { get; }

    protected FormidableJsBackedService(IJSRuntime jsRuntime) => Module = new FormidableJsModule(jsRuntime);

    public ValueTask DisposeAsync() => Module.DisposeAsync();

    public void Dispose() => Module.Dispose();
}
