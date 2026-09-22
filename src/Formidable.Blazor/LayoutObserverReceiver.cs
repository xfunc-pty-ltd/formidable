using Microsoft.JSInterop;

namespace Formidable.Blazor;

/// <summary>The object the script's layout observer calls back into, which hands the report on to the form.</summary>
/// <param name="onLayoutMoved">Run when the browser reports a change under the form element.</param>
// A type of its own because JSInvokableAttribute requires a public method, and a public method
// on a component is public API, which this is not. Interop resolves the method from the
// instance the reference was created over, so a public method on an internal type is reached
// exactly as one on a public type is, and the form keeps its own handler private.
internal sealed class LayoutObserverReceiver(Func<Task> onLayoutMoved)
{
    /// <summary>Called by name from the script's <c>MutationObserver</c> callback when the form element's subtree changed; renaming it here renames nothing in the browser.</summary>
    /// <returns>A task that completes once the form has answered the report.</returns>
    [JSInvokable]
    public Task NotifyLayoutMoved() => onLayoutMoved();
}
