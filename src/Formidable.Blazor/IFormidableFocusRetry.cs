using Microsoft.AspNetCore.Components.Forms;

namespace Formidable.Blazor;

/// <summary>The shipped focus service's second attempt, made after a <c>FocusFallback</c> answers <see langword="true"/>, told apart from a first attempt so focus the visitor moved meanwhile stays put.</summary>
// Internal and reached through a type test, so a consumer's own IFormidableFocusService, or one
// that wraps the shipped service, implements FocusAsync alone and gets a plain second FocusAsync.
internal interface IFormidableFocusRetry
{
    /// <summary>Focuses the field's element as <see cref="IFormidableFocusService.FocusAsync"/> does, unless the latest first-attempt miss was this field's and, since it, the visitor pressed a key or a pointer button and focus sits on another control.</summary>
    /// <param name="field">The field whose first attempt missed.</param>
    /// <returns><see langword="true"/> when the element took focus; <see langword="false"/> when it did not, or when focus stays where the visitor moved it.</returns>
    ValueTask<bool> RetryFocusAsync(FieldIdentifier field);
}
