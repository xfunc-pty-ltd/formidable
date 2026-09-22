using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json.Nodes;
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

    private static int GetMaxCachedPathLength()
    {
        var field = typeof(ReflectionModelIntrospector)
            .GetField("MaxCachedPathLength", BindingFlags.NonPublic | BindingFlags.Static)!;
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

    private sealed class IndexedRow
    {
        public string City { get; set; } = string.Empty;
    }

    /// <summary>
    /// Two public indexers, which is what <c>Type.GetProperty("Item")</c> cannot answer: the
    /// shape <see cref="System.Text.Json.Nodes.JsonObject"/>, <c>NameValueCollection</c> and
    /// the non-generic <c>OrderedDictionary</c> all have, and the one a consumer's own keyed
    /// bag reaches for.
    /// </summary>
    private sealed class TwoIndexerBag
    {
        private readonly IndexedRow _byInt = new() { City = "reached by int" };
        private readonly IndexedRow _byString = new() { City = "reached by string" };

        public IndexedRow this[int index] => _byInt;

        public IndexedRow this[string key] => _byString;
    }

    private sealed class TwoIndexerHolder
    {
        public TwoIndexerBag Bag { get; } = new();

        public JsonObject Json { get; } = new() { ["a"] = new JsonObject { ["Foo"] = 1 } };
    }

    [Fact]
    public void An_intermediate_with_two_indexers_navigates_by_the_token_rather_than_throwing()
    {
        // The index token decides the overload — a token that parses as an int wants the int
        // indexer, anything else wants the string one — so both halves of a two-indexer type
        // stay reachable. Resolving the ambiguity here is what keeps the class's stated
        // fallback contract honest: reflection's own "Item" lookup throws for this shape.
        var holder = new TwoIndexerHolder();

        var byInt = _introspector.Resolve(holder, "Bag[0].City");
        var byString = _introspector.Resolve(holder, "Bag[k].City");

        Assert.Same(holder.Bag[0], byInt.Owner);
        Assert.Equal(nameof(IndexedRow.City), byInt.PropertyName);
        Assert.Same(holder.Bag["k"], byString.Owner);
        Assert.Equal(nameof(IndexedRow.City), byString.PropertyName);
    }

    [Fact]
    public void A_JsonObject_intermediate_resolves_through_its_string_indexer()
    {
        // Every JsonNode declares both this[int] and this[string], so a RuleForEach over a
        // JsonObject of extensible fields navigates through exactly the shape reflection's own
        // lookup calls ambiguous. The engine's Resolve has no catch around it, so the failure
        // this shape produced was a thrown pass rather than a mis-filed message.
        var holder = new TwoIndexerHolder();

        var field = _introspector.Resolve(holder, "Json[a].Foo");

        Assert.Same(holder.Json["a"], field.Owner);
        Assert.Equal("Foo", field.PropertyName);
    }

    [Fact]
    public void A_path_longer_than_the_cache_length_cap_resolves_but_is_never_remembered()
    {
        // The cache holds a copy of the key plus a segment per property, so the retained cost
        // of one entry scales with the path's length, not with the entry count the total cap
        // measures. A path no validator would ever produce is answered and dropped, which is
        // what holds the byte bound the two caps claim together against a flood of long keys.
        var order = new TestOrder();
        var introspector = new ReflectionModelIntrospector();
        var overlong = new string('a', GetMaxCachedPathLength() + 1);

        var field = introspector.Resolve(order, overlong);

        Assert.Same(order, field.Owner);
        Assert.Equal(overlong, field.PropertyName);
        Assert.False(TryGetCachedParse(introspector, overlong, out _));
        Assert.Equal(0, GetPathCacheCount(introspector));
    }

    [Fact]
    public void A_path_at_the_cache_length_cap_is_still_remembered()
    {
        // The control for the refusal above: the length cap is a ceiling on what is worth
        // remembering, not one that swallows ordinary paths — a path exactly at it is cached.
        var order = new TestOrder();
        var introspector = new ReflectionModelIntrospector();
        var atCap = new string('a', GetMaxCachedPathLength());

        introspector.Resolve(order, atCap);

        Assert.True(TryGetCachedParse(introspector, atCap, out _));
    }

    private class NonPublicMembers
    {
        public NonPublicMembers(TestAddress hidden) => Hidden = hidden;

        public string Visible { get; set; } = string.Empty;

        internal TestAddress Hidden { get; }

        private string Secret { get; } = "private";

        protected string Guarded { get; } = "protected";

        public static string Shared { get; } = "static";

        internal string Reveal() => $"{Secret}{Guarded}";
    }

    [Fact]
    public void Member_resolution_reaches_public_instance_members_only()
    {
        // Every path this walks can arrive from outside the app -- FormValidationEngine.Resolve
        // hands it a server response's issue paths verbatim -- so the members it will read are
        // deliberately the ones a model declares as its public shape. A non-public or static
        // member is answered exactly as a member that does not exist: unreadable, and
        // unnavigable, so navigation stops on the deepest owner it did reach.
        var model = new NonPublicMembers(new TestAddress { City = "Adelaide" });

        Assert.True(_introspector.TryReadValue(model, nameof(NonPublicMembers.Visible), out _, out _));
        Assert.False(_introspector.TryReadValue(model, "Hidden", out _, out var hiddenType));
        Assert.Null(hiddenType);
        Assert.False(_introspector.TryReadValue(model, "Secret", out _, out _));
        Assert.False(_introspector.TryReadValue(model, "Guarded", out _, out _));
        Assert.False(_introspector.TryReadValue(model, nameof(NonPublicMembers.Shared), out _, out _));

        // The navigation half of the same rule: an internal intermediate is not stepped through,
        // so the field resolves on the model with the whole remaining path as its name.
        var field = _introspector.Resolve(model, "Hidden.City");
        Assert.Same(model, field.Owner);
        Assert.Equal("Hidden.City", field.PropertyName);

        // The control that keeps the assertions above about ACCESSIBILITY rather than about the
        // members being absent: the same members are there, and readable, from inside the type.
        Assert.Equal("privateprotected", model.Reveal());
    }
}
