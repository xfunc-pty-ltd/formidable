using Formidable.Sample.Shared;

namespace Formidable.Sample.Pages;

public partial class CssColours
{
    // Defaults match the light theme's tokens in app.css, but the wrapper emits no inline
    // style until a colour is actually picked (see _touched): it stays inert until then, so
    // the theme's own light/dark tokens show through instead of these light defaults
    // overriding dark mode from first render.
    private string _error = "#b91c1c";
    private string _accent = "#0774b8";
    private string _warn = "#a16207";
    private string _info = "#516fca";
    private bool _touched;

    private string Error
    {
        get => _error;
        set { _error = value; _touched = true; }
    }

    private string Accent
    {
        get => _accent;
        set { _accent = value; _touched = true; }
    }

    private string Warn
    {
        get => _warn;
        set { _warn = value; _touched = true; }
    }

    private string Info
    {
        get => _info;
        set { _info = value; _touched = true; }
    }

    private string? WrapperStyle => _touched
        ? $"--error: {_error}; --error-text: {_error}; --accent: {_accent}; --ring: {_accent}26; --warn: {_warn}; --info: {_info};"
        : null;

    private readonly Listing _listing = new();
    private string _status = string.Empty;

    private void HandleValid() => _status = $"Submitted — \"{_listing.Title}\" saved with this palette.";
}
