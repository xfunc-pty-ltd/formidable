namespace Formidable;

/// <summary>
/// A model that can clear values not applicable to its current selections — deselected option
/// branches, rows with no content — before persistence or validation. Called by the server
/// validation filters before validation; client code may invoke it directly before saving
/// drafts.
/// </summary>
/// <remarks>
/// Implemented by consumer models, so it grows accordingly: a member added after v1 carries a
/// default implementation, and a model that does not override it keeps compiling with its
/// normalization unchanged — the default clears nothing the model's own
/// <see cref="Normalize"/> does not.
/// </remarks>
public interface INormalizableModel
{
    /// <summary>Clears fields not applicable to the current selection state and strips empty collection rows.</summary>
    void Normalize();
}
