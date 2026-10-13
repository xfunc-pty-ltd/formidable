using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using Formidable.Introspection;
using Microsoft.AspNetCore.Components.Forms;

namespace Formidable.Blazor;

/// <summary>Turns an introspector's <see cref="ResolvedField"/> into a Blazor <see cref="FieldIdentifier"/>.</summary>
public static class ResolvedFieldExtensions
{
    /// <summary>Builds the resolved field's <see cref="FieldIdentifier"/>: the owner and member; for an element of a list that is not a dictionary, the collection and the index, as Blazor names an element bound by its position; or <paramref name="rootModel"/> and <paramref name="originalPath"/> for a value-type owner.</summary>
    /// <param name="field">The resolved field.</param>
    /// <param name="rootModel">The model the path was resolved against.</param>
    /// <param name="originalPath">The path as the validator or the server reply spelled it.</param>
    /// <returns>The identifier; an empty member on the root gives the model-level identifier.</returns>
    /// <remarks>
    /// An element is named by its index alone: <c>Tags[0]</c> gives the list and <c>0</c>, the
    /// field <c>FieldIdentifier.Create(() => model.Tags[0])</c> names, so a row bound that way
    /// receives its own messages. A list is an array or any collection implementing
    /// <see cref="IList"/>, <see cref="IList{T}"/> or <see cref="IReadOnlyList{T}"/>. A dictionary's
    /// entry keeps its brackets, an <see cref="OrderedDictionary{TKey, TValue}"/>'s included,
    /// because FluentValidation numbers entries by position and Blazor names one by its key.
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
        // the brackets go. A list is any collection IsIndexedList accepts: a non-generic IList, or
        // one implementing only IList<T> or IReadOnlyList<T>. FluentValidation numbers a
        // dictionary's entries by position, while Blazor names an entry by its key, so an entry
        // keeps its brackets rather than meet another entry's input; an OrderedDictionary is a
        // list as well, and keeps them too.
        if (IsIndexedList(field.Owner)
            && field.PropertyName is ['[', .., ']'] name
            && TryParseIndex(name.AsSpan(1, name.Length - 2), out var index))
        {
            return new FieldIdentifier(field.Owner, index.ToString(CultureInfo.InvariantCulture));
        }

        return new FieldIdentifier(field.Owner, field.PropertyName);
    }

    /// <summary>Whether <paramref name="owner"/> names its elements by index: a list that is not a dictionary.</summary>
    /// <param name="owner">The object a field names a member or an element of.</param>
    /// <returns><see langword="true"/> for a non-generic <see cref="IList"/>, such as an array, a <c>List&lt;T&gt;</c> or a <c>Collection&lt;T&gt;</c>, and for a collection implementing <see cref="IList{T}"/> or <see cref="IReadOnlyList{T}"/>, when it is no dictionary; <see langword="false"/> otherwise, every dictionary included, an <see cref="OrderedDictionary{TKey, TValue}"/> among them.</returns>
    // A dictionary of either kind is excluded before the list tests, because an ordered one is a
    // list of its pairs as well. A collection outside the non-generic interfaces is read once per
    // type (ListShapeOf), so a form's every read after the first is a cache hit.
    internal static bool IsIndexedList(object owner) => owner switch
    {
        IDictionary => false,
        IList => true,
        string => false,
        IEnumerable => ListShapeOf(owner.GetType()).IsList,
        _ => false,
    };

    /// <summary>The element type a collection that implements only <see cref="IList{T}"/> or <see cref="IReadOnlyList{T}"/> declares.</summary>
    /// <param name="type">The collection's type.</param>
    /// <returns>The list interface's type argument; <see langword="null"/> for a type that implements neither interface, or that is a dictionary.</returns>
    internal static Type? GenericListElementType(Type type) => ListShapeOf(type).ElementType;

    /// <summary>Whether a type is a list known by its generic interface, and the element type that interface names.</summary>
    /// <param name="IsList">Whether the type implements <see cref="IList{T}"/> or <see cref="IReadOnlyList{T}"/> and neither generic dictionary interface.</param>
    /// <param name="ElementType">The list interface's type argument, when <paramref name="IsList"/> is set.</param>
    private sealed record ListShape(bool IsList, Type? ElementType);

    private static readonly ListShape NotAList = new(false, null);

    // Weak on the type, so a collectible load context that defines a list type can still unload.
    // The table settles at the few collection types an app binds.
    private static readonly ConditionalWeakTable<Type, ListShape> ListShapes = new();

    /// <summary>The list shape of <paramref name="type"/>, read from its interfaces on the first ask and remembered.</summary>
    /// <param name="type">A collection type.</param>
    /// <returns>The shape; <see cref="NotAList"/> for a dictionary or a collection with no generic list interface.</returns>
    private static ListShape ListShapeOf(Type type) => ListShapes.GetValue(type, ReadListShape);

    /// <summary>Reads <paramref name="type"/>'s interfaces for a generic list interface and the generic dictionary interfaces.</summary>
    /// <param name="type">A collection type.</param>
    /// <returns>The shape the interfaces give.</returns>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2070",
        Justification = "The type is the runtime type of a live instance. The trimmer keeps an interface " +
            "implementation on a type the app constructs whenever that interface is used, and the " +
            "typeof tests here use IList<>, IReadOnlyList<>, IDictionary<,> and IReadOnlyDictionary<,>. " +
            "An implementation removed anyway reads as no list, so the element keeps its brackets, " +
            "as any collection that is not a list does, and nothing throws.")]
    private static ListShape ReadListShape(Type type)
    {
        var shape = NotAList;
        foreach (var candidate in type.GetInterfaces())
        {
            if (!candidate.IsGenericType)
            {
                continue;
            }

            var definition = candidate.GetGenericTypeDefinition();
            if (definition == typeof(IDictionary<,>) || definition == typeof(IReadOnlyDictionary<,>))
            {
                return NotAList;
            }

            if (!shape.IsList && (definition == typeof(IList<>) || definition == typeof(IReadOnlyList<>)))
            {
                shape = new ListShape(true, candidate.GetGenericArguments()[0]);
            }
        }

        return shape;
    }

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
