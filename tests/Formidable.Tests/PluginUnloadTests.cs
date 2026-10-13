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

    // A plain validator met under a profile that leaves out the default rules has its rules counted,
    // and a profile seen to select a rule is remembered per validator type while the type is
    // loaded. The record lives in core, a default-context type, so it must not hold the plugin's
    // validator type.
    [Fact]
    public void A_plugin_validator_met_under_a_profile_without_the_default_rules_unloads()
    {
        var context = CollectiblePlugin.LoadUseAndUnload(static plugin =>
            Assert.True((bool)CollectiblePlugin.TypeOf(plugin, "PluginEntry").GetMethod("ValidateUnderReviewOnly")!.Invoke(null, null)!));

        Assert.False(CollectiblePlugin.SurvivesCollections(context));
    }
}
