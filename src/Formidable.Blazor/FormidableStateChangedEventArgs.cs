using Microsoft.AspNetCore.Components.Forms;

namespace Formidable.Blazor;

/// <summary>The arguments <see cref="IFormidableEngine.StateChanged"/> carries: which fields' state or messages moved, and whether something no field owns did; grown by init-only properties so a handler written against it keeps compiling.</summary>
/// <remarks>
/// The engine fills both members on every publish. A handler that re-renders for one field skips
/// a publish whose <see cref="Includes"/> answers <see langword="false"/> for it; one that renders
/// form-wide state reads <see cref="FormChanged"/> too. A field the engine holds nothing for
/// (whether it never did or has since let it go) is named once the engine holds something for
/// it. Its pending state moves with <see cref="FormChanged"/> as the form starts and stops
/// checking for a submit, and as the page's fields change while one runs.
/// </remarks>
// The class exists so the event's delegate shape never has to change: anything the event learns
// to say about what changed is added here as init-only properties a handler written before the
// addition simply does not read. Blazor's own ValidationStateChangedEventArgs makes the same
// bargain.
public sealed class FormidableStateChangedEventArgs : EventArgs
{
    /// <summary>A shared instance that names every field as changed and no form-level change, for raising the event without allocating.</summary>
    public static new readonly FormidableStateChangedEventArgs Empty = new();

    /// <summary>The fields whose state or messages moved in this publish; <see langword="null"/> means every field, which <see cref="Empty"/> carries.</summary>
    public IReadOnlySet<FieldIdentifier>? ChangedFields { get; init; }

    /// <summary>Whether something no single field owns moved: the reading order, <see cref="IFormidableEngine.IsFormValid"/>, <see cref="IFormidableEngine.IsValidating"/>, <see cref="IFormidableEngine.IsSubmitting"/>, <see cref="IFormidableEngine.HasSubmitted"/>, or, as the page's fields change while a submit runs, which fields it marks as checking.</summary>
    public bool FormChanged { get; init; }

    /// <summary>Whether this publish names every field as changed, which is <see cref="ChangedFields"/> being <see langword="null"/>.</summary>
    public bool Everything => ChangedFields is null;

    /// <summary>Whether <paramref name="field"/>'s state or messages moved in this publish; <see langword="true"/> for every field when <see cref="ChangedFields"/> is <see langword="null"/>.</summary>
    /// <param name="field">The field a handler renders.</param>
    /// <returns><see langword="true"/> when the handler should re-render for <paramref name="field"/>.</returns>
    public bool Includes(FieldIdentifier field) => ChangedFields is null || ChangedFields.Contains(field);
}
