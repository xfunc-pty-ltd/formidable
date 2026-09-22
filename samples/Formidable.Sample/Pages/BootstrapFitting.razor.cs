using Formidable.Blazor;
using Formidable.Sample.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Formidable.Sample.Pages;

public partial class BootstrapFitting : IAsyncDisposable
{
    [Inject] private IJSRuntime Js { get; set; } = default!;

    private readonly QuickContact _contact = new();
    private string _status = string.Empty;

    // The whole UI-library integration: state class NAMES remapped onto Bootstrap's.
    // Pending keeps its default - this validator has no async rules, and the name only
    // matters to whichever stylesheet targets it.
    private readonly FormidableOptions _options = new()
    {
        CssClasses = new FormidableCssClasses { Invalid = "is-invalid", Valid = "is-valid" }
    };

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            await Js.InvokeVoidAsync("formidableSample.watchBsTheme", "bootstrap-demo");
        }
    }

    private void HandleValid() => _status = $"Submitted — thanks, {_contact.Name}!";

    public async ValueTask DisposeAsync()
    {
        await Js.InvokeVoidAsync("formidableSample.unwatchBsTheme", "bootstrap-demo");
    }
}
