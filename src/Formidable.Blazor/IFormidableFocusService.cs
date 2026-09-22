using Microsoft.AspNetCore.Components.Forms;

namespace Formidable.Blazor;

/// <summary>
/// Moves keyboard focus (and scrolls into view) to the DOM element rendered for a field.
/// The target element is located by its <see cref="FormidableFieldId"/> id, which the kit's
/// inputs assign automatically — any element carrying that id can be focused this way.
/// </summary>
/// <remarks>
/// Implementing this interface — a recording double in a bUnit test, a focus behaviour of
/// your own — is supported surface, and it grows accordingly: a member added after v1
/// carries a default implementation that does nothing and reports having done nothing, the
/// answer <see cref="FocusAsync"/> already gives for an element the DOM does not hold.
/// Moving focus is a courtesy, so an implementation that does not override the addition
/// declines it and leaves the page as it was.
/// </remarks>
public interface IFormidableFocusService
{
    /// <summary>
    /// Moves focus to the rendered element for <paramref name="field"/>. The scroll that brings it
    /// into view prefers the field's message list when one is rendered, so clicking a
    /// collection-level issue shows the message that was clicked rather than the middle of the
    /// group; it falls back to the focus target itself when no message list exists. Returns
    /// <c>true</c> when the element was found and focused, <c>false</c> when no element with the
    /// field's id exists in the DOM — e.g. a virtualized row outside the render window.
    /// </summary>
    /// <param name="field">The field whose rendered element should receive focus.</param>
    ValueTask<bool> FocusAsync(FieldIdentifier field);
}
