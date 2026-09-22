namespace Formidable.Blazor;

/// <summary>The arguments <see cref="IFormidableEngine.StateChanged"/> carries; empty, and grown by init-only properties so a handler written against it keeps compiling.</summary>
// The class exists so the event's delegate shape never has to change: anything the event learns
// to say about what changed is added here as init-only properties a handler written before the
// addition simply does not read. Blazor's own ValidationStateChangedEventArgs makes the same
// bargain.
public sealed class FormidableStateChangedEventArgs : EventArgs
{
    /// <summary>A shared instance for raising the event without allocating.</summary>
    public static new readonly FormidableStateChangedEventArgs Empty = new();
}
