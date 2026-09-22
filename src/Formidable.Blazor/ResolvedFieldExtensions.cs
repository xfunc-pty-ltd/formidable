using Formidable.Introspection;
using Microsoft.AspNetCore.Components.Forms;

namespace Formidable.Blazor;

/// <summary>Turns an introspector's <see cref="ResolvedField"/> into a Blazor <see cref="FieldIdentifier"/>.</summary>
public static class ResolvedFieldExtensions
{
    /// <summary>Builds a <see cref="FieldIdentifier"/> from the resolved owner and member, or from <paramref name="rootModel"/> and <paramref name="originalPath"/> when the owner is a value type, which <see cref="FieldIdentifier"/> cannot hold.</summary>
    /// <param name="field">The resolved field.</param>
    /// <param name="rootModel">The model the path was resolved against.</param>
    /// <param name="originalPath">The path as the validator or the server reply spelled it.</param>
    /// <returns>The identifier; an empty member on the root gives the model-level identifier.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="rootModel"/> is <see langword="null"/>.</exception>
    public static FieldIdentifier ToFieldIdentifier(this ResolvedField field, object rootModel, string originalPath)
    {
        ArgumentNullException.ThrowIfNull(rootModel);

        if (field.Owner.GetType().IsValueType)
        {
            return new FieldIdentifier(rootModel, originalPath);
        }

        return new FieldIdentifier(field.Owner, field.PropertyName);
    }
}
