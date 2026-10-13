using System.Reflection;
using FluentValidation;
using Formidable.Tests.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Formidable.AspNetCore.Tests;

// A host that loads models and validators from a plugin in a collectible context can unload the
// plugin once it is done with it, as long as no static cache holds one of the plugin's types. The
// test here reaches this package's cache with a plugin type.
public class PluginUnloadTests : PluginUnloadControls
{
    // [Validate] remembers the ValidateAsync method it calls per argument type, for every request.
    [Fact]
    public void Validate_on_a_plugin_model_unloads()
    {
        var context = CollectiblePlugin.LoadUseAndUnload(static plugin => Assert.True(RunValidateFilter(plugin)));

        Assert.False(CollectiblePlugin.SurvivesCollections(context));
    }

    // One request through the filter, with the plugin's validator registered as the app would
    // register it. Answers whether the action ran.
    private static bool RunValidateFilter(Assembly plugin)
    {
        var modelType = CollectiblePlugin.TypeOf(plugin, "PluginOrder");
        var services = new ServiceCollection();
        services.AddSingleton(typeof(IValidator<>).MakeGenericType(modelType), CollectiblePlugin.New(plugin, "PluginOrderValidator"));
        services.AddFormidable();
        using var provider = services.BuildServiceProvider();

        var executing = new ActionExecutingContext(
            new ActionContext(new DefaultHttpContext { RequestServices = provider }, new RouteData(), new ActionDescriptor()),
            new List<IFilterMetadata>(),
            new Dictionary<string, object?> { ["order"] = Activator.CreateInstance(modelType) },
            controller: new object());
        var ranAction = false;
        new ValidateAttribute()
            .OnActionExecutionAsync(executing, () =>
            {
                ranAction = true;
                return Task.FromResult<ActionExecutedContext>(null!);
            })
            .GetAwaiter()
            .GetResult();

        return ranAction;
    }
}
