namespace Formidable.Shared;

/// <summary>
/// Renders a runtime type's name the way a developer wrote it, for diagnostics — <c>Type.Name</c>
/// keeps the CLR's generic-arity suffix, so a generic type reporting itself with
/// <c>GetType().Name</c> would say "DelegatingModelValidator`1" rather than
/// "DelegatingModelValidator". Compiled into <c>Formidable</c>, <c>Formidable.Blazor</c> and
/// <c>Formidable.AspNetCore</c> from this one shared source file, so each assembly gets its own
/// <see langword="internal"/> copy rather than a shared reference.
/// </summary>
internal static class FriendlyTypeName
{
    /// <summary>The type's name with any generic-arity suffix trimmed off.</summary>
    internal static string Of(Type type)
    {
        var name = type.Name;
        var arity = name.IndexOf('`', StringComparison.Ordinal);
        return arity < 0 ? name : name[..arity];
    }
}
