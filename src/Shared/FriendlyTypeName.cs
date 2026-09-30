namespace Formidable.Shared;

/// <summary>Renders a type's name without the CLR's generic-arity suffix, and a nullable value type as the type it holds plus <c>?</c>, for diagnostics.</summary>
// Type.Name keeps the CLR's generic-arity suffix, so a generic type reporting itself with
// GetType().Name would say "DelegatingModelValidator`1" rather than "DelegatingModelValidator".
// Cut at the backtick alone, every nullable value type would read "Nullable" and hide the type
// it holds, so a Nullable<T> is named by that type with the "?" C# marks it with.
// Compiled into Formidable, Formidable.Blazor and Formidable.AspNetCore from this one shared
// source file, so each assembly gets its own internal copy rather than a shared reference.
internal static class FriendlyTypeName
{
    /// <summary>The type's name with any generic-arity suffix trimmed off; a <see cref="Nullable{T}"/> is named by the type it holds, followed by <c>?</c>.</summary>
    /// <param name="type">The type to name.</param>
    /// <returns>The name up to the first backtick, or the whole name when there is none; <c>Int32?</c> for <c>int?</c>.</returns>
    internal static string Of(Type type)
    {
        if (Nullable.GetUnderlyingType(type) is { } held)
        {
            return Of(held) + "?";
        }

        var name = type.Name;
        var arity = name.IndexOf('`', StringComparison.Ordinal);
        return arity < 0 ? name : name[..arity];
    }
}
