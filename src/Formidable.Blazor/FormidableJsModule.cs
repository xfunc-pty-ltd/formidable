using System.Diagnostics.CodeAnalysis;
using Microsoft.JSInterop;

namespace Formidable.Blazor;

/// <summary>
/// Loader for the library's JS module (formidable.js): imports it on first use, caches only a
/// successful import, and owns both disposal shapes. Each JS-backed service holds its own
/// instance rather than sharing one, so disposal ownership stays with the service the container
/// disposes.
/// </summary>
internal sealed class FormidableJsModule
{
    private readonly IJSRuntime _jsRuntime;

    // Cache the import operation itself, not just its result: the assignment below runs
    // synchronously (no await before it), so concurrent first callers all observe the same
    // in-flight task instead of each triggering their own "import" call and racing to
    // overwrite (and leak) a previously-imported module reference. Only a SUCCESSFUL import stays
    // cached - see the catch in ImportAsync.
    private Task<IJSObjectReference>? _moduleTask;

    public FormidableJsModule(IJSRuntime jsRuntime) => _jsRuntime = jsRuntime;

    /// <summary>Invokes <paramref name="identifier"/> on the module and returns its result.</summary>
    public async ValueTask<T> InvokeAsync<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicConstructors |
            DynamicallyAccessedMemberTypes.PublicFields |
            DynamicallyAccessedMemberTypes.PublicProperties)] T>(string identifier, params object?[] args)
    {
        var module = await ImportAsync();
        return await module.InvokeAsync<T>(identifier, args);
    }

    /// <summary>Invokes <paramref name="identifier"/> on the module, discarding any result.</summary>
    public async ValueTask InvokeVoidAsync(string identifier, params object?[] args)
    {
        var module = await ImportAsync();
        await module.InvokeVoidAsync(identifier, args);
    }

    private async Task<IJSObjectReference> ImportAsync()
    {
        var moduleTask = _moduleTask ??= _jsRuntime.InvokeAsync<IJSObjectReference>(
            "import", "./_content/Formidable.Blazor/formidable.js").AsTask();

        try
        {
            return await moduleTask;
        }
        catch
        {
            // Caching a faulted import would disable the module for the rest of the circuit after
            // a single transient failure, so drop it and let the next call re-import. Guarded on
            // reference equality: a concurrent caller may already have installed a replacement,
            // and clearing that would cost the import it is sharing.
            if (ReferenceEquals(_moduleTask, moduleTask))
            {
                _moduleTask = null;
            }

            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_moduleTask is null)
        {
            return;
        }

        try
        {
            var module = await _moduleTask;
            await module.DisposeAsync();
        }
        catch (JSDisconnectedException)
        {
            // Circuit already gone - nothing to release, whether the import itself
            // never completed or the module was already torn down with the circuit.
        }
    }

    // Offering a synchronous Dispose alongside DisposeAsync is the MEDI-recommended shape for a
    // resource whose owning container may be disposed either way: a container/scope disposed
    // synchronously calls this overload, and an owner only implementing IAsyncDisposable throws
    // ("... type only implements IAsyncDisposable. Use DisposeAsync to dispose the container.")
    // instead - which broke a consumer's own bUnit teardown, since bUnit's container disposes
    // synchronously by default. This path is deliberately best-effort: the JS module dies with
    // the circuit regardless of whether anything here releases it, so there is no correctness
    // reason to block. Only a module import that already finished is anything to act on; only a
    // module dispose that completes without actually crossing the interop boundary (e.g. a
    // synchronous test double) is observed. Anything still in flight, or the import having never
    // completed or having faulted, is dropped rather than awaited or thrown from a method that
    // cannot itself be asynchronous.
    public void Dispose()
    {
        if (_moduleTask is not { IsCompletedSuccessfully: true } moduleTask)
        {
            return;
        }

        try
        {
            var disposeTask = moduleTask.Result.DisposeAsync();
            if (!disposeTask.IsCompletedSuccessfully)
            {
                return; // needs a real await; nothing a synchronous Dispose can safely do
            }
        }
        catch (JSDisconnectedException)
        {
            // Circuit already gone - nothing to release.
        }
    }
}
