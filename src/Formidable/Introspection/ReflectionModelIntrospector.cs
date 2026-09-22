using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace Formidable.Introspection;

/// <summary>The reflection-based <see cref="IModelIntrospector"/> that <see cref="FormidableServiceCollectionExtensions.AddFormidable"/> registers by default, caching parsed paths and member lookups.</summary>
/// <remarks>
/// The last segment never navigates: it names the member on whatever owner the walk reached. A
/// segment that cannot be navigated (such as a null value, an unknown member, an out-of-range
/// index, a missing key or a throwing getter) ends the walk at the deepest non-null owner, with
/// the rest of the path as the member name. A malformed path resolves to the root with the whole
/// path as the member name; an empty path, to the root with an empty member name.
/// </remarks>
[RequiresUnreferencedCode("Walks the object graph via reflection; model members must not be trimmed.")]
// A source-generated implementation can replace this behind the interface with no consumer-facing
// change.
public sealed class ReflectionModelIntrospector : IModelIntrospector
{
    /// <summary>How many parsed paths are remembered; once the cap is spent, a path is still parsed and answered, just not cached.</summary>
    // PropertyPath.TryParse is purely syntactic and never touches the model, so neither a
    // successful nor a failed parse is bounded by anything the app itself controls: the Blazor
    // engine hands a server response's issue paths to Resolve unfiltered, and almost any
    // attacker-chosen string parses as a valid single-segment path. A few thousand entries covers
    // even a large virtualized form (hundreds of rows, each legitimately producing its own path,
    // such as "Sessions[437].Title"). Two caps bound the cost together, because an entry costs a
    // copy of the key plus a PathSegment per segment, several bytes per byte of path, so a count
    // alone bounds nothing: this one caps how many paths are remembered, MaxCachedPathLength caps
    // how long one may be to earn an entry, and their product is the ceiling on what is retained.
    private const int MaxCachedPaths = 4096;

    /// <summary>The longest path, or member name, that earns a cache entry; a longer one is answered every time, never cached.</summary>
    // FluentValidation property paths are tens of characters (a deeply nested one with indexed
    // rows is still well inside this), while the paths a server response can carry are whatever
    // that response says they are. Capping the key's length is what stops an unbounded key buying
    // an unbounded entry.
    private const int MaxCachedPathLength = 256;

    /// <summary>How many (declaring type, member name) lookups are remembered, indexers included; once the cap is spent, a lookup is still answered, just not cached.</summary>
    // The key here is only half app-controlled: the type comes from the real object graph, but the
    // member name is read from a path segment, which (like MaxCachedPaths) can arrive from a server
    // response unfiltered. GetProperty simply returns null for a name that does not exist, so
    // walking one real model type against many fabricated names would otherwise grow this cache
    // without bound too. Modest on purpose: the legitimate space here (an app's own model types
    // crossed with their own declared properties) is small and settles early, well under this cap,
    // before any attacker-controlled traffic could push it over. An entry holds a copy of the name,
    // so the name half of the key is capped at MaxCachedPathLength as well, for the reason the path
    // cache pairs its two caps: a count alone bounds nothing when each entry can be as long as the
    // sender likes. No type declares a member that long, and a path segment is never longer than
    // the path it was cut from.
    private const int MaxCachedProperties = 1024;

    private readonly ConcurrentDictionary<(Type Type, string Property), PropertyInfo?> _propertyCache = new();

    // Keyed on the raw path string. A null entry records a path PropertyPath.TryParse
    // rejected. Both cache the parse OUTCOME for a repeat lookup; both refuse a new entry
    // beyond MaxCachedPaths (success or failure) — past the cap, Resolve still parses and
    // answers correctly, it just stops remembering.
    private readonly ConcurrentDictionary<string, IReadOnlyList<PathSegment>?> _pathCache = new();

