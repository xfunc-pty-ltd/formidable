using System.Collections;
using System.Globalization;
using Formidable.Introspection;
using Microsoft.AspNetCore.Components.Forms;

namespace Formidable.Blazor;

/// <summary>Turns an introspector's <see cref="ResolvedField"/> into a Blazor <see cref="FieldIdentifier"/>.</summary>
public static class ResolvedFieldExtensions
{
    /// <summary>Builds a <see cref="FieldIdentifier"/> for the resolved field: the owner and member; for an element of a non-generic <see cref="IList"/> (an array or a <c>List&lt;T&gt;</c>), the collection and the index, as Blazor names it; or <paramref name="rootModel"/> and <paramref name="originalPath"/> for a value-type owner.</summary>
    /// <param name="field">The resolved field.</param>
    /// <param name="rootModel">The model the path was resolved against.</param>
    /// <param name="originalPath">The path as the validator or the server reply spelled it.</param>
    /// <returns>The identifier; an empty member on the root gives the model-level identifier.</returns>
    /// <remarks>
    /// An element is named by its index alone: <c>Tags[0]</c> gives the list and <c>0</c>, the
    /// field <c>FieldIdentifier.Create(() => model.Tags[0])</c> names, so a row bound that way
    /// receives its own messages. A dictionary's entry keeps its brackets, and so does an element of
    /// a collection that implements only the generic <see cref="IList{T}"/>, whose rows bound by
    /// index receive none of their messages.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="rootModel"/> is <see langword="null"/>.</exception>
    public static FieldIdentifier ToFieldIdentifier(this ResolvedField field, object rootModel, string originalPath)
    {
        ArgumentNullException.ThrowIfNull(rootModel);

        if (field.Owner.GetType().IsValueType)
        {
            return new FieldIdentifier(rootModel, originalPath);
        }

        // A list or array element is the field Blazor names by the collection and the index, so
        // the brackets go. Only a non-generic IList counts, which every array, List<T> and
        // Collection<T> is: FluentValidation numbers a dictionary's entries by position, while
        // Blazor names an entry by its key, so an entry keeps its brackets rather than meet another
        // entry's input. A collection that implements only the generic IList<T> keeps them too.
        if (field.Owner is IList
            && field.PropertyName is ['[', .., ']'] name
            && int.TryParse(name.AsSpan(1, name.Length - 2), NumberStyles.None, CultureInfo.InvariantCulture, out var index))
        {
            return new FieldIdentifier(field.Owner, index.ToString(CultureInfo.InvariantCulture));
        }

        return new FieldIdentifier(field.Owner, field.PropertyName);
    }
}
