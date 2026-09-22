namespace Formidable.Introspection;

/// <summary>One segment of a property path: a property name or an indexer token.</summary>
// Grows by properties behind the existing factories or new ones, never by changes to a factory's
// shape; any added member folds into the record's synthesized equality.
public readonly record struct PathSegment
{
    private PathSegment(string? propertyName, string? indexToken)
    {
        PropertyName = propertyName;
        IndexToken = indexToken;
    }

    /// <summary>The property name; <see langword="null"/> for an indexer segment.</summary>
    public string? PropertyName { get; }

    /// <summary>The raw indexer token, a numeric index or a dictionary key; <see langword="null"/> for a property segment.</summary>
    public string? IndexToken { get; }

    /// <summary>Whether this segment is an indexer.</summary>
    public bool IsIndexer => IndexToken is not null;

    /// <summary>Creates a property segment.</summary>
    /// <param name="name">The property name.</param>
    /// <returns>The segment.</returns>
    public static PathSegment Property(string name) => new(name, null);

    /// <summary>Creates an indexer segment from its raw token.</summary>
    /// <param name="token">The text between the brackets, a numeric index or a dictionary key.</param>
    /// <returns>The segment.</returns>
    public static PathSegment Indexer(string token) => new(null, token);
}
