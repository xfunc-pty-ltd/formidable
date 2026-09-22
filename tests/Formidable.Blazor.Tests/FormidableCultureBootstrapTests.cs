using System.Globalization;

namespace Formidable.Blazor.Tests;

// CultureInfo.DefaultThreadCurrentCulture/UICulture are process-wide statics, so every test here
// saves and restores both in a finally block regardless of outcome. xunit runs the [Fact]s within
// this single class sequentially (only cross-class parallelism needs a shared [Collection]), so no
// other coordination is needed to keep these mutations from interleaving with one another.
public class FormidableCultureBootstrapTests
{
    [Fact]
    public async Task Valid_stored_culture_sets_both_default_thread_cultures()
    {
        var originalCulture = CultureInfo.DefaultThreadCurrentCulture;
        var originalUiCulture = CultureInfo.DefaultThreadCurrentUICulture;

        try
        {
            await FormidableCultureBootstrap.ApplyStoredCultureAsync(() => Task.FromResult<string?>("de-DE"));

            Assert.Equal("de-DE", CultureInfo.DefaultThreadCurrentCulture?.Name);
            Assert.Equal("de-DE", CultureInfo.DefaultThreadCurrentUICulture?.Name);
        }
        finally
        {
            CultureInfo.DefaultThreadCurrentCulture = originalCulture;
            CultureInfo.DefaultThreadCurrentUICulture = originalUiCulture;
        }
    }

    [Fact]
    public async Task Unrecognized_stored_culture_falls_back_to_the_supplied_fallback()
    {
        var originalCulture = CultureInfo.DefaultThreadCurrentCulture;
        var originalUiCulture = CultureInfo.DefaultThreadCurrentUICulture;

        try
        {
            await FormidableCultureBootstrap.ApplyStoredCultureAsync(
                () => Task.FromResult<string?>("zz-notaculture"),
                fallback: CultureInfo.GetCultureInfo("en-AU"));

            Assert.Equal("en-AU", CultureInfo.DefaultThreadCurrentCulture?.Name);
            Assert.Equal("en-AU", CultureInfo.DefaultThreadCurrentUICulture?.Name);
        }
        finally
        {
            CultureInfo.DefaultThreadCurrentCulture = originalCulture;
            CultureInfo.DefaultThreadCurrentUICulture = originalUiCulture;
        }
    }

    [Fact]
    public async Task No_stored_culture_and_no_fallback_leaves_the_boot_culture_untouched()
    {
        var originalCulture = CultureInfo.DefaultThreadCurrentCulture;
        var originalUiCulture = CultureInfo.DefaultThreadCurrentUICulture;

        try
        {
            await FormidableCultureBootstrap.ApplyStoredCultureAsync(() => Task.FromResult<string?>(null));

            Assert.Equal(originalCulture, CultureInfo.DefaultThreadCurrentCulture);
            Assert.Equal(originalUiCulture, CultureInfo.DefaultThreadCurrentUICulture);
        }
        finally
        {
            CultureInfo.DefaultThreadCurrentCulture = originalCulture;
            CultureInfo.DefaultThreadCurrentUICulture = originalUiCulture;
        }
    }
}
