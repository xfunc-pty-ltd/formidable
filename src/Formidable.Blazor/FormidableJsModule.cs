using System.Diagnostics.CodeAnalysis;
using Microsoft.JSInterop;

namespace Formidable.Blazor;

/// <summary>Loads the library's script module (formidable.js) on the first invocation, keeps only a successful import (a prerender's failed one is retried on the next call), and releases it under either disposal shape.</summary>
// Each JS-backed service and each root holds its own instance rather than sharing one, so
// disposal ownership stays with whoever the container or the renderer disposes.
internal sealed class FormidableJsModule
{
    private readonly IJSRuntime _jsRuntime;

    // Cache the import operation itself, not just its result: the assignment below runs
    // synchronously (no await before it), so concurrent first callers all observe the same
    // in-flight task instead of each triggering their own "import" call and racing to
    // overwrite (and leak) a previously-imported module reference. Only a SUCCESSFUL import stays
    // cached - see the catch in ImportAsync.
    private Task<IJSObjectReference>? _moduleTask;

    /// <summary>Creates a loader over <paramref name="jsRuntime"/>; nothing is imported until the first invocation.</summary>
    /// <param name="jsRuntime">The runtime the import and every invocation go through.</param>
    public FormidableJsModule(IJSRuntime jsRuntime) => _jsRuntime = jsRuntime;

    /// <summary>Whether an exception is the interop boundary failing (the script throwing, the runtime disconnected, disposed or cancelled) rather than the code behind it.</summary>
    /// <param name="exception">The exception to judge.</param>
    /// <returns><see langword="true"/> for <see cref="JSException"/>, <see cref="JSDisconnectedException"/>, <see cref="ObjectDisposedException"/> and <see cref="OperationCanceledException"/>; an interop timeout on a server circuit arrives as the last.</returns>
    // Stated once, because every caller that catches on it draws the same line: a boundary that
    // is gone, disconnected or never loaded costs the page whatever that call would have bought
    // and nothing else, where an implementation failing on its own terms is a bug for someone to
    // see rather than one to swallow.
    internal static bool IsInteropFailure(Exception exception) =>
        exception is JSException or JSDisconnectedException or ObjectDisposedException
            or OperationCanceledException;

    /// <summary>Invokes the named export on the module and returns its result.</summary>
    /// <typeparam name="T">The type the result deserializes to.</typeparam>
    /// <param name="identifier">The export's name.</param>
    /// <param name="args">The arguments to pass.</param>
    /// <returns>The export's answer.</returns>
    public async ValueTask<T> InvokeAsync<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicConstructors |
            DynamicallyAccessedMemberTypes.PublicFields |
            DynamicallyAccessedMemberTypes.PublicProperties)] T>(string identifier, params object?[] args)
    {
        var module = await ImportAsync();
        return await module.InvokeAsync<T>(identifier, args);
    }

    /// <summary>Invokes the named export on the module, discarding any result.</summary>
    /// <param name="identifier">The export's name.</param>
    /// <param name="args">The arguments to pass.</param>
    /// <returns>A task that completes when the export has returned.</returns>
    public async ValueTask InvokeVoidAsync(string identifier, params object?[] args)
    {
        var module = await ImportAsync();
        await module.InvokeVoidAsync(identifier, args);
    }

    /// <summary>Imports the module once, sharing the in-flight import with concurrent callers and forgetting a failed one so the next call re-imports.</summary>
    /// <returns>The imported module.</returns>
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

    /// <summary>Releases a successfully imported module, swallowing an interop failure; an import still in flight or already failed is left alone.</summary>
    /// <returns>A task that completes when the release has answered, or at once when there is nothing to release.</returns>
    // An import still in flight is skipped rather than awaited: it may never complete, and
    // awaiting it here would risk hanging whatever disposed this instance (container teardown,
    // circuit teardown on Blazor Server) on an interop call that answers after nothing is
    // listening. A faulted import already dropped itself from the cache (see ImportAsync) and
    // has nothing loaded to release, so neither case throws or blocks.
    public async ValueTask DisposeAsync()
    {
        if (_moduleTask is not { IsCompletedSuccessfully: true } moduleTask)
        {
            return;
        }

        try
        {
            await moduleTask.Result.DisposeAsync();
        }
        catch (Exception exception) when (IsInteropFailure(exception))
        {
            // Circuit already gone - nothing to release.
        }
    }

    // Offering a synchronous Dispose alongside DisposeAsync is the MEDI-recommended shape for a
    // resource whose owning container may be disposed either way: a container/scope disposed
    // synchronously calls this overload, and an owner only implementing IAsyncDisposable throws
    // ("... type only implements IAsyncDisposable. Use DisposeAsync to dispose the container.")
    // instead - which broke a consumer's own bUnit teardown, since bUnit's container disposes
    // synchronously by default. This path is deliberately best-effort: the JS module dies with
    // the circuit regardless of whether anything here releases it, so there is no correctness
    // reason to block. Only a module import that already finished is anything to act on, and the
    // dispose call is fired and discarded rather than awaited: a synchronous method has no way to
    // wait on it, so whether the release itself completes, faults or never finishes is never
    // observed here on any path. The discard is also why the catch below is narrower than
    // DisposeAsync's IsInteropFailure - nothing here inspects the task afterward, so the only
    // exception this method can ever see is one the call raises before it returns a task at all,
    // and JSDisconnectedException, a circuit already known gone, is the case this catch is kept
    // for.
    public void Dispose()
    {
        if (_moduleTask is not { IsCompletedSuccessfully: true } moduleTask)
        {
            return;
        }

        try
        {
            _ = moduleTask.Result.DisposeAsync();
        }
        catch (JSDisconnectedException)
        {
            // Circuit already gone - nothing to release.
        }
    }
}
