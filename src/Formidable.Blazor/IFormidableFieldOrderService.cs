using Microsoft.AspNetCore.Components.Forms;

namespace Formidable.Blazor;

/// <summary>Answers which of a form's fields are on the page and in what order, so a blocked submit's issues list in the order the visitor reads the form rather than the order the validator declares its rules.</summary>
/// <remarks>
/// Implementing this interface is supported; a member added later carries a default
/// implementation, so an implementation written against these members keeps compiling.
/// <see cref="FormidableBlazorServiceCollectionExtensions.AddFormidableBlazor(Microsoft.Extensions.DependencyInjection.IServiceCollection)"/>
/// registers the JS-backed implementation only where none is registered, so a consumer's own,
/// or a test's recording double, registered first replaces it.
/// The currency is the field, not its element id; <see cref="FormidableFieldId.For(FieldIdentifier)"/>
/// maps a field to the id its element carries.
/// </remarks>
// A member added here defaults to answering that nothing could be resolved, the state a null
// from OrderAsync already puts a host in (validator order stands until a later render
// resolves), so an implementation that does not override the addition never asserts an order
// it did not derive.
public interface IFormidableFieldOrderService
{
    /// <summary>Returns the given fields that are on the page, in document order, omitting any no element carries, or <see langword="null"/> when the order could not be resolved.</summary>
    /// <param name="fields">The fields to place, in any order; the host includes the model-level field, whose element is the <c>&lt;form&gt;</c>, and <see cref="FormidableFieldId.For(FieldIdentifier)"/> derives its id as for any other field.</param>
    /// <returns>The placed fields in document order, an empty list when none is on the page, or <see langword="null"/> to be asked again on a later render.</returns>
    /// <remarks>
    /// The shipped implementation locates each field's element by its id with
    /// <c>document.getElementById</c>, so an element inside a shadow root reads as absent and its
    /// field sorts last, and where two elements share an id the first in document order takes
    /// the field's place.
    /// </remarks>
    ValueTask<IReadOnlyList<FieldIdentifier>?> OrderAsync(IReadOnlyList<FieldIdentifier> fields);
}
