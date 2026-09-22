using System.Globalization;

namespace Formidable.Sample.Shared;

// A WebAssembly app downloads its satellite resource assemblies for whatever culture is current
// when RunAsync() is called, and never again, so a stored language choice has to be applied before
// that line rather than from a page afterwards. This lives in the sample rather than in the
// library because every part of it is the app's decision: where the choice is kept, under what
// key, and what a stale one should do.
public static class CultureBootstrap
{
    // The stored name arrives through a delegate rather than a storage call of its own, so the app
    // keeps ownership of where the choice lives (localStorage here, a cookie or a fetched user
    // profile elsewhere) and this stays a parse with a fallback and nothing to stand in for.
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