    // Counts of the entries claimed in _pathCache / _propertyCache, checked against their caps
    // before each new entry is added. Approximate against the dictionaries' own counts — two
    // threads can both claim a slot for the same new key and only one TryAdd lands — which is
    // fine, since a cap only needs to stop unbounded growth, not land on an exact number. Each
    // stops at its cap rather than counting every later miss: an int that kept counting would
    // wrap after 2^31 misses and pass the cap check again.
    private int _cachedPathCount;
    private int _cachedPropertyCount;

    /// <summary>Resolves <paramref name="propertyPath"/> against <paramref name="rootModel"/> by walking public instance properties and indexers, falling back as the class remarks describe.</summary>
    /// <param name="rootModel">The model the path starts from.</param>
    /// <param name="propertyPath">A FluentValidation property path; empty for the model-level field.</param>
    /// <returns>The deepest non-null owner reached and the member name on it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="rootModel"/> or <paramref name="propertyPath"/> is <see langword="null"/>.</exception>
    public ResolvedField Resolve(object rootModel, string propertyPath)
    {
        ArgumentNullException.ThrowIfNull(rootModel);
        ArgumentNullException.ThrowIfNull(propertyPath);

        if (propertyPath.Length == 0)
        {
            // FluentValidation emits an empty PropertyName for model-level rules
            // (RuleFor(x => x), context.AddFailure) — the issue attaches to the root.
            return new ResolvedField(rootModel, string.Empty);
        }

        if (!TryParsePath(propertyPath, out var segments))
        {
            return new ResolvedField(rootModel, propertyPath);
        }

        object current = rootModel;

        for (var i = 0; i < segments.Count - 1; i++)
        {
            var next = Navigate(current, segments[i]);

            if (next is null)
            {
                return new ResolvedField(current, RejoinFrom(segments, i));
            }

            current = next;
        }

        // Terminal segment: never navigate — it names the field on the current owner. A
        // successful parse always yields at least one segment: PropertyPath's grammar requires
        // the first one to be a property (TryParse refuses both a leading '.' and a leading
        // indexer), so segments.Count - 1 never goes negative here.
        return new ResolvedField(current, RejoinFrom(segments, segments.Count - 1));
    }

    /// <summary>Reads the public instance property <paramref name="propertyName"/> on <paramref name="owner"/>, with the type it declares.</summary>
    /// <param name="owner">The instance to read from.</param>
    /// <param name="propertyName">The property name on <paramref name="owner"/>.</param>
    /// <param name="value">The value read; <see langword="null"/> when the read failed.</param>
    /// <param name="declaredType">The property's declared type; <see langword="null"/> when the read failed.</param>
    /// <returns><see langword="true"/> when the property was found and read; <see langword="false"/> for an empty name, a name no public instance property matches, a name more than one declaration matches, or a read that throws.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="owner"/> or <paramref name="propertyName"/> is <see langword="null"/>.</exception>
    public bool TryReadValue(
        object owner,
        string propertyName,
        out object? value,
        out Type? declaredType)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(propertyName);

        value = null;
        declaredType = null;

        if (propertyName.Length == 0)
        {
            return false;
        }

        PropertyInfo? property;
        try
        {
            property = GetOrCacheProperty(owner.GetType(), propertyName);
        }
        catch
        {
            // Ambiguous member (a property hidden via 'new') - unreadable, rather than a guess
            // between two declarations.
            return false;
        }

        if (property is null)
        {
            return false;
        }

        try
        {
            value = property.GetValue(owner);
        }
        catch
        {
            // A throwing getter, or a member whose value cannot be boxed: there is no value to
            // hand back, so the read failed.
            return false;
        }

