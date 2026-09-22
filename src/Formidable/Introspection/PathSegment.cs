namespace Formidable.Introspection;

/// <summary>One segment of a property path: either a property name or an indexer token.</summary>
/// <remarks>Grows by properties behind the existing factories or new ones, never by changes to
/// a factory's shape; any added member folds into the record's synthesized equality.</remarks>
public readonly record struct PathSegment
{
    private PathSegment(string? propertyName, string? indexToken)
    {
        PropertyName = propertyName;
        IndexToken = indexToken;
    }

    /// <summary>The property name; null for indexer segments.</summary>
    public string? PropertyName { get; }

    /// <summary>The raw indexer token (numeric index or dictionary key); null for property segments.</summary>
    public string? IndexToken { get; }

    /// <summary>True when this segment is an indexer.</summary>
    public bool IsIndexer => IndexToken is not null;

    /// <summary>Creates a property segment.</summary>
    public static PathSegment Property(string name) => new(name, null);

    /// <summary>Creates an indexer segment from its raw token.</summary>
    public static PathSegment Indexer(string token) => new(null, token);
}
