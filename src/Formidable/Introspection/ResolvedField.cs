namespace Formidable.Introspection;

/// <summary>The owning instance and member name a property path resolved to.</summary>
/// <remarks>Grows by init-only properties, never by constructor parameters, so existing
/// construction keeps compiling and binding; any added member folds into the record's
/// synthesized equality.</remarks>
/// <param name="Owner">The deepest non-null object reached along the path.</param>
/// <param name="PropertyName">The member name on <paramref name="Owner"/> — the remaining
/// (unresolved) path when the walk stopped early.</param>
public readonly record struct ResolvedField(object Owner, string PropertyName);
