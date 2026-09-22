namespace Formidable.Introspection;

/// <summary>Resolves validator property paths against a live object graph.</summary>
/// <remarks>
/// A swappable seam — <c>AddFormidable</c> registers the reflection-based implementation only
/// when nothing else is, which is how a fully trimmed publish supplies its own — and it grows
/// accordingly: a member added after v1 carries a default implementation answering "this
/// cannot be read", the report <see cref="TryReadValue"/> already gives for a member it cannot
/// find, which every caller treats as a reason to claim nothing rather than as a value. An
/// implementation that does not override the addition therefore claims nothing it did not
/// read.
/// </remarks>
public interface IModelIntrospector
{
    /// <summary>
    /// Resolves <paramref name="propertyPath"/> against <paramref name="rootModel"/>.
    /// When an intermediate value is null or missing (the usual state for a failing
    /// required-value rule), returns the deepest non-null owner with the remaining path as the
    /// property name. An empty <paramref name="propertyPath"/> denotes a model-level issue and
    /// resolves to <paramref name="rootModel"/> itself with an empty property name.
    /// </summary>
    ResolvedField Resolve(object rootModel, string propertyPath);

    /// <summary>
    /// Reads the value of <paramref name="propertyName"/> on <paramref name="owner"/>, together
    /// with the type the member DECLARES rather than the type the value happens to have. The
    /// declared type is what tells a <see langword="null"/> from a never-assigned
    /// <see langword="int"/>: both arrive here as an absence, and only the declaration says
    /// which absence it is.
    /// </summary>
    /// <param name="owner">The instance to read from — a <see cref="ResolvedField.Owner"/>.</param>
    /// <param name="propertyName">
    /// The member name on <paramref name="owner"/> — a <see cref="ResolvedField.PropertyName"/>.
    /// </param>
    /// <param name="value">The value read, or <see langword="null"/> when the read failed.</param>
    /// <param name="declaredType">
    /// The type <paramref name="propertyName"/> is declared as, or <see langword="null"/> when
    /// the read failed.
    /// </param>
    /// <remarks>
    /// The pairing with <see cref="Resolve"/> is deliberate: a caller resolves a validator path
    /// to an owner and a member, then reads that member. A member name that is really an
    /// unresolved remainder (<c>Address.City</c> where <c>Address</c> was null), an empty name
    /// (the model-level rule shape), an indexer token, and a name no member matches all report
    /// <see langword="false"/> — "this cannot be read", which is a different answer from "this
    /// holds nothing" and callers must not merge the two.
    /// </remarks>
    /// <returns>
    /// <see langword="true"/> when the member was found and read; <see langword="false"/>
    /// otherwise, including when reading it threw.
    /// </returns>
    bool TryReadValue(
        object owner,
        string propertyName,
        out object? value,
        out Type? declaredType);
}
