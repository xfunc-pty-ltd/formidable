namespace Formidable.Introspection;

/// <summary>Resolves validator property paths against a live object graph.</summary>
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
}
