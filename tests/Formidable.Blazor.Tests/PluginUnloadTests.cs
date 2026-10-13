using Formidable.Tests.Shared;

namespace Formidable.Blazor.Tests;

// A host that loads models from a plugin in a collectible context can unload the plugin once it is
// done with it, as long as no static cache holds one of the plugin's types. Blazor's own
// FieldIdentifier keeps an accessor per type an input's expression reads from, so a plugin whose
// pages bind inputs stays loaded anyway; the tests here reach each of this package's caches
// without one.
public class PluginUnloadTests : PluginUnloadControls
{
    // The submit-coverage check remembers per type whether a collection that is no list yields
    // key-value pairs.
    [Fact]
    public void A_plugin_collection_of_pairs_unloads()
    {
        var context = CollectiblePlugin.LoadUseAndUnload(static plugin =>
            Assert.True(SubmitCoverageTracker.IsDictionary(CollectiblePlugin.New(plugin, "PluginPairs"))));

        Assert.False(CollectiblePlugin.SurvivesCollections(context));
    }

    // Field naming remembers per type whether a collection is a list known by its generic
    // interface, and the element type that interface names, here a plugin type too.
    [Fact]
    public void A_plugin_list_known_by_its_generic_interface_unloads()
    {
        var context = CollectiblePlugin.LoadUseAndUnload(static plugin =>
        {
            var list = CollectiblePlugin.New(plugin, "PluginList");
            Assert.True(ResolvedFieldExtensions.IsIndexedList(list));
            Assert.Same(CollectiblePlugin.TypeOf(plugin, "PluginOrder"), ResolvedFieldExtensions.GenericListElementType(list.GetType()));
        });

        Assert.False(CollectiblePlugin.SurvivesCollections(context));
    }
}
