namespace Formidable;

/// <summary>A model that clears the values its current selections make irrelevant before it is validated or saved.</summary>
/// <remarks>
/// Both server adapters call <see cref="Normalize"/> before validating; the Blazor engine calls
/// it at submit when its <c>NormalizeOnSubmit</c> option is set; any caller may call it directly
/// before saving a draft. Implementing this interface is supported; a member added later carries
/// a default implementation, so an implementation written against these members keeps compiling.
/// </remarks>
// A member added here defaults to clearing nothing the model's own Normalize does not, so a model
// that does not override it keeps its normalization unchanged.
public interface INormalizableModel
{
    /// <summary>Clears fields not applicable to the current selection state and strips empty collection rows.</summary>
    void Normalize();
}
