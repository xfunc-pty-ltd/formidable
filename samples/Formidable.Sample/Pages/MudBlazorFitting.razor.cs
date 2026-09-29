using Formidable.Sample.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor;

namespace Formidable.Sample.Pages;

public partial class MudBlazorFitting
{
    // MudBlazor's script is loaded here rather than in index.html, so a visitor who never opens
    // this page never downloads it. MudBlazor's components call into the script from their first
    // render, so the page renders them once the script has run. An app built on MudBlazor
    // references the script in its host page instead and renders them straight away.
    private const string MudBlazorScript = "_content/MudBlazor/MudBlazor.min.js";

    [Inject] private IJSRuntime Js { get; set; } = default!;

    private readonly StudioBooking _booking = new();
    private MudThemeProvider? _theme;
    private bool _scriptLoaded;
    private bool _colourSchemeRead;
    private bool _isDarkMode;
    private string _status = string.Empty;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            await Js.InvokeVoidAsync("formidableSample.loadScript", MudBlazorScript);
            _scriptLoaded = true;
            StateHasChanged();
        }
        else if (_theme is not null && !_colourSchemeRead)
        {
            // The OS colour scheme is read once here; the theme provider follows any later change
            // through @bind-IsDarkMode.
            _colourSchemeRead = true;
            _isDarkMode = await _theme.GetSystemDarkModeAsync();
            StateHasChanged();
        }
    }

    private void HandleValid() => _status = $"Booked {_booking.Room} for {_booking.BandName}.";
}
