namespace Formidable.Shared;

/// <summary>Finds the closed form of an open generic class among a type and its base types, to read a type argument off a live object.</summary>
// Base types alone, never interfaces: a base type is read without a trimming warning, where an
// interface walk is flagged (IL2075) in these AOT-compatible packages. Nothing is built; each type
// is compared with the open one, which every caller names by a literal typeof.
// Compiled into Formidable and Formidable.Blazor from this one shared source file, so each
// assembly gets its own internal copy rather than a shared reference.
internal static class ClosedGenericType
{
    /// <summary>The closed form of <paramref name="openType"/> that <paramref name="type"/> is or derives from.</summary>
    /// <param name="type">The type to start from, usually a live object's own type.</param>
    /// <param name="openType">The open generic class.</param>
    /// <returns>The closed type, or <see langword="null"/> when neither the type nor any of its base types is a form of <paramref name="openType"/>.</returns>
    internal static Type? Of(Type type, Type openType)
    {
        for (var candidate = type; candidate is not null; candidate = candidate.BaseType)
        {
            if (candidate.IsGenericType && candidate.GetGenericTypeDefinition() == openType)
            {
                return candidate;
            }
        }

        return null;
    }
}
