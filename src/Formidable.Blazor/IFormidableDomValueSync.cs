namespace Formidable.Blazor;

/// <summary>
/// Writes a field's authoritative value into its DOM element, addressed by
/// <see cref="FormidableFieldId"/>. The kit's date and number inputs call this on <c>blur</c>
/// because those are the two controls whose DOM can silently disagree with the model: a native
/// <c>&lt;input type="number"&gt;</c> holding text that is not a number (<c>"e3"</c>, a lone
/// <c>"-"</c>) keeps displaying it while reporting an empty <c>value</c> to every event, so no
/// render ever knows there is anything to overwrite — the box shows one thing, the model holds
/// another, and Blazor's diff, which only writes when the rendered value differs from what the
/// browser reported, cannot repair it. Writing the model's formatted value straight into the
/// element at blur is the one move that always reconciles the two.
/// </summary>
/// <remarks>
/// Public for the same reason <see cref="IFormidableFocusService"/> is: a consumer's own bUnit
/// tests can substitute a fake instead of configuring JS interop for every blur. Registered by
/// <c>AddFormidableBlazor</c>; existing registrations are respected. Unlike
/// <see cref="IFormidableFocusService"/> and <see cref="IFormidableFieldOrderService"/>, which take
/// a <see cref="Microsoft.AspNetCore.Components.Forms.FieldIdentifier"/> because they answer
/// questions about a field in the abstract, this writes to one already-rendered element, and its
/// caller already holds that element's id from rendering it.
/// <para>
/// The interface grows accordingly: a member added after v1 carries a default implementation
/// that writes nothing — the answer <see cref="SyncValueAsync"/> already gives for a missing
/// element. The model is the authority and a write here only reconciles an element's display
/// to it, so an implementation that does not override the addition skips a reconciliation
/// rather than corrupting anything.
/// </para>
/// </remarks>
public interface IFormidableDomValueSync
{
    /// <summary>
    /// Sets the value of the element with id <paramref name="elementId"/> to
    /// <paramref name="value"/> (<see langword="null"/> writes an empty string). A missing
    /// element is a no-op.
    /// </summary>
    /// <param name="elementId">The element's id, as <see cref="FormidableFieldId"/> renders it.</param>
    /// <param name="value">The field's current value, formatted the way the control renders it.</param>
    ValueTask SyncValueAsync(string elementId, string? value);
}
