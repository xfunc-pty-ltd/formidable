namespace Formidable.Introspection;

/// <summary>Resolves property paths, as a validator or a server reply reports them, against a live object graph; the seam a consumer replaces to publish with full assembly trimming.</summary>
/// <remarks>
/// Implementing this interface is supported; a member added later carries a default
/// implementation, so an implementation written against these members keeps compiling.
/// <see cref="FormidableServiceCollectionExtensions.AddFormidable"/> registers
/// <see cref="ReflectionModelIntrospector"/> only when no implementation is already registered,
/// so a replacement is registered before that call.
/// </remarks>
// A member added later answers "cannot be read", the report TryReadValue already gives for a
// member it cannot find, which every caller treats as a reason to claim nothing rather than as a
// value; an implementation that does not override the addition claims nothing it did not read.
public interface IModelIntrospector
{
    /// <summary>Resolves <paramref name="propertyPath"/> against <paramref name="rootModel"/> to the deepest non-null owner on the path and the member name on it.</summary>
    /// <param name="rootModel">The model the path starts from.</param>
    /// <param name="propertyPath">A FluentValidation property path; empty for the model-level field.</param>
    /// <returns>The owner and the member name; when the walk stops early, the deepest non-null owner with the rest of the path as the member name.</returns>
    /// <remarks>
    /// A null or missing intermediate value (the usual state for a failing required-value rule)
    /// stops the walk there. An empty <paramref name="propertyPath"/> resolves to
    /// <paramref name="rootModel"/> with an empty member name.
    /// </remarks>
    ResolvedField Resolve(object rootModel, string propertyPath);

    /// <summary>Reads <paramref name="propertyName"/> on <paramref name="owner"/>, with the type the member declares rather than the type its value has.</summary>
    /// <param name="owner">The instance to read from, a <see cref="ResolvedField.Owner"/>.</param>
    /// <param name="propertyName">The member name on <paramref name="owner"/>, a <see cref="ResolvedField.PropertyName"/>.</param>
    /// <param name="value">The value read, or <see langword="null"/> when the read failed.</param>
    /// <param name="declaredType">The member's declared type, or <see langword="null"/> when the read failed.</param>
    /// <returns><see langword="true"/> when the member was found and read; <see langword="false"/> when it cannot be read, including when reading it threw.</returns>
    /// <remarks>
    /// The declared type is what tells an <c>int</c> holding its default from an <c>int?</c>
    /// holding the same number. An unresolved remainder such as <c>Address.City</c>, an empty
    /// name, an indexer token, and a name no member matches all report <see langword="false"/>,
    /// meaning the member cannot be read, which a caller keeps apart from a member holding
    /// nothing.
    /// </remarks>
    bool TryReadValue(
        object owner,
        string propertyName,
        out object? value,
        out Type? declaredType);
}
