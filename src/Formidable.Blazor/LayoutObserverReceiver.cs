using Microsoft.JSInterop;

namespace Formidable.Blazor;

/// <summary>
/// The browser's way back into a form: the library's own script reports that the form's rendered
/// elements moved, and this hands that report on to whatever the form does about it.
/// </summary>
/// <remarks>
/// It is a type of its own because <see cref="JSInvokableAttribute"/> requires a public method, and
/// a public method on a component is public API — which this is not, and which nothing outside the
/// library has any reason to call. Interop resolves the method from the instance the reference was
/// created over, so a public method on an internal type is reached exactly as one on a public type
/// is, and the form keeps its own handler private.
/// </remarks>
/// <param name="onLayoutMoved">Run when the browser reports a move.</param>
internal sealed class LayoutObserverReceiver(Func<Task> onLayoutMoved)
{
    /// <summary>
    /// Called from the script's <c>MutationObserver</c> callback, by this name.
    /// </summary>
    /// <remarks>
    /// The name is the string the script invokes rather than a C# naming choice, which is why it
    /// carries no <c>Async</c> suffix: renaming it here renames nothing in the browser.
    /// </remarks>
    /// <returns>A task completing once the form has answered the report.</returns>
    [JSInvokable]
    public Task NotifyLayoutMoved() => onLayoutMoved();
}
