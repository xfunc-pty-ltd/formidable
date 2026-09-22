using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

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
    private readonly ConcurrentDictionary<(Type Type, string Property), PropertyInfo?> _propertyCache = new();

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

        if (!PropertyPath.TryParse(propertyPath, out var segments))
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
            var property = _propertyCache.GetOrAdd(
                (instance.GetType(), propertyName),
                static key => key.Type.GetProperty(key.Property, BindingFlags.Public | BindingFlags.Instance));

            return property?.GetValue(instance);
        }
        catch
        {
            // Ambiguous member (property hidden via 'new'), throwing getter — treat as
            // unresolvable and let Resolve fall back to the deepest non-null owner.
            return null;
        }
    }

    private static object? GetIndexedValue(object collection, string indexToken)
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

    private static object? TryIndexerProperty(object collection, string indexToken)
    {
        var indexer = collection.GetType().GetProperty("Item", BindingFlags.Public | BindingFlags.Instance);
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
