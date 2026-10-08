using Formidable.Sample.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor;

namespace Formidable.Sample.Pages;

public partial class MudBlazorFitting : IAsyncDisposable
{
    // MudBlazor's files are loaded here rather than in index.html, so a visitor who never opens
    // this page never downloads them. MudBlazor's components call into the script from their
    // first render, and they draw at no sensible size until the stylesheet applies, so the form
    // waits for both. An app built on MudBlazor references both files in its host page instead
    // and renders its components straight away.
    private const string MudBlazorScript = "_content/MudBlazor/MudBlazor.min.js";
    private const string MudBlazorStylesheet = "_content/MudBlazor/MudBlazor.min.css";

    [Inject] private IJSRuntime Js { get; set; } = default!;

    private readonly StudioBooking _booking = new();
    private MudThemeProvider? _theme;
    private Func<Task>? _nextStep;
    private bool _scriptLoaded;
    private bool _stylesheetLoaded;
    private bool _failed;
    private bool _isDarkMode;
    private string _status = string.Empty;

    // Loading runs in steps, each after the render the step before it asked for: the script, then
    // the visitor's colour scheme (read through the theme provider the script lets the page
    // draw), then the stylesheet. The theme provider is in place before the stylesheet applies,
    // so the page's text never shows without MudBlazor's font. A page that has gone renders no
    // more, so a visitor who leaves mid-way ends the steps there.
    protected override void OnInitialized() => _nextStep = LoadScriptAsync;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_nextStep is { } step)
        {
            _nextStep = null;
            await step();
        }
    }

    // A download that fails (a dropped connection, a blocked request) rejects the load. The page
    // then says so and offers Retry, where the placeholder would otherwise stand for the rest of
    // the visit. sample.js forgets a failed script, so the next call asks for it again.
    private async Task LoadScriptAsync()
    {
        try
        {
            await Js.InvokeVoidAsync("formidableSample.loadScript", MudBlazorScript);
            _scriptLoaded = true;
            _nextStep = ReadColourSchemeAsync;
        }
        catch (JSException)
        {
            _failed = true;
        }

        StateHasChanged();
    }

    // The scheme is read once, before the stylesheet goes in, so MudBlazor's look arrives in the
    // visitor's scheme. The theme provider follows any later change through @bind-IsDarkMode.
    private async Task ReadColourSchemeAsync()
    {
        _isDarkMode = await _theme!.GetSystemDarkModeAsync();
        _nextStep = LoadStylesheetAsync;
        StateHasChanged();
    }

    // The stylesheet comes and goes with the page: it is added here and taken out in
    // DisposeAsync, so another page keeps the sample's own look. sample.js takes a failed link
    // out again, so Retry adds a fresh one.
    private async Task LoadStylesheetAsync()
    {
        try
        {
            await Js.InvokeVoidAsync("formidableSample.loadStylesheet", MudBlazorStylesheet);
            _stylesheetLoaded = true;
        }
        catch (JSException)
        {
            _failed = true;
        }

        StateHasChanged();
    }

    // Retry runs the step that failed again, after the render the click causes.
    private void Retry()
    {
        _failed = false;
        _nextStep = _scriptLoaded ? LoadStylesheetAsync : LoadScriptAsync;
    }

    private void HandleValid() => _status = $"Booked {_booking.Room} for {_booking.BandName}.";

    public async ValueTask DisposeAsync()
    {
        await Js.InvokeVoidAsync("formidableSample.unloadStylesheet", MudBlazorStylesheet);
    }
}
