namespace Formidable.Shared;

/// <summary>Renders a type's name without the CLR's generic-arity suffix, for diagnostics.</summary>
// Type.Name keeps the CLR's generic-arity suffix, so a generic type reporting itself with
// GetType().Name would say "DelegatingModelValidator`1" rather than "DelegatingModelValidator".
// Compiled into Formidable, Formidable.Blazor and Formidable.AspNetCore from this one shared
// source file, so each assembly gets its own internal copy rather than a shared reference.
internal static class FriendlyTypeName
{
    /// <summary>The type's name with any generic-arity suffix trimmed off.</summary>
    /// <param name="type">The type to name.</param>
    /// <returns>The name up to the first backtick, or the whole name when there is none.</returns>
    internal static string Of(Type type)
    {
        var name = type.Name;
        var arity = name.IndexOf('`', StringComparison.Ordinal);
        return arity < 0 ? name : name[..arity];
    }
}
