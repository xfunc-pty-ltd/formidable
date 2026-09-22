using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;

namespace Formidable.Blazor;

/// <summary>JS-module-backed focus/scroll for fields, addressed by <see cref="FormidableFieldId"/>.</summary>
internal sealed class FormidableFocusService : IFormidableFocusService, IAsyncDisposable
{
    private readonly IJSRuntime _jsRuntime;

    // Cache the import operation itself, not just its result: the assignment below runs
    // synchronously (no await before it), so concurrent first callers all observe the same
    // in-flight task instead of each triggering their own "import" call and racing to
    // overwrite (and leak) a previously-imported module reference. Only a SUCCESSFUL import stays
    // cached - see the catch in FocusAsync.
    private Task<IJSObjectReference>? _moduleTask;

    public FormidableFocusService(IJSRuntime jsRuntime) => _jsRuntime = jsRuntime;

    public async ValueTask<bool> FocusAsync(FieldIdentifier field)
    {
        var moduleTask = _moduleTask ??= _jsRuntime.InvokeAsync<IJSObjectReference>(
            "import", "./_content/Formidable.Blazor/formidable.js").AsTask();

        IJSObjectReference module;
        try
        {
            module = await moduleTask;
        }
        catch
        {
            // Caching a faulted import would disable focus for the rest of the circuit after a
            // single transient failure, so drop it and let the next call re-import. Guarded on
            // reference equality: a concurrent caller may already have installed a replacement,
            // and clearing that would cost the import it is sharing.
            if (ReferenceEquals(_moduleTask, moduleTask))
            {
                _moduleTask = null;
            }

            throw;
        }

        return await module.InvokeAsync<bool>("focusField", FormidableFieldId.For(field));
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
}
