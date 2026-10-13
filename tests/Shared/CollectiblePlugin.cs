using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace Formidable.Tests.Shared;

/// <summary>Loads the plugin fixture into a collectible context and reports whether the context outlives its unload.</summary>
// Linked into each test project that pins a static cache against a plugin's types. The plugin is
// copied to a folder of its own beside the tests, and its references (Formidable, FluentValidation)
// resolve from the default context, as they would for a host that owns the library.
internal static class CollectiblePlugin
{
    private const int MaxCollections = 30;

    private static string PluginPath { get; } =
        Path.Combine(AppContext.BaseDirectory, "plugin", "Formidable.Plugin.Fixture.dll");

    /// <summary>Loads the plugin into a new collectible context, hands its assembly to <paramref name="use"/>, and unloads the context.</summary>
    /// <param name="use">What the host does with the plugin; anything it keeps from the assembly keeps the context alive.</param>
    /// <returns>A weak reference to the unloaded context.</returns>
    // Not inlined, so no local of this frame can keep the context or the assembly reachable.
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static WeakReference LoadUseAndUnload(Action<Assembly> use)
    {
        var context = new PluginLoadContext();
        use(context.LoadFromAssemblyPath(PluginPath));
        context.Unload();
        return new WeakReference(context);
    }

    /// <summary>Whether the context is still alive after up to 30 full collections.</summary>
    /// <param name="context">The weak reference <see cref="LoadUseAndUnload"/> returned.</param>
    /// <returns><see langword="true"/> when something still holds the context or one of its types.</returns>
    public static bool SurvivesCollections(WeakReference context)
    {
        for (var i = 0; context.IsAlive && i < MaxCollections; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        return context.IsAlive;
    }

    /// <summary>The plugin type of that name.</summary>
    /// <param name="plugin">The plugin's assembly.</param>
    /// <param name="name">The type's name, without its namespace.</param>
    /// <returns>The type.</returns>
    public static Type TypeOf(Assembly plugin, string name) =>
        plugin.GetType($"Formidable.Plugin.Fixture.{name}", throwOnError: true)!;

    /// <summary>A new instance of the plugin type of that name.</summary>
    /// <param name="plugin">The plugin's assembly.</param>
    /// <param name="name">The type's name, without its namespace.</param>
    /// <returns>The instance.</returns>
    public static object New(Assembly plugin, string name) => Activator.CreateInstance(TypeOf(plugin, name))!;

    // Returns null for every name, so each reference the plugin makes resolves from the default
    // context and only the plugin's own types are collectible.
    private sealed class PluginLoadContext() : AssemblyLoadContext("formidable-plugin-fixture", isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName) => null;
    }
}

/// <summary>The two controls every project's unload tests inherit: the collections can see an unload, and they can see a pin.</summary>
public abstract class PluginUnloadControls
{
    [Fact]
    public void A_plugin_validating_its_own_model_under_Submit_unloads()
    {
        var context = CollectiblePlugin.LoadUseAndUnload(static plugin =>
            Assert.True((bool)CollectiblePlugin.TypeOf(plugin, "PluginEntry").GetMethod("ValidateUnderSubmit")!.Invoke(null, null)!));

        Assert.False(CollectiblePlugin.SurvivesCollections(context));
    }

    [Fact]
    public void A_held_plugin_object_keeps_the_context_alive_until_released()
    {
        var held = new List<object>();
        var context = CollectiblePlugin.LoadUseAndUnload(plugin => held.Add(CollectiblePlugin.New(plugin, "PluginOrder")));

        Assert.True(CollectiblePlugin.SurvivesCollections(context));
        held.Clear();
        Assert.False(CollectiblePlugin.SurvivesCollections(context));
    }
}
