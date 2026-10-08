using Formidable.Blazor;
using Formidable.Sample.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Formidable.Sample.Pages;

public partial class BootstrapFitting : IAsyncDisposable
{
    private const string BootstrapStylesheet = "lib/bootstrap.min.css";

    [Inject] private IJSRuntime Js { get; set; } = default!;

    private readonly QuickContact _contact = new();
    private string _status = string.Empty;

    // The whole UI-library integration: state class NAMES remapped onto Bootstrap's.
    // Warning and Info keep their defaults - Bootstrap has no advisory tier to remap onto and
    // this validator never raises either severity, so there is nothing for them to style.
    // Pending also keeps its default - this validator has no async rules, and the name only
    // matters to whichever stylesheet targets it.
    private readonly FormidableOptions _options = new()
    {
        CssClasses = new FormidableCssClasses { Invalid = "is-invalid", Valid = "is-valid" }
    };

    private Func<Task>? _nextStep;
    private bool _stylesheetLoaded;
    private bool _failed;

    // Bootstrap's stylesheet comes and goes with the page: it is added here and taken out in
    // DisposeAsync. It applies once it has downloaded, and the form waits for it, so the form
    // never draws in the sample's style and then jumps into Bootstrap's. Each step runs after the
    // render the step before it asked for, and a page that has gone renders no more, so a
    // visitor who leaves mid-way ends the steps there.
    protected override void OnInitialized() => _nextStep = LoadStylesheetAsync;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_nextStep is { } step)
        {
            _nextStep = null;
            await step();
        }
    }

    // A download that fails rejects the load, and the page says so and offers Retry. sample.js
    // takes a failed link out again, so Retry adds a fresh one.
    private async Task LoadStylesheetAsync()
    {
        try
        {
            await Js.InvokeVoidAsync("formidableSample.loadStylesheet", BootstrapStylesheet);
            _stylesheetLoaded = true;
            _nextStep = WatchColourSchemeAsync;
        }
        catch (JSException)
        {
            _failed = true;
        }

        StateHasChanged();
    }

    // The box around the form exists once the form shows, so it takes the scheme from then on.
    private async Task WatchColourSchemeAsync() =>
        await Js.InvokeVoidAsync("formidableSample.watchBsTheme", "bootstrap-demo");

    private void Retry()
    {
        _failed = false;
        _nextStep = LoadStylesheetAsync;
    }

    private void HandleValid() => _status = $"Submitted — thanks, {_contact.Name}!";

    public async ValueTask DisposeAsync()
    {
        await Js.InvokeVoidAsync("formidableSample.unloadStylesheet", BootstrapStylesheet);
        await Js.InvokeVoidAsync("formidableSample.unwatchBsTheme", "bootstrap-demo");
    }
}
