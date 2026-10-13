using Formidable.Tests.Shared;

namespace Formidable.Tests;

// A host that loads validators from a plugin in a collectible context can unload the plugin once it
// is done with it, as long as no static cache holds one of the plugin's types. Each test here
// reaches one of core's caches with a plugin type.
public class PluginUnloadTests : PluginUnloadControls
{
    // DraftSubmitValidator<T> remembers its overlap scan per derived class. When T is the host's,
    // the closed base class belongs to the default context and outlives the plugin, so it must not
    // hold the plugin's validator type.
    [Fact]
    public void A_plugin_validator_for_a_host_model_unloads()
    {
        var context = CollectiblePlugin.LoadUseAndUnload(static plugin =>
            Assert.IsAssignableFrom<DraftSubmitValidator<Version>>(CollectiblePlugin.New(plugin, "HostModelValidator")));

        Assert.False(CollectiblePlugin.SurvivesCollections(context));
    }
}
