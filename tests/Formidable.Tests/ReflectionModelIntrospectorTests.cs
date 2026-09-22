using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Immutable;
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

    /// <summary>
    /// Reads one of the two cap counters (<c>_cachedPathCount</c> / <c>_cachedPropertyCount</c>)
    /// the introspector checks a new entry against, for tests only.
    /// </summary>
    private static int GetCapCounter(ReflectionModelIntrospector introspector, string counterField)
    {
        var field = typeof(ReflectionModelIntrospector)
            .GetField(counterField, BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (int)field.GetValue(introspector)!;
    }

    /// <summary>
    /// Places a cap counter at a chosen value, so a state that takes 2^31 misses to reach can be
    /// tested from the miss after it rather than driven there.
    /// </summary>
    private static void SetCapCounter(ReflectionModelIntrospector introspector, string counterField, int value)
    {
        var field = typeof(ReflectionModelIntrospector)
            .GetField(counterField, BindingFlags.NonPublic | BindingFlags.Instance)!;
        field.SetValue(introspector, value);
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
        // a failed one — FormidableEngine.Resolve feeds this cache issue paths straight off
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

    /// <summary>
    /// The list shapes that implement the non-generic <see cref="IList"/> and still refuse the
    /// read that decides an index: <see cref="Array"/> does whatever its rank and throws from
    /// <c>this[int]</c> past rank one, a default <see cref="ImmutableArray{T}"/> is a value
    /// rather than a null and throws from <c>Count</c>, and a consumer's own list can do either.
    /// Every path through them is one a server response can name.
    /// </summary>
    private sealed class HostileLists
    {
        public int[,] Grid { get; } = new int[2, 2];

        public ImmutableArray<IndexedRow> Rows { get; }

        public ArrayList Bad { get; } = new ThrowingIndexerList { "one" };
    }

    private sealed class ThrowingIndexerList : ArrayList
    {
        public override object? this[int index]
        {
            get => throw new NotSupportedException("indexer failure");
            set => throw new NotSupportedException("indexer failure");
        }
    }

    [Fact]
    public void A_rank_two_array_intermediate_falls_back_rather_than_throwing()
    {
        // The class contract promises that a failure to navigate lands on the deepest owner
        // reached, and the two-indexer pin above holds it for the indexer-property arm. The IList
        // arm answers the same shape of path, so it owes the same answer: the array is the owner
        // the walk reached, and the index it could not take is what is left of the path.
        var model = new HostileLists();

        var field = _introspector.Resolve(model, "Grid[0].X");

        Assert.Same(model.Grid, field.Owner);
        Assert.Equal("[0].X", field.PropertyName);
    }

    [Fact]
    public void A_default_ImmutableArray_intermediate_falls_back_rather_than_throwing()
    {
        // Reflection boxes the default struct and hands it back as a value, so the null
        // fallback never fires; the boxed array is then the IList whose Count throws. The owner
        // reached is that box, which is why the assertion is on its type rather than its identity.
        var model = new HostileLists();

        var field = _introspector.Resolve(model, "Rows[0].Name");

        Assert.IsType<ImmutableArray<IndexedRow>>(field.Owner);
        Assert.Equal("[0].Name", field.PropertyName);
    }

    [Fact]
    public void A_list_whose_indexer_throws_falls_back_rather_than_throwing()
    {
        // The in-range half of the arm: Count answers, the index is inside it, and the read
        // itself is what throws.
        var model = new HostileLists();

        var field = _introspector.Resolve(model, "Bad[0].X");

        Assert.Same(model.Bad, field.Owner);
        Assert.Equal("[0].X", field.PropertyName);
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

    [Fact]
    public void A_member_name_longer_than_the_cache_length_cap_resolves_but_is_never_remembered()
    {
        // The member-name half of the property cache's key is a path segment, so it arrives as
        // unfiltered as the path it was cut from, and an entry holds a copy of it: the count cap
        // bounds how many names are kept and this bounds how long one may be, the same pair the
        // path cache holds. A name no type could declare is reflected, answered and dropped —
        // and spends none of the count budget on the way, so a flood of them cannot spend it.
        var order = new TestOrder();
        var introspector = new ReflectionModelIntrospector();
        var overlong = new string('a', GetMaxCachedPathLength() + 1);

        var field = introspector.Resolve(order, $"{overlong}.Leaf");

        Assert.Same(order, field.Owner);
        Assert.Equal($"{overlong}.Leaf", field.PropertyName);
        Assert.False(TryGetCachedProperty(introspector, typeof(TestOrder), overlong, out _));
        Assert.Equal(0, GetPropertyCacheCount(introspector));
        Assert.Equal(0, GetCapCounter(introspector, "_cachedPropertyCount"));
    }

    [Fact]
    public void A_member_name_at_the_cache_length_cap_is_still_remembered()
    {
        // The control for the refusal above, through the read that reaches the property cache
        // with no path parse in front of it: a name exactly at the cap is cached, its miss
        // included.
        var order = new TestOrder();
        var introspector = new ReflectionModelIntrospector();
        var atCap = new string('a', GetMaxCachedPathLength());

        Assert.False(introspector.TryReadValue(order, atCap, out _, out _));

        Assert.True(TryGetCachedProperty(introspector, typeof(TestOrder), atCap, out var cached));
        Assert.Null(cached);
    }

    [Fact]
    public void Path_cache_counter_stops_at_the_cap_rather_than_counting_every_later_miss()
    {
        // The counter IS the cap. One that kept counting every miss past it would wrap negative
        // after 2^31 of them and pass the check again, re-opening the cache to as many entries
        // more; stopping at the cap is what makes that unreachable.
        var order = new TestOrder();
        var introspector = new ReflectionModelIntrospector();
        var cap = GetMaxCachedPaths();

        for (var i = 0; i < cap + 50; i++)
        {
            introspector.Resolve(order, $"Field{i}");
        }

        Assert.Equal(cap, GetPathCacheCount(introspector));
        Assert.Equal(cap, GetCapCounter(introspector, "_cachedPathCount"));
    }

    [Fact]
    public void Path_cache_counter_at_the_wrap_edge_admits_nothing_and_does_not_move()
    {
        // The state 2^31 misses would reach, entered directly: the counter one miss from
        // wrapping, then the misses the wrap would have admitted.
        var order = new TestOrder();
        var introspector = new ReflectionModelIntrospector();
        SetCapCounter(introspector, "_cachedPathCount", int.MaxValue);

        for (var i = 0; i < 50; i++)
        {
            introspector.Resolve(order, $"Field{i}");
        }

        Assert.Equal(0, GetPathCacheCount(introspector));
        Assert.Equal(int.MaxValue, GetCapCounter(introspector, "_cachedPathCount"));
    }

    [Fact]
    public void Property_cache_counter_stops_at_the_cap_rather_than_counting_every_later_miss()
    {
        var order = new TestOrder();
        var introspector = new ReflectionModelIntrospector();
        var cap = GetMaxCachedProperties();

        for (var i = 0; i < cap + 50; i++)
        {
            introspector.Resolve(order, $"RandomProp{i}.Leaf");
        }

        Assert.Equal(cap, GetPropertyCacheCount(introspector));
        Assert.Equal(cap, GetCapCounter(introspector, "_cachedPropertyCount"));
    }

    [Fact]
    public void Property_cache_counter_at_the_wrap_edge_admits_nothing_and_does_not_move()
    {
        var order = new TestOrder();
        var introspector = new ReflectionModelIntrospector();
        SetCapCounter(introspector, "_cachedPropertyCount", int.MaxValue);

        for (var i = 0; i < 50; i++)
        {
            introspector.Resolve(order, $"RandomProp{i}.Leaf");
        }

        Assert.Equal(0, GetPropertyCacheCount(introspector));
        Assert.Equal(int.MaxValue, GetCapCounter(introspector, "_cachedPropertyCount"));
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
        // Every path this walks can arrive from outside the app -- FormidableEngine.Resolve
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
