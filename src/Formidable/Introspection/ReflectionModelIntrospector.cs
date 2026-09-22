using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Threading;

namespace Formidable.Introspection;

/// <summary>
/// Reflection-based <see cref="IModelIntrospector"/> with per-(type, property) caching.
/// A source-generated implementation can replace this behind the interface without any
/// consumer-facing change.
/// </summary>
/// <remarks>
/// Fallback contract for <see cref="Resolve"/>: the terminal segment never navigates — it
/// always names the field on whatever owner the walk has reached so far. A failure to navigate
/// an intermediate segment (null value, unknown member, out-of-range index, missing key,
/// throwing getter) returns the deepest non-null owner reached along the way, paired with the
/// remaining (unresolved) path rejoined from that point. An empty <c>propertyPath</c>
/// as passed to <see cref="Resolve"/> resolves to the model root with an empty property name. A
/// malformed path (one <see cref="PropertyPath.TryParse"/> rejects) resolves to the model root
/// with the entire original path as the property name.
/// </remarks>
[RequiresUnreferencedCode("Walks the object graph via reflection; model members must not be trimmed.")]
public sealed class ReflectionModelIntrospector : IModelIntrospector
{
    // PropertyPath.TryParse is purely syntactic — it never touches the model — so neither a
    // successful nor a failed parse is naturally bounded by anything the app itself controls:
    // FormValidationEngine.Resolve calls Resolve with a server response's issue paths
    // verbatim, and almost any attacker-chosen string parses as a valid single-segment path.
    // A few thousand entries comfortably covers even a large virtualized form (hundreds of
    // rows, each legitimately producing its own path, e.g. "Sessions[437].Title") at a
    // bounded worst-case cost of a few hundred KB (a short string key plus a handful of
    // PathSegment structs per entry) — see MaxCachedPaths below for where the cap applies.
    private const int MaxCachedPaths = 4096;

    // The (declaring type, member name) key here is only half app-controlled: the type comes
    // from the real object graph, but the member name is read from a path segment, which (like
    // MaxCachedPaths above) can arrive from a server response unfiltered — GetProperty simply
    // returns null for a name that doesn't exist, so walking one real model type against many
    // fabricated names would otherwise grow this cache without bound too. Modest on purpose:
    // the legitimate space here — an app's own model types crossed with their own declared
    // properties — is small and settles early, well under this cap, before any
    // attacker-controlled traffic could push it over.
    private const int MaxCachedProperties = 1024;

    private readonly ConcurrentDictionary<(Type Type, string Property), PropertyInfo?> _propertyCache = new();

    // Keyed on the raw path string. A null entry records a path PropertyPath.TryParse
    // rejected. Both cache the parse OUTCOME for a repeat lookup; both refuse a new entry
    // beyond MaxCachedPaths (success or failure) — past the cap, Resolve still parses and
    // answers correctly, it just stops remembering.
    private readonly ConcurrentDictionary<string, IReadOnlyList<PathSegment>?> _pathCache = new();

    // Approximate counts of entries added to _pathCache / _propertyCache, checked against
    // their caps before each new entry is added. Interlocked, not exact under a race (two
    // threads can both pass the check for the same new key), which is fine — a cap only needs
    // to stop unbounded growth, not land on an exact number.
    private int _cachedPathCount;
    private int _cachedPropertyCount;

    /// <inheritdoc />
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

        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            var isLast = i == segments.Count - 1;

            if (isLast)
            {
                // Terminal segment: never navigate — it names the field on the current owner.
                return new ResolvedField(current, RejoinFrom(segments, i));
            }

            var next = Navigate(current, segment);

            if (next is null)
            {
                return new ResolvedField(current, RejoinFrom(segments, i));
            }

            current = next;
        }

        return new ResolvedField(rootModel, propertyPath); // unreachable for non-empty parses
    }

    /// <inheritdoc />
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

    /// <summary>
    /// Parses <paramref name="propertyPath"/> via the path cache. A cache hit (success or
    /// previously-cached failure) short-circuits <see cref="PropertyPath.TryParse"/> entirely.
    /// A cache miss is always parsed to answer this call, but is only added to the cache below
    /// <see cref="MaxCachedPaths"/> — beyond the cap it's still parsed and answered correctly
    /// every time, just not remembered.
    /// </summary>
    private bool TryParsePath(string propertyPath, [NotNullWhen(true)] out IReadOnlyList<PathSegment>? segments)
    {
        if (_pathCache.TryGetValue(propertyPath, out segments))
        {
            return segments is not null;
        }

        var parsed = PropertyPath.TryParse(propertyPath, out var result);
        segments = parsed ? result : null;

        if (Interlocked.Increment(ref _cachedPathCount) <= MaxCachedPaths)
        {
            _pathCache.TryAdd(propertyPath, segments);
        }

        return parsed;
    }

    /// <summary>
    /// Advances from <paramref name="current"/> across one non-terminal <paramref name="segment"/>.
    /// Returns null when the segment can't be navigated (missing property, throwing getter,
    /// out-of-range index, missing key) — the caller falls back to <paramref name="current"/>
    /// as the deepest resolved owner.
    /// </summary>
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

    /// <summary>
    /// Looks up <paramref name="propertyName"/> on <paramref name="type"/> via the property
    /// cache. A cache hit short-circuits reflection entirely; a cache miss is always reflected
    /// to answer this call, but is only added to the cache below
    /// <see cref="MaxCachedProperties"/> — beyond the cap the lookup still answers correctly
    /// every time, just not remembered.
    /// </summary>
    private PropertyInfo? GetOrCacheProperty(Type type, string propertyName)
    {
        var key = (type, propertyName);

        if (_propertyCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var property = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);

        if (Interlocked.Increment(ref _cachedPropertyCount) <= MaxCachedProperties)
        {
            _propertyCache.TryAdd(key, property);
        }

        return property;
    }

    private object? GetIndexedValue(object collection, string indexToken)
    {
        if (int.TryParse(indexToken, out var index))
        {
            return collection switch
            {
                IList list when index >= 0 && index < list.Count => list[index],
                IList => null,
                _ => TryIndexerProperty(collection, indexToken)
            };
        }

        return TryIndexerProperty(collection, indexToken);
    }

    private object? TryIndexerProperty(object collection, string indexToken)
    {
        var indexer = GetOrCacheProperty(collection.GetType(), "Item");

        if (indexer is null)
        {
            return null;
        }

        try
        {
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
