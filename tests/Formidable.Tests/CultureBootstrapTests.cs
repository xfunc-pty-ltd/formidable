using System.Globalization;
using Formidable.Sample.Shared;

namespace Formidable.Tests;

// CultureInfo.DefaultThreadCurrentCulture/UICulture are process-wide statics, so every test here
// saves and restores both in a finally block regardless of outcome. The restore settles the
// value, not the window it stood in: xunit runs one class's [Fact]s one at a time, so these three
// cannot interleave with each other, and runs different classes in parallel, so any other class
// in this assembly can read a culture set here. The collection is the coordination for that half
// - see ProcessGlobalStateCollection for how far it reaches and who owes membership.
[Collection(ProcessGlobalStateCollection.Name)]
public class CultureBootstrapTests
{
    [Fact]
    public async Task Valid_stored_culture_sets_both_default_thread_cultures()
    {
        var originalCulture = CultureInfo.DefaultThreadCurrentCulture;
        var originalUiCulture = CultureInfo.DefaultThreadCurrentUICulture;

        try
        {
            await CultureBootstrap.ApplyStoredCultureAsync(() => Task.FromResult<string?>("de-DE"));

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
            await CultureBootstrap.ApplyStoredCultureAsync(
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
            await CultureBootstrap.ApplyStoredCultureAsync(() => Task.FromResult<string?>(null));

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
