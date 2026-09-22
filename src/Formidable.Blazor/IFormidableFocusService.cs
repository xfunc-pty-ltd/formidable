using Microsoft.AspNetCore.Components.Forms;

namespace Formidable.Blazor;

/// <summary>
/// Moves keyboard focus (and scrolls into view) to the DOM element rendered for a field.
/// The target element is located by its <see cref="FormidableFieldId"/> id, which the kit's
/// inputs assign automatically — any element carrying that id can be focused this way.
/// </summary>
public interface IFormidableFocusService
{
    /// <summary>Scrolls to and focuses the element rendered for <paramref name="field"/>.</summary>
    /// <param name="field">The field whose rendered element should receive focus.</param>
    ValueTask FocusAsync(FieldIdentifier field);
}
