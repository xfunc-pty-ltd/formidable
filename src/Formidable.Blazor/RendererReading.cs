using Microsoft.AspNetCore.Components;

namespace Formidable.Blazor;

/// <summary>The reading both roots take of what their renderer says about itself.</summary>
internal static class RendererReading
{
    /// <summary>Whether the renderer says outright that it is not interactive; a renderer that cannot say counts as interactive.</summary>
    /// <typeparam name="TComponent">The component's type.</typeparam>
    /// <param name="component">The component whose renderer is read.</param>
    /// <param name="rendererInfo">Reads the component's <c>RendererInfo</c>, which is protected, so each root passes a static lambda that reads its own.</param>
    /// <returns><see langword="true"/> only when <c>RendererInfo.IsInteractive</c> reads <see langword="false"/>.</returns>
    // A renderer that declines to describe itself has said nothing, and nothing is not proof of a
    // dead end: bUnit's test renderer throws from RendererInfo unless a test declares one, and
    // rendering a form in a component test must not depend on having declared it. The reader is
    // a static lambda so the form's read on every render allocates nothing.
    internal static bool DeclaresItselfStatic<TComponent>(
        TComponent component, Func<TComponent, RendererInfo> rendererInfo)
    {
        try
        {
            return !rendererInfo(component).IsInteractive;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
