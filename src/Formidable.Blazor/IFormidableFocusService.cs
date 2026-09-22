using Microsoft.AspNetCore.Components.Forms;

namespace Formidable.Blazor;

/// <summary>Moves focus to the element carrying a field's <see cref="FormidableFieldId"/> id and scrolls it into view; the seam every focus move the library makes goes through.</summary>
/// <remarks>
/// Implementing this interface is supported; a member added later carries a default
/// implementation, so an implementation written against these members keeps compiling.
/// <see cref="FormidableBlazorServiceCollectionExtensions.AddFormidableBlazor(Microsoft.Extensions.DependencyInjection.IServiceCollection)"/>
/// registers the JS-backed implementation only where none is registered, so a consumer's own,
/// or a test's recording double, registered first replaces it.
/// </remarks>
// A member added here defaults to doing nothing and reporting that nothing took focus, the
// answer FocusAsync already gives when nothing did: moving focus is a courtesy, so an
// implementation that does not override the addition declines it and leaves the page as it was.
public interface IFormidableFocusService
{
    /// <summary>Moves focus to the element carrying <paramref name="field"/>'s id and scrolls its message list, or the element itself when no list is rendered, into view.</summary>
    /// <param name="field">The field whose element should take focus.</param>
    /// <returns><see langword="true"/> when the element took focus; <see langword="false"/> when no element carries the id or the one that does will not take focus.</returns>
    ValueTask<bool> FocusAsync(FieldIdentifier field);
}
