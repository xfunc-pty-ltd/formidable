using System.Globalization;
using Microsoft.JSInterop;

namespace Formidable.Blazor;

/// <summary>
/// Applies a previously-stored culture at WASM boot. Call this BEFORE
/// <c>WebAssemblyHost.RunAsync()</c>: that is the point at which WebAssembly downloads the
/// satellite resource assemblies for the current culture, so setting it any later leaves the app
/// running on the culture it booted with. Blazor Server hosts set culture through a different
/// mechanism (the request's culture, not a client-stored one) and do not use this class.
/// </summary>
public static class FormidableCultureBootstrap
{
    /// <summary>
    /// Reads <paramref name="storageKey"/> from <c>localStorage</c> via the library's JS module
    /// and applies it as described by
    /// <see cref="ApplyStoredCultureAsync(Func{Task{string?}}, CultureInfo?)"/>. Call this BEFORE
    /// <c>WebAssemblyHost.RunAsync()</c> so satellite resource assemblies download for the right
    /// culture; Blazor Server hosts set culture differently and do not use this overload.
    /// </summary>
    /// <param name="js">The JS runtime used to read <paramref name="storageKey"/> from <c>localStorage</c>.</param>
    /// <param name="storageKey">The <c>localStorage</c> key the culture was stored under.</param>
    /// <param name="fallback">
    /// The culture to apply when nothing usable is stored. <see langword="null"/> leaves the
    /// boot culture untouched in that case.
    /// </param>
    public static async Task ApplyStoredCultureAsync(
        IJSRuntime js, string storageKey = "formidable.culture", CultureInfo? fallback = null) =>
        await ApplyStoredCultureAsync(
            () => new FormidableJsModule(js).InvokeAsync<string?>("getStoredCulture", storageKey).AsTask(),
            fallback);

    /// <summary>
    /// Reads a stored culture name via <paramref name="readStoredCulture"/> and, if it names a
    /// recognized culture, sets both <see cref="CultureInfo.DefaultThreadCurrentCulture"/> and
    /// <see cref="CultureInfo.DefaultThreadCurrentUICulture"/> to it. A missing, blank, or
    /// unrecognized value applies <paramref name="fallback"/> instead, or leaves the boot culture
    /// untouched when <paramref name="fallback"/> is <see langword="null"/>. Call this BEFORE
    /// <c>WebAssemblyHost.RunAsync()</c> so satellite resource assemblies download for the right
    /// culture; Blazor Server hosts set culture differently and do not use this overload.
    /// </summary>
    /// <param name="readStoredCulture">Reads the stored culture name, or <see langword="null"/> if none is stored.</param>
    /// <param name="fallback">
    /// The culture to apply when nothing usable is stored. <see langword="null"/> leaves the
    /// boot culture untouched in that case.
    /// </param>
    public static async Task ApplyStoredCultureAsync(
        Func<Task<string?>> readStoredCulture, CultureInfo? fallback = null)
    {
        var stored = await readStoredCulture();
        CultureInfo? culture = fallback;

        if (!string.IsNullOrWhiteSpace(stored))
        {
            try
            {
                culture = CultureInfo.GetCultureInfo(stored);
            }
            catch (CultureNotFoundException)
            {
                // A corrupted or stale stored value must not stop the app from booting.
                culture = fallback;
            }
        }

        if (culture is null)
        {
            return;
        }

        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
    }
}
