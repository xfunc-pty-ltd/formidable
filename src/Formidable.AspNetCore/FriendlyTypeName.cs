namespace Formidable.AspNetCore;

/// <summary>
/// Renders a runtime type's name the way a developer wrote it, for diagnostics —
/// <c>Type.Name</c> keeps the CLR generic-arity suffix ("Order`1").
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
