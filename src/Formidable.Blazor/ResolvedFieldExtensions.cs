using System.Collections;
using System.Globalization;
using Formidable.Introspection;
using Microsoft.AspNetCore.Components.Forms;

namespace Formidable.Blazor;

/// <summary>Turns an introspector's <see cref="ResolvedField"/> into a Blazor <see cref="FieldIdentifier"/>.</summary>
public static class ResolvedFieldExtensions
{
    /// <summary>Builds a <see cref="FieldIdentifier"/> for the resolved field: the owner and member; for an element of a non-generic <see cref="IList"/> that is not an <see cref="IDictionary"/> (an array, a <c>List&lt;T&gt;</c>), the collection and the index, as Blazor names it; or <paramref name="rootModel"/> and <paramref name="originalPath"/> for a value-type owner.</summary>
    /// <param name="field">The resolved field.</param>
    /// <param name="rootModel">The model the path was resolved against.</param>
    /// <param name="originalPath">The path as the validator or the server reply spelled it.</param>
    /// <returns>The identifier; an empty member on the root gives the model-level identifier.</returns>
    /// <remarks>
    /// An element is named by its index alone: <c>Tags[0]</c> gives the list and <c>0</c>, the
    /// field <c>FieldIdentifier.Create(() => model.Tags[0])</c> names, so a row bound that way
    /// receives its own messages. A dictionary's entry keeps its brackets, an
    /// <see cref="OrderedDictionary{TKey, TValue}"/>'s included, because FluentValidation numbers
    /// entries by position and Blazor names one by its key. So does an element of a collection that
    /// implements only the generic <see cref="IList{T}"/>, whose rows bound by index receive none
    /// of their messages.
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
        // the brackets go. Only a non-generic IList that is not a dictionary counts (every array,
        // List<T> and Collection<T>). FluentValidation numbers a dictionary's entries by position,
        // while Blazor names an entry by its key, so an entry keeps its brackets rather than meet
        // another entry's input; an OrderedDictionary is a list as well, and keeps them too. So
        // does a collection that implements only the generic IList<T>.
        if (IsIndexedList(field.Owner)
            && field.PropertyName is ['[', .., ']'] name
            && TryParseIndex(name.AsSpan(1, name.Length - 2), out var index))
        {
            return new FieldIdentifier(field.Owner, index.ToString(CultureInfo.InvariantCulture));
        }

        return new FieldIdentifier(field.Owner, field.PropertyName);
    }

    /// <summary>Whether <paramref name="owner"/> names its elements by index: a non-generic <see cref="IList"/> that is not an <see cref="IDictionary"/>.</summary>
    /// <param name="owner">The object a field names a member or an element of.</param>
    /// <returns><see langword="true"/> for a non-generic <see cref="IList"/> that is not an <see cref="IDictionary"/>, such as an array, a <c>List&lt;T&gt;</c> or a <c>Collection&lt;T&gt;</c>; <see langword="false"/> otherwise, every dictionary included, an <see cref="OrderedDictionary{TKey, TValue}"/> among them.</returns>
    internal static bool IsIndexedList(object owner) => owner is IList and not IDictionary;

    /// <summary>Whether <paramref name="field"/> names an element by its index, the way <see cref="ToFieldIdentifier"/> and <c>FieldIdentifier.Create(() => model.Tags[i])</c> both name one.</summary>
    /// <param name="field">The field.</param>
    /// <returns><see langword="true"/> when the field's model passes <see cref="IsIndexedList"/> and its name is an index that <see cref="TryParseIndex"/> reads.</returns>
    internal static bool IsIndexedElement(FieldIdentifier field) =>
        IsIndexedList(field.Model) && TryParseIndex(field.FieldName, out _);

    /// <summary>Reads an element's index, written as digits alone with no sign, space or separator.</summary>
    /// <param name="digits">The index, without its brackets.</param>
    /// <param name="index">The index read.</param>
    /// <returns><see langword="true"/> when <paramref name="digits"/> is a whole index.</returns>
    internal static bool TryParseIndex(ReadOnlySpan<char> digits, out int index) =>
        int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out index);
}
