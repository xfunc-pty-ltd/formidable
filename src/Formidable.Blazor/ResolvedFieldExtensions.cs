using Formidable.Introspection;
using Microsoft.AspNetCore.Components.Forms;

namespace Formidable.Blazor;

/// <summary>Bridges introspector results to Blazor <see cref="FieldIdentifier"/>s.</summary>
public static class ResolvedFieldExtensions
{
    /// <summary>
    /// Converts a resolved field to a <see cref="FieldIdentifier"/>. An empty property name on
    /// the root model produces the model-level identifier
    /// (<c>new FieldIdentifier(rootModel, string.Empty)</c>). A value-type owner (the object
    /// the path reaches just before the member is a struct, which <see cref="FieldIdentifier"/>
    /// cannot hold) falls back to the root model with the original path as the field name.
    /// </summary>
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
