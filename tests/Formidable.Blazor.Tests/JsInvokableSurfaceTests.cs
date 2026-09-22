using System.Reflection;
using Microsoft.JSInterop;

namespace Formidable.Blazor.Tests;

/// <summary>
/// <see cref="JSInvokableAttribute"/> requires a public method, which makes it the one way a
/// browser callback can arrive on a consumer's API by accident. Nothing about the browser side
/// changes when it does, so no journey and no bUnit render can see it: only this can.
/// </summary>
public class JsInvokableSurfaceTests
{
    [Fact]
    public void No_JSInvokable_method_is_reachable_from_outside_the_library()
    {
        var reachable = typeof(FormidableForm<>).Assembly
            .GetTypes()
            .SelectMany(type => type.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(method => method.IsDefined(typeof(JSInvokableAttribute), inherit: false))
            .Where(method => method.DeclaringType!.IsVisible)
            .Select(method => $"{method.DeclaringType!.Name}.{method.Name}")
            .ToArray();

        Assert.Empty(reachable);
    }
}
