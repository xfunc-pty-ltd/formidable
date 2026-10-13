namespace Formidable.Blazor;

/// <summary>Where a form stood as a page sent a request: taken with <see cref="IFormidableEngine.MarkServerRequest"/> as the request leaves, and handed back with its reply.</summary>
/// <remarks>
/// The default value marks nothing, and a reply applied with it applies as an unmarked reply does.
/// </remarks>
public readonly struct ServerRequestMark
{
    internal ServerRequestMark(object owner, int editStamp, int generation)
    {
        Owner = owner;
        EditStamp = editStamp;
        Generation = generation;
    }

    // A token that stands for the engine that took the mark, or null for the default mark. A reset
    // builds a new engine, so identity is what tells a reset apart: the new engine's readings start
    // again from zero. A token of the engine's own rather than the engine itself, so a mark a page
    // keeps after its form is gone keeps nothing of the form alive.
    internal object? Owner { get; }

    // Whether this is the default mark, which marks nothing.
    internal bool IsDefault => Owner is null;

    // The engine's edit stamp as the mark was taken; an edit or a load moves it.
    internal int EditStamp { get; }

    // The verdict store's generation as the mark was taken; a rendered-field-set change moves it.
    internal int Generation { get; }
}
