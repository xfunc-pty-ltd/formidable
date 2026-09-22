namespace Formidable.Introspection;

/// <summary>Parses a FluentValidation property path such as <c>LineItems[0].Sku</c> into segments.</summary>
/// <remarks>
/// Accepted grammar:
/// <code>
///   path      := property ( '.' property | '.'? indexer )*
///   property  := any run of characters except '.', '[', ']'
///   indexer   := '[' token ']'   (a numeric index or a dictionary key)
/// </code>
/// Consecutive indexers chain without a dot (<c>A[0][1]</c>), and a redundant dot before an
/// indexer is accepted (<c>A.[0]</c>). Rejected: a blank path, a leading, trailing or doubled dot,
/// an unclosed or empty bracket pair, a leading indexer, ']' inside a name, and a property
/// directly after ']' without a dot.
/// </remarks>
public static class PropertyPath
{
    /// <summary>Parses <paramref name="path"/> into segments, returning <see langword="false"/> for a malformed path.</summary>
    /// <param name="path">The property path.</param>
    /// <param name="segments">The segments in order; empty when the parse fails.</param>
    /// <returns><see langword="true"/> when <paramref name="path"/> matches the grammar in the class remarks.</returns>
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

    /// <summary>Consumes one '.' separator, rejecting a leading, trailing or doubled dot.</summary>
    /// <param name="remaining">The unparsed rest of the path, starting at the dot.</param>
    /// <param name="segmentsSoFar">How many segments precede the dot; zero makes it a leading dot.</param>
    /// <returns><see langword="true"/> when the dot was consumed and something other than a dot follows it.</returns>
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
    /// <param name="remaining">The unparsed rest of the path, starting at the '['.</param>
    /// <param name="result">The segments parsed so far; empty makes this a leading indexer, which is rejected.</param>
    /// <returns><see langword="true"/> when a closed, non-empty bracket pair was consumed and it is not the first segment.</returns>
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

    /// <summary>Consumes a bare property name up to the next '.', '[' or the end of the input.</summary>
    /// <param name="remaining">The unparsed rest of the path, starting at the name.</param>
    /// <param name="result">The segments parsed so far, which the name is appended to.</param>
    /// <returns><see langword="true"/> when the name holds no ']'.</returns>
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