        declaredType = property.PropertyType;
        return true;
    }

    /// <summary>Parses <paramref name="propertyPath"/> through the path cache, remembering the outcome, success or failure, only under both caps.</summary>
    /// <param name="propertyPath">The path to parse.</param>
    /// <param name="segments">The segments; <see langword="null"/> when the path is malformed.</param>
    /// <returns><see langword="true"/> when the path parsed, whether from the cache or afresh.</returns>
    private bool TryParsePath(string propertyPath, [NotNullWhen(true)] out IReadOnlyList<PathSegment>? segments)
    {
        if (_pathCache.TryGetValue(propertyPath, out segments))
        {
            return segments is not null;
        }

        var parsed = PropertyPath.TryParse(propertyPath, out var result);
        segments = parsed ? result : null;

        if (propertyPath.Length <= MaxCachedPathLength
            && TryClaimCacheSlot(ref _cachedPathCount, MaxCachedPaths))
        {
            _pathCache.TryAdd(propertyPath, segments);
        }

        return parsed;
    }

    /// <summary>Claims one entry of a cache's budget, or answers <see langword="false"/> once the budget is spent; the count stops at <paramref name="cap"/>.</summary>
    /// <param name="count">The entries claimed so far.</param>
    /// <param name="cap">The most entries the cache may hold.</param>
    /// <returns><see langword="true"/> when a slot was claimed.</returns>
    private static bool TryClaimCacheSlot(ref int count, int cap)
    {
        while (true)
        {
            var current = Volatile.Read(ref count);

            if (current >= cap)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref count, current + 1, current) == current)
            {
                return true;
            }
        }
    }

    /// <summary>Advances one non-terminal <paramref name="segment"/> from <paramref name="current"/>, or <see langword="null"/> when it cannot be navigated.</summary>
    /// <param name="current">The object reached so far.</param>
    /// <param name="segment">The property or indexer segment to navigate.</param>
    /// <returns>The object the segment reaches; <see langword="null"/> for a segment such as a null value, a missing member, a throwing getter, an out-of-range index or a missing key, which <see cref="Resolve"/> answers with <paramref name="current"/> as the owner.</returns>
    private object? Navigate(object current, PathSegment segment) =>
        segment.IsIndexer
            ? GetIndexedValue(current, segment.IndexToken!)
            : GetPropertyValue(current, segment.PropertyName!);

    private object? GetPropertyValue(object instance, string propertyName)
    {
        try
        {
            var property = GetOrCacheProperty(instance.GetType(), propertyName);

            return property?.GetValue(instance);
        }
        catch
        {
            // Ambiguous member (property hidden via 'new'), throwing getter — treat as
            // unresolvable and let Resolve fall back to the deepest non-null owner.
            return null;
        }
    }

    /// <summary>Looks <paramref name="propertyName"/> up on <paramref name="type"/> through the property cache, remembering the answer only under both caps.</summary>
    /// <param name="type">The owner's type.</param>
    /// <param name="propertyName">The property name to find.</param>
    /// <returns>The public instance property, or <see langword="null"/> when none matches.</returns>
    /// <exception cref="AmbiguousMatchException">More than one property on <paramref name="type"/> has the name, which the callers read as a member that cannot be read.</exception>
    private PropertyInfo? GetOrCacheProperty(Type type, string propertyName)
    {
        var key = (type, propertyName);

        if (_propertyCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var property = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);

        // The length test comes first, so an over-long name spends none of the count budget.
        if (propertyName.Length <= MaxCachedPathLength
            && TryClaimCacheSlot(ref _cachedPropertyCount, MaxCachedProperties))
        {
            _propertyCache.TryAdd(key, property);
        }

        return property;
    }

    private object? GetIndexedValue(object collection, string indexToken)
    {
        var isNumeric = int.TryParse(indexToken, out var index);

        if (isNumeric && collection is IList list)
        {
            try
            {
                return index >= 0 && index < list.Count ? list[index] : null;
            }
            catch
            {
                // An IList that refuses the read anyway: System.Array implements it whatever
                // its rank and throws from this[int] past rank one, a default ImmutableArray<T>
                // is a boxed value rather than a null and throws from Count, and a consumer's
                // own list may do either. Fall back on the deepest owner, as the class contract
                // promises for every other navigation failure.
                return null;
            }
        }

        return TryIndexerProperty(collection, indexToken, isNumeric);
    }

    private object? TryIndexerProperty(object collection, string indexToken, bool isNumeric)
    {
        try
        {
            var indexer = GetOrCacheIndexer(collection.GetType(), isNumeric);

            if (indexer is null)
            {
                return null;
            }

            var parameterType = indexer.GetIndexParameters()[0].ParameterType;
            var key = Convert.ChangeType(indexToken, parameterType);
            return indexer.GetValue(collection, [key]);
        }
        catch
        {
            // Missing key, wrong key type, or index conversion failure — fall back.
            return null;
        }
    }

    /// <summary>Looks up the indexer the token's kind asks for through the property cache, under a key no member name can collide with and counted against <see cref="MaxCachedProperties"/> as any member is.</summary>
    /// <param name="type">The collection's type.</param>
    /// <param name="numericToken">Whether the token parsed as an <see cref="int"/>.</param>
    /// <returns>The indexer; <see langword="null"/> when <see cref="FindIndexer"/> picks none.</returns>
    private PropertyInfo? GetOrCacheIndexer(Type type, bool numericToken)
    {
        var key = (type, numericToken ? "this[int]" : "this[string]");

        if (_propertyCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var indexer = FindIndexer(type, numericToken);

        if (TryClaimCacheSlot(ref _cachedPropertyCount, MaxCachedProperties))
        {
            _propertyCache.TryAdd(key, indexer);
        }

        return indexer;
    }

    /// <summary>Picks the indexer a token addresses: the one whose parameter type matches exactly, else the one that accepts it by assignment, else a type's only indexer.</summary>
    /// <param name="type">The collection's type.</param>
    /// <param name="numericToken">Whether the token parsed as an <see cref="int"/>, which asks for an <see cref="int"/> indexer; otherwise a <see cref="string"/> one.</param>
    /// <returns>The chosen indexer; <see langword="null"/> when the first step with a candidate finds more than one, or no step finds any, so navigation falls back on the deepest owner.</returns>
    // Indexers overload, so a type declaring more than one (JsonObject and JsonArray, as every
    // JsonNode declares both; NameValueCollection; the non-generic OrderedDictionary; a consumer's
    // own keyed bag) makes GetProperty("Item") ambiguous, and the token itself is what resolves it.
    // The assignable step is for an object-keyed indexer, which is what OrderedDictionary declares;
    // the only-indexer step lets a Guid-keyed dictionary reach the conversion that decides it.
    // Anything still ambiguous gets the answer a member hidden by new gets.
    private static PropertyInfo? FindIndexer(Type type, bool numericToken)
    {
        var wanted = numericToken ? typeof(int) : typeof(string);
        PropertyInfo? exact = null;
        PropertyInfo? assignable = null;
        PropertyInfo? only = null;
        var exactCount = 0;
        var assignableCount = 0;
        var totalCount = 0;

        foreach (var candidate in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var parameters = candidate.GetIndexParameters();

            if (parameters.Length != 1)
            {
                continue;
            }

            totalCount++;
            only = candidate;
            var parameterType = parameters[0].ParameterType;

            if (parameterType == wanted)
            {
                exactCount++;
                exact = candidate;
            }
            else if (parameterType.IsAssignableFrom(wanted))
            {
                assignableCount++;
                assignable = candidate;
            }
        }

        if (exactCount == 1)
        {
            return exact;
        }

        if (exactCount == 0 && assignableCount == 1)
        {
            return assignable;
        }

        return totalCount == 1 ? only : null;
    }

    private static string RejoinFrom(IReadOnlyList<PathSegment> segments, int start)
    {
        if (start == segments.Count - 1)
        {
            // Dominant case: the terminal segment never navigates, so most Resolve calls land
            // here with exactly one segment left — return its name with no StringBuilder.
            var terminal = segments[start];
            return terminal.IsIndexer ? $"[{terminal.IndexToken}]" : terminal.PropertyName!;
        }

        var builder = new System.Text.StringBuilder();

        for (var i = start; i < segments.Count; i++)
        {
            var segment = segments[i];

            if (segment.IsIndexer)
            {
                builder.Append('[').Append(segment.IndexToken).Append(']');
            }
            else
            {
                if (builder.Length > 0)
                {
                    builder.Append('.');
                }

                builder.Append(segment.PropertyName);
            }
        }

        return builder.ToString();
    }
}
