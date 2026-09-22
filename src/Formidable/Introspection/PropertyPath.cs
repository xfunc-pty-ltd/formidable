namespace Formidable.Introspection;

/// <summary>Parses FluentValidation property paths (e.g. <c>LineItems[0].Sku</c>) into segments.</summary>
/// <remarks>
/// Accepted grammar:
/// <code>
///   path      := property ( '.' property | '.'? indexer )*
///   property  := any run of characters except '.', '[', ']'
///   indexer   := '[' token ']'   — numeric index or dictionary key; consecutive
///                indexers chain without a separating dot (e.g. "A[0][1]"), and a
///                redundant '.' before an indexer is accepted (e.g. "A.[0]")
/// </code>
/// Rejected: blank paths, leading/trailing/doubled dots, unclosed or empty brackets,
/// a leading indexer, ']' inside a name, and a property directly after ']' without a dot.
/// </remarks>
public static class PropertyPath
{
    /// <summary>
    /// Parses <paramref name="path"/> into segments. Returns false for malformed paths
    /// (blank, unbalanced brackets, empty segments, missing separators) — callers fall back to
    /// treating the whole path as a single property name.
    /// </summary>
    public static bool TryParse(string path, out IReadOnlyList<PathSegment> segments)
    {
        segments = [];

        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var result = new List<PathSegment>();
        var remaining = path.AsSpan();
        var expectSeparator = false; // true right after a ']' — only '.', '[' or end may follow

        while (remaining.Length > 0)
        {
            if (remaining[0] == '.')
            {
                if (!TryConsumeSeparator(ref remaining, result.Count))
                {
                    return false;
                }

                expectSeparator = false;
                continue;
            }

            if (remaining[0] == '[')
            {
                if (!TryConsumeIndexer(ref remaining, result))
                {
                    return false;
                }

                expectSeparator = true;
                continue;
            }

            if (expectSeparator)
            {
                return false; // property name directly after ']' without '.'
            }

            if (!TryConsumeProperty(ref remaining, result))
            {
                return false;
            }
        }

        segments = result;
        return true;
    }

    /// <summary>Consumes a single '.' separator. Rejects a leading, trailing, or doubled dot.</summary>
    private static bool TryConsumeSeparator(ref ReadOnlySpan<char> remaining, int segmentsSoFar)
    {
        if (segmentsSoFar == 0)
        {
            return false; // leading dot
        }

        remaining = remaining[1..];

        if (remaining.Length == 0 || remaining[0] == '.')
        {
            return false; // trailing or doubled dot
        }

        return true;
    }

    /// <summary>Consumes a '[token]' indexer and appends it to <paramref name="result"/>.</summary>
    private static bool TryConsumeIndexer(ref ReadOnlySpan<char> remaining, List<PathSegment> result)
    {
        var close = remaining.IndexOf(']');
        if (close <= 1 || result.Count == 0)
        {
            return false; // unclosed, empty, or leading indexer
        }

        result.Add(PathSegment.Indexer(remaining[1..close].ToString()));
        remaining = remaining[(close + 1)..];
        return true;
    }

    /// <summary>Consumes a bare property name up to the next '.', '[', or end of input.</summary>
    private static bool TryConsumeProperty(ref ReadOnlySpan<char> remaining, List<PathSegment> result)
    {
        // One scan for either separator. Searching for each in turn re-reads the whole
        // remainder whenever one of them is absent, which makes a bracket-free path cost a
        // scan per segment — quadratic in the length of a path the caller does not control.
        var next = remaining.IndexOfAny('.', '[');
        var end = next < 0 ? remaining.Length : next;

        if (remaining[..end].IndexOf(']') >= 0)
        {
            return false; // stray close bracket inside a name
        }

        result.Add(PathSegment.Property(remaining[..end].ToString()));
        remaining = remaining[end..];
        return true;
    }
}
