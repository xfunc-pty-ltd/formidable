using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;

namespace Formidable.Blazor;

/// <summary>JS-module-backed focus/scroll for fields, addressed by <see cref="FormidableFieldId"/>.</summary>
internal sealed class FormidableFocusService : IFormidableFocusService, IDisposable, IAsyncDisposable
{
    private readonly FormidableJsModule _module;

    public FormidableFocusService(IJSRuntime jsRuntime) => _module = new FormidableJsModule(jsRuntime);

    public ValueTask<bool> FocusAsync(FieldIdentifier field) =>
        _module.InvokeAsync<bool>("focusField", FormidableFieldId.For(field));

    // Both disposal shapes so either kind of container teardown releases the module - the
    // rationale and the semantics live with FormidableJsModule.
    public ValueTask DisposeAsync() => _module.DisposeAsync();

    public void Dispose() => _module.Dispose();
}
