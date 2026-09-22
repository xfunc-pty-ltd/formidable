namespace Formidable.Blazor;

/// <summary>
/// Arguments for <see cref="IFormValidationEngine.StateChanged"/>. The class carries no detail:
/// it exists so the event's delegate shape never has to change — anything the event learns to
/// say about WHAT changed is added here as init-only properties, which a handler written before
/// the addition keeps compiling against and simply does not read. Blazor's own
/// <c>ValidationStateChangedEventArgs</c> makes the same bargain.
/// </summary>
public sealed class FormidableStateChangedEventArgs : EventArgs
{
    /// <summary>
    /// A reusable instance for raising the event without allocating per notification — the right
    /// arguments for any raise that has nothing beyond "state changed" to say.
    /// </summary>
    public static new readonly FormidableStateChangedEventArgs Empty = new();
}
