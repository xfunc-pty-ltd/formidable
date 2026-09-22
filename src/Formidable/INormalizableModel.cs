namespace Formidable;

/// <summary>
/// A model that can clear values not applicable to its current selections — deselected option
/// branches, rows with no content — before persistence or validation. Called by form
/// infrastructure prior to save/submit on the client and enforced again at the server boundary.
/// </summary>
public interface INormalizableModel
{
    /// <summary>Clears fields not applicable to the current selection state and strips empty collection rows.</summary>
    void Normalize();
}
