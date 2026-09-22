using System.Collections.Concurrent;
using System.Reflection;
using Formidable.Introspection;
using Formidable.Tests.Fixtures;

namespace Formidable.Tests;

public class ReflectionModelIntrospectorTests
{
    private readonly ReflectionModelIntrospector _introspector = new();

    /// <summary>Reads the introspector's private path-parse cache via reflection, for tests only.</summary>
    private static bool TryGetCachedParse(
        ReflectionModelIntrospector introspector, string path, out IReadOnlyList<PathSegment>? segments)
    {
        var field = typeof(ReflectionModelIntrospector)
            .GetField("_pathCache", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var cache = (ConcurrentDictionary<string, IReadOnlyList<PathSegment>?>)field.GetValue(introspector)!;
        return cache.TryGetValue(path, out segments);
    }

    private static int GetPathCacheCount(ReflectionModelIntrospector introspector)
    {
        var field = typeof(ReflectionModelIntrospector)
            .GetField("_pathCache", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var cache = (ConcurrentDictionary<string, IReadOnlyList<PathSegment>?>)field.GetValue(introspector)!;
        return cache.Count;
    }

    private static int GetMaxCachedPaths()
    {
        var field = typeof(ReflectionModelIntrospector)
            .GetField("MaxCachedPaths", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (int)field.GetRawConstantValue()!;
    }

    private static int GetMaxCachedProperties()
    {
        var field = typeof(ReflectionModelIntrospector)
            .GetField("MaxCachedProperties", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (int)field.GetRawConstantValue()!;
    }

    private static int GetPropertyCacheCount(ReflectionModelIntrospector introspector)
    {
        var field = typeof(ReflectionModelIntrospector)
            .GetField("_propertyCache", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var cache = (ConcurrentDictionary<(Type, string), PropertyInfo?>)field.GetValue(introspector)!;
        return cache.Count;
    }

    private static bool TryGetCachedProperty(
        ReflectionModelIntrospector introspector, Type type, string propertyName, out PropertyInfo? property)
    {
        var field = typeof(ReflectionModelIntrospector)
            .GetField("_propertyCache", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var cache = (ConcurrentDictionary<(Type, string), PropertyInfo?>)field.GetValue(introspector)!;
        return cache.TryGetValue((type, propertyName), out property);
    }

    [Fact]
    public void Empty_path_resolves_to_root_model()
    {
        var order = new TestOrder();

        var field = _introspector.Resolve(order, string.Empty);

        Assert.Same(order, field.Owner);
        Assert.Equal(string.Empty, field.PropertyName);
    }

    [Fact]
    public void Top_level_property_resolves_to_root()
    {
        var order = new TestOrder();

        var field = _introspector.Resolve(order, "Description");

        Assert.Same(order, field.Owner);
        Assert.Equal("Description", field.PropertyName);
    }

    [Fact]
    public void Nested_property_resolves_to_nested_owner()
    {
        var order = new TestOrder { Customer = new TestCustomer { Address = new TestAddress() } };

        var field = _introspector.Resolve(order, "Customer.Address.City");

        Assert.Same(order.Customer.Address, field.Owner);
        Assert.Equal("City", field.PropertyName);
    }

    [Fact]
    public void Indexed_item_resolves_to_the_item()
    {
        var order = new TestOrder { LineItems = [new TestLineItem(), new TestLineItem()] };

        var field = _introspector.Resolve(order, "LineItems[1].Sku");

        Assert.Same(order.LineItems[1], field.Owner);
        Assert.Equal("Sku", field.PropertyName);
    }

    [Fact]
    public void Null_intermediate_falls_back_to_deepest_owner_with_remaining_path()
    {
        var order = new TestOrder { Customer = new TestCustomer { Address = null } };

        var field = _introspector.Resolve(order, "Customer.Address.City");

        Assert.Same(order.Customer, field.Owner);
        Assert.Equal("Address.City", field.PropertyName);
    }

    [Fact]
    public void Null_terminal_parent_returns_property_on_owner()
    {
        var order = new TestOrder { Customer = null };

        var field = _introspector.Resolve(order, "Customer.Name");

        Assert.Same(order, field.Owner);
        Assert.Equal("Customer.Name", field.PropertyName);
    }

    [Fact]
    public void Out_of_range_index_falls_back_with_remaining_path()
    {
        var order = new TestOrder { LineItems = [new TestLineItem()] };

        var field = _introspector.Resolve(order, "LineItems[5].Sku");

        Assert.Same(order.LineItems, field.Owner);
        Assert.Equal("[5].Sku", field.PropertyName);
    }

    [Fact]
    public void Dictionary_key_resolves_when_present_and_falls_back_when_missing()
    {
        var order = new TestOrder { Attributes = { ["colour"] = "red" } };

        var hit = _introspector.Resolve(order, "Attributes[colour]");
        Assert.Same(order.Attributes, hit.Owner);
        Assert.Equal("[colour]", hit.PropertyName);

        var miss = _introspector.Resolve(order, "Attributes[missing]");
        Assert.Same(order.Attributes, miss.Owner);
        Assert.Equal("[missing]", miss.PropertyName);
    }

    [Fact]
    public void Unknown_property_falls_back_with_remaining_path()
    {
        var order = new TestOrder();

        var field = _introspector.Resolve(order, "Nope.Deeper");

        Assert.Same(order, field.Owner);
        Assert.Equal("Nope.Deeper", field.PropertyName);
    }

    [Fact]
    public void Malformed_path_resolves_to_root_with_whole_path()
    {
        var order = new TestOrder();

        var field = _introspector.Resolve(order, "Items[0");

        Assert.Same(order, field.Owner);
        Assert.Equal("Items[0", field.PropertyName);
    }

    private sealed class ThrowingModel
    {
        public ThrowingModel? Child => throw new InvalidOperationException("getter failure");
    }

    [Fact]
    public void Throwing_getter_during_navigation_falls_back_instead_of_throwing()
    {
        var model = new ThrowingModel();

        var field = _introspector.Resolve(model, "Child.Name");

        Assert.Same(model, field.Owner);
        Assert.Equal("Child.Name", field.PropertyName);
    }

    [Fact]
    public void Repeated_resolve_reuses_the_same_parsed_segment_list()
    {
        var order = new TestOrder { Customer = new TestCustomer { Address = new TestAddress() } };

        _introspector.Resolve(order, "Customer.Address.City");
        Assert.True(TryGetCachedParse(_introspector, "Customer.Address.City", out var firstParse));

        _introspector.Resolve(order, "Customer.Address.City");
        Assert.True(TryGetCachedParse(_introspector, "Customer.Address.City", out var secondParse));

        // Same list instance both times: the second Resolve reused the cached parse instead of
        // re-parsing the path string.
        Assert.Same(firstParse, secondParse);
    }

    [Fact]
    public void Unparseable_path_caches_the_failure_and_stays_consistent_across_calls()
    {
        var order = new TestOrder();

        var first = _introspector.Resolve(order, "Items[0");
        var second = _introspector.Resolve(order, "Items[0");

        Assert.Same(order, first.Owner);
        Assert.Equal("Items[0", first.PropertyName);
        Assert.Same(order, second.Owner);
        Assert.Equal("Items[0", second.PropertyName);

        // The failed parse itself is cached (not merely re-derived consistently every call).
        Assert.True(TryGetCachedParse(_introspector, "Items[0", out var cached));
        Assert.Null(cached);
    }

    [Fact]
    public void Path_cache_refuses_new_entries_past_a_total_cap_but_keeps_serving_earlier_hits()
    {
        // PropertyPath.TryParse is syntax-only, so a successful parse is no more bounded than
        // a failed one — FormValidationEngine.Resolve feeds this cache issue paths straight off
        // a server response, so a flood of distinct NEW paths (parseable or not) must not grow
        // the cache without bound, while entries already cached keep serving hits.
        var order = new TestOrder { Customer = new TestCustomer { Address = new TestAddress() } };
        var introspector = new ReflectionModelIntrospector();
        var cap = GetMaxCachedPaths();

        // Primed before the fill: must still serve as a hit once the cap has tripped.
        introspector.Resolve(order, "Customer.Address.City");
        Assert.True(TryGetCachedParse(introspector, "Customer.Address.City", out var primedParse));

        // Fill past the cap with distinct new paths, alternating parseable and unparseable.
        for (var i = 0; i < cap + 50; i++)
        {
            var path = i % 2 == 0 ? $"Field{i}" : $"Items[{i}";
            introspector.Resolve(order, path);
        }

        // (b) the cache stopped growing at the cap (the primed entry counts toward it too).
        Assert.Equal(cap, GetPathCacheCount(introspector));

        // (c) the entry primed before the fill still serves as a hit, same parsed instance.
        var repeatedPrimed = introspector.Resolve(order, "Customer.Address.City");
        Assert.Same(order.Customer.Address, repeatedPrimed.Owner);
        Assert.Equal("City", repeatedPrimed.PropertyName);
        Assert.True(TryGetCachedParse(introspector, "Customer.Address.City", out var primedAfter));
        Assert.Same(primedParse, primedAfter);

        // (a) a NEW parseable path past the cap still resolves correctly, uncached.
        var overCapParseable = $"Field{cap + 49}";
        Assert.False(TryGetCachedParse(introspector, overCapParseable, out _));
        var parseableResult = introspector.Resolve(order, overCapParseable);
        Assert.Same(order, parseableResult.Owner);
        Assert.Equal(overCapParseable, parseableResult.PropertyName);
        Assert.False(TryGetCachedParse(introspector, overCapParseable, out _));

        // (a) a NEW unparseable path past the cap still resolves correctly, uncached.
        var overCapUnparseable = $"Items[{cap + 49}";
        Assert.False(TryGetCachedParse(introspector, overCapUnparseable, out _));
        var unparseableResult = introspector.Resolve(order, overCapUnparseable);
        Assert.Same(order, unparseableResult.Owner);
        Assert.Equal(overCapUnparseable, unparseableResult.PropertyName);
        Assert.False(TryGetCachedParse(introspector, overCapUnparseable, out _));
    }

    [Fact]
    public void Property_cache_refuses_new_entries_past_a_total_cap_but_keeps_serving_earlier_hits()
    {
        // GetProperty is reflected per (declaring type, member name); the member-name half of
        // that key comes from a path segment, which — like the path cache above — can arrive
        // from a server response unfiltered, so walking one real model type with many
        // fabricated property names must not grow this cache without bound either.
        var order = new TestOrder();
        var introspector = new ReflectionModelIntrospector();
        var cap = GetMaxCachedProperties();

        // Primed before the fill: a real (type, property) pair must still serve as a hit.
        introspector.Resolve(order, "Customer.Name"); // "Customer" is segment 0, non-terminal.
        Assert.True(TryGetCachedProperty(introspector, typeof(TestOrder), "Customer", out _));

        // Fill past the cap with distinct new (TestOrder, property-name) pairs.
        for (var i = 0; i < cap + 50; i++)
        {
            introspector.Resolve(order, $"RandomProp{i}.Leaf");
        }

        // (b) the cache stopped growing at the cap.
        Assert.Equal(cap, GetPropertyCacheCount(introspector));

        // (c) the pair primed before the fill still serves as a hit.
        Assert.True(TryGetCachedProperty(introspector, typeof(TestOrder), "Customer", out _));

        // (a) a NEW (type, property) pair past the cap still resolves correctly, uncached.
        var overCapProperty = $"RandomProp{cap + 49}";
        Assert.False(TryGetCachedProperty(introspector, typeof(TestOrder), overCapProperty, out _));
        var result = introspector.Resolve(order, $"{overCapProperty}.Leaf");
        Assert.Same(order, result.Owner);
        Assert.Equal($"{overCapProperty}.Leaf", result.PropertyName);
        Assert.False(TryGetCachedProperty(introspector, typeof(TestOrder), overCapProperty, out _));
    }

    /// <summary>
    /// The value read comes back with the type the member DECLARES, not the type the value
    /// happens to have — the whole point of the pairing, since a boxed int cannot say whether it
    /// came from an int or an int?.
    /// </summary>
    [Fact]
    public void TryReadValue_reports_the_declared_type_beside_the_value()
    {
        var item = new TestLineItem { Sku = "ABC", Quantity = 0 };

        Assert.True(_introspector.TryReadValue(item, nameof(TestLineItem.Sku), out var sku, out var skuType));
        Assert.Equal("ABC", sku);
        Assert.Equal(typeof(string), skuType);

        Assert.True(_introspector.TryReadValue(item, nameof(TestLineItem.Quantity), out var quantity, out var quantityType));
        Assert.Equal(0, quantity);
        Assert.Equal(typeof(int), quantityType);
    }

    /// <summary>
    /// A null reference member reads successfully — null is a value the caller has to be able to
    /// see — while the shapes that cannot be read report false, so a caller can tell "this holds
    /// nothing" from "this could not be asked". The unresolved-remainder case is the one that
    /// matters most: Resolve hands back exactly that shape when an intermediate is null.
    /// </summary>
    [Fact]
    public void TryReadValue_separates_a_null_value_from_a_member_it_cannot_read()
    {
        var order = new TestOrder();

        Assert.True(_introspector.TryReadValue(order, nameof(TestOrder.Customer), out var customer, out var customerType));
        Assert.Null(customer);
        Assert.Equal(typeof(TestCustomer), customerType);

        var stranded = _introspector.Resolve(order, "Customer.Name");
        Assert.Equal("Customer.Name", stranded.PropertyName);
        Assert.False(_introspector.TryReadValue(stranded.Owner, stranded.PropertyName, out _, out var strandedType));
        Assert.Null(strandedType);

        Assert.False(_introspector.TryReadValue(order, string.Empty, out _, out _));
        Assert.False(_introspector.TryReadValue(order, "Missing", out _, out _));
        Assert.False(_introspector.TryReadValue(order, "[0]", out _, out _));
    }
}
