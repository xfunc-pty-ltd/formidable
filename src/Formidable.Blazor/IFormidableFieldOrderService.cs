using Microsoft.AspNetCore.Components.Forms;

namespace Formidable.Blazor;

/// <summary>
/// Resolves the document order of a form's rendered fields: given a set of fields, it answers
/// with those that are actually on the page, in the order they appear in. That is the order a
/// visitor reads the form in, and therefore the order a blocked submit's issues are reported in,
/// rather than the order the validator declares its rules.
/// </summary>
/// <remarks>
/// <para>
/// Public for the same reason <see cref="IFormidableFocusService"/> is: the shipped
/// implementation is JS-backed, so a consumer's own bUnit tests can substitute a fake instead of
/// configuring module interop for every render. Registered by <c>AddFormidableBlazor</c>;
/// existing registrations are respected.
/// </para>
/// <para>
/// The currency is the field rather than its rendered element id, so an implementation can order
/// by anything it knows about a field; a field maps to the id its element carries through
/// <see cref="FormidableFieldId.For(FieldIdentifier)"/>, which is how the shipped implementation
/// asks the browser.
/// </para>
/// <para>
/// The interface grows accordingly: a member added after v1 carries a default implementation
/// answering "nothing could be resolved" — the state a <see langword="null"/> from
/// <see cref="OrderAsync"/> already puts a host in, where validator order stands until a
/// later render resolves. An implementation that does not override the addition therefore
/// never asserts an order it did not derive.
/// </para>
/// </remarks>
public interface IFormidableFieldOrderService
{
    /// <summary>
    /// Returns the fields from <paramref name="fields"/> that are present in the DOM, in document
    /// order. A field no element carries is omitted rather than reported — a field that renders
    /// nothing has no place on the page to report. Returns <see langword="null"/> when the order
    /// could not be resolved at all, which a host should treat as "ask again on a later render"
    /// rather than as an empty page.
    /// </summary>
    /// <param name="fields">
    /// The fields to locate, in any order. Not only the fields a consumer registered through
    /// <see cref="FieldRegistry"/>: the shipped host always adds one field of its own, the
    /// model-level identifier (a <see cref="FieldIdentifier"/> with an empty
    /// <see cref="FieldIdentifier.FieldName"/>) whose element is the form's own <c>&lt;form&gt;</c>
    /// tag — an implementation need not special-case it, since <see cref="FormidableFieldId.For"/>
    /// derives an id for it the same way it does for any other field.
    /// </param>
    /// <remarks>
    /// "Present in the DOM" is what the shipped implementation can see, and it locates each
    /// field by the id <see cref="FormidableFieldId.For(FieldIdentifier)"/> mints, through
    /// <c>document.getElementById</c>. That call has two blind spots. An element inside a shadow
    /// root — an open one included — is not found, so its field is reported absent, the same
    /// answer a field that renders nothing gets, and the shipped host sorts an absent field last.
    /// Where two elements carry the same id, the call answers with the first in document order,
    /// so that is the position the field takes and the other element is never consulted. Three
    /// things reach that: one field rendered in two places, a consumer id that collides with a
    /// minted one, and two different fields whose minted ids collide — an id is a pair of 32-bit
    /// hashes, and <see cref="FormidableFieldId.For(FieldIdentifier)"/> promises only that two of
    /// them are overwhelmingly likely to differ. Both blind spots are stated for the same reason:
    /// an element can be on the page without this answer accounting for it.
    /// </remarks>
    ValueTask<IReadOnlyList<FieldIdentifier>?> OrderAsync(IReadOnlyList<FieldIdentifier> fields);
}
