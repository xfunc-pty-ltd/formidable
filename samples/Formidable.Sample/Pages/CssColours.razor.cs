using Formidable.Sample.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Formidable.Sample.Pages;

public partial class CssColours
{
    [Inject] private IJSRuntime Js { get; set; } = default!;

    // The swatches start at the light theme's tokens in app.css, then take the values the
    // visitor's colour scheme gives the form once the page has rendered. The wrapper writes a
    // token only once its picker has been used, so every other token keeps the theme's own
    // light or dark value.
    private string _error = "#b91c1c";
    private string _accent = "#0774b8";
    private string _warn = "#a16207";
    private string _info = "#516fca";
    private string _valid = "#15803d";

    // Each picker's own pick, null until that picker is used.
    private string? _errorPick;
    private string? _accentPick;
    private string? _warnPick;
    private string? _infoPick;
    private string? _validPick;

    private ElementReference _wrapper;

    private string Error
    {
        get => _errorPick ?? _error;
        set => _errorPick = value;
    }

    private string Accent
    {
        get => _accentPick ?? _accent;
        set => _accentPick = value;
    }

    private string Warn
    {
        get => _warnPick ?? _warn;
        set => _warnPick = value;
    }

    private string Info
    {
        get => _infoPick ?? _info;
        set => _infoPick = value;
    }

    private string Valid
    {
        get => _validPick ?? _valid;
        set => _validPick = value;
    }

    private string? WrapperStyle
    {
        get
        {
            var style = (_errorPick is null ? "" : $"--error: {_errorPick}; --error-text: {_errorPick}; ")
                + (_accentPick is null ? "" : $"--accent: {_accentPick}; --ring: {_accentPick}26; ")
                + (_warnPick is null ? "" : $"--warn: {_warnPick}; ")
                + (_infoPick is null ? "" : $"--info: {_infoPick}; ")
                + (_validPick is null ? "" : $"--valid: {_validPick}; ");
            return style.Length == 0 ? null : style;
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            var theme = await Js.InvokeAsync<string[]>(
                "formidableSample.readTokens", _wrapper, new[] { "--error", "--accent", "--warn", "--info", "--valid" });
            (_error, _accent, _warn, _info, _valid) = (theme[0], theme[1], theme[2], theme[3], theme[4]);
            StateHasChanged();
        }
    }

    private readonly Listing _listing = new();
    private string _status = string.Empty;

    private void HandleValid() => _status = $"Submitted — \"{_listing.Title}\" saved with this palette.";
}
