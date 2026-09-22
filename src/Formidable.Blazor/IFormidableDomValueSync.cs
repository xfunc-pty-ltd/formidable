namespace Formidable.Blazor;

/// <summary>Writes a field's current value into the DOM element that renders it, so a number or date box cannot keep showing text the model rejected.</summary>
/// <remarks>
/// Implementing this interface is supported; a member added later carries a default
/// implementation, so an implementation written against these members keeps compiling.
/// <see cref="FormidableBlazorServiceCollectionExtensions.AddFormidableBlazor(Microsoft.Extensions.DependencyInjection.IServiceCollection)"/>
/// registers the JS-backed implementation only where none is registered, so a consumer's own,
/// or a test's recording double, registered first replaces it.
/// The kit's date and number inputs call it on <c>blur</c>.
/// </remarks>
// Takes the element id where IFormidableFocusService and IFormidableFieldOrderService take a
// FieldIdentifier: those answer questions about a field in the abstract, while this writes to one
// already-rendered element whose id its caller holds from rendering it.
// A member added here defaults to writing nothing, the answer SyncValueAsync already gives for a
// missing element: the model is the authority and a write only reconciles an element's display
// to it, so an implementation that does not override the addition skips a reconciliation rather
// than corrupting anything.
public interface IFormidableDomValueSync
{
    /// <summary>Sets the value of the element with id <paramref name="elementId"/> to <paramref name="value"/>, writing an empty string for <see langword="null"/>; a missing element is a no-op.</summary>
    /// <param name="elementId">The element's id, as <see cref="FormidableFieldId"/> renders it.</param>
    /// <param name="value">The field's current value, formatted the way the control renders it.</param>
    /// <returns>A task that completes when the write is done.</returns>
    ValueTask SyncValueAsync(string elementId, string? value);
}
