using System.Collections;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Text.Json.Nodes;
using Formidable.Blazor.Tests.Fixtures;
using Formidable.Introspection;
using Microsoft.AspNetCore.Components.Forms;

namespace Formidable.Blazor.Tests;

public class ResolvedFieldExtensionsTests
{
    private readonly ReflectionModelIntrospector _introspector = new();

    [Fact]
    public void Reference_owner_maps_directly()
    {
        var order = new EngineOrder { Customer = new EngineCustomer() };

        var field = _introspector.Resolve(order, "Customer.Name").ToFieldIdentifier(order, "Customer.Name");

        Assert.Same(order.Customer, field.Model);
        Assert.Equal("Name", field.FieldName);
    }

    [Fact]
    public void Indexed_item_owner_maps_to_the_item()
    {
        var order = new EngineOrder { Items = [new EngineItem(), new EngineItem()] };

        var field = _introspector.Resolve(order, "Items[1].Sku").ToFieldIdentifier(order, "Items[1].Sku");

        Assert.Same(order.Items[1], field.Model);
        Assert.Equal("Sku", field.FieldName);
    }

    [Fact]
    public void Empty_path_maps_to_model_level_identifier()
    {
        var order = new EngineOrder();

        var field = _introspector.Resolve(order, string.Empty).ToFieldIdentifier(order, string.Empty);

        Assert.Same(order, field.Model);
        Assert.Equal(string.Empty, field.FieldName);
    }

    [Fact]
    public void Struct_owner_falls_back_to_root_model_with_original_path()
    {
        var order = new EngineOrder();

        var resolved = _introspector.Resolve(order, "Location.X");
        var field = resolved.ToFieldIdentifier(order, "Location.X");

        Assert.Same(order, field.Model);
        Assert.Equal("Location.X", field.FieldName);
    }

    // A list or array element is the field Blazor's FieldIdentifier.Create names for a row bound
    // with () => model.Tags[i]: the collection and the index. An object row's property and a
    // value-type owner keep the shapes the two tests above give them. Mutation that must break it:
    // return the introspector's member name unchanged, and both elements read "[1]".
    [Fact]
    public void A_list_or_array_element_maps_to_the_collection_and_its_index()
    {
        var model = new ScalarRows { List = ["a", "b"], Array = ["a", "b"] };
        var order = new EngineOrder { Items = [new EngineItem(), new EngineItem()] };

        var listRow = _introspector.Resolve(model, "List[1]").ToFieldIdentifier(model, "List[1]");
        var arrayRow = _introspector.Resolve(model, "Array[1]").ToFieldIdentifier(model, "Array[1]");
        var objectRow = _introspector.Resolve(order, "Items[1].Sku").ToFieldIdentifier(order, "Items[1].Sku");
        var structOwner = _introspector.Resolve(order, "Location.X").ToFieldIdentifier(order, "Location.X");

        Assert.Equal(FieldIdentifier.Create(() => model.List[1]), listRow);
        Assert.Equal(FieldIdentifier.Create(() => model.Array[1]), arrayRow);
        Assert.Equal(new FieldIdentifier(order.Items[1], nameof(EngineItem.Sku)), objectRow);
        Assert.Equal(new FieldIdentifier(order, "Location.X"), structOwner);
    }

    // Only a whole index on a list or array is an element. A dictionary's entry is not one, even
    // under a numeric key, because FluentValidation counts a dictionary's entries by position
    // while Blazor names one by its key. The remainder a walk stopped short of keeps its name too.
    // A pin: both names already hold before elements are named by index, and they must survive
    // it. Mutations that must break it: widen the list-or-array test to any collection (the entry
    // reads "0"), or read the index up to the first ']' rather than to the end (the remainder
    // reads "5").
    [Fact]
    public void A_dictionary_entry_or_an_unresolved_remainder_keeps_its_brackets()
    {
        var model = new ScalarRows { List = ["a"], Keyed = { [0] = "red" } };

        var entry = _introspector.Resolve(model, "Keyed[0]").ToFieldIdentifier(model, "Keyed[0]");
        var remainder = _introspector.Resolve(model, "List[5].Length").ToFieldIdentifier(model, "List[5].Length");

        Assert.Equal(new FieldIdentifier(model.Keyed, "[0]"), entry);
        Assert.Equal(new FieldIdentifier(model.List, "[5].Length"), remainder);
    }

    // An OrderedDictionary is a non-generic IList as well as a dictionary. FluentValidation numbers
    // its entries by position, while Blazor names an input bound to one by its key, so an entry
    // keeps its brackets as any dictionary's does. The list interface is asserted first, so the
    // test cannot pass for want of it. Mutation that must break it: drop the dictionary exclusion
    // from the list test, and the entry reads "1".
    [Fact]
    public void An_OrderedDictionary_entry_keeps_its_brackets()
    {
        var model = new ScalarRows { Ordered = { [1] = "fine", [2] = "", [3] = "ok" } };
        Assert.IsAssignableFrom<IList>(model.Ordered);

        var entry = _introspector.Resolve(model, "Ordered[1]").ToFieldIdentifier(model, "Ordered[1]");

        Assert.Same(model.Ordered, entry.Model);
        Assert.Equal("[1]", entry.FieldName);
    }

    // A list of key-value pairs is a list and not a dictionary, so its elements keep their index
    // naming whatever they hold. A pin. Mutation that must break it: exclude every collection of
    // key-value pairs from the list test as well, and the element keeps its brackets.
    [Fact]
    public void A_list_of_key_value_pairs_keeps_its_index_naming()
    {
        var model = new ScalarRows { Pairs = [new(1, "fine"), new(2, "")] };

        var element = _introspector.Resolve(model, "Pairs[1]").ToFieldIdentifier(model, "Pairs[1]");

        Assert.Same(model.Pairs, element.Model);
        Assert.Equal("1", element.FieldName);
        Assert.Equal(FieldIdentifier.Create(() => model.Pairs[1]), element);
    }

    // A field names an element when its model is a list named by index and its name is a whole
    // index, the shape FieldIdentifier.Create(() => model.Tags[i]) gives. A dictionary's entry is
    // not one under a numeric key, an ordered one's included, and neither is a bracketed or signed
    // name. Mutations that must break it: drop the dictionary exclusion from the list test (both
    // dictionary entries read as elements), or parse the name with the default number style (the
    // signed name reads as one).
    [Fact]
    public void An_indexed_element_is_a_whole_index_on_a_list_that_is_not_a_dictionary()
    {
        var model = new ScalarRows { List = ["a", "b"], Array = ["a", "b"], Keyed = { [1] = "red" }, Ordered = { [1] = "fine" } };

        Assert.True(ResolvedFieldExtensions.IsIndexedElement(FieldIdentifier.Create(() => model.List[1])));
        Assert.True(ResolvedFieldExtensions.IsIndexedElement(FieldIdentifier.Create(() => model.Array[1])));
        Assert.False(ResolvedFieldExtensions.IsIndexedElement(FieldIdentifier.Create(() => model.Keyed[1])));
        Assert.False(ResolvedFieldExtensions.IsIndexedElement(FieldIdentifier.Create(() => model.Ordered[1])));
        Assert.False(ResolvedFieldExtensions.IsIndexedElement(new FieldIdentifier(model.List, "[1]")));
        Assert.False(ResolvedFieldExtensions.IsIndexedElement(new FieldIdentifier(model.List, "+1")));
    }

    // An element of a list that implements only the generic IList<T> or IReadOnlyList<T> is the
    // field Blazor names by the list and the index, as an element of a List<T> is. Mutations that
    // must break it: accept the non-generic IList alone in the list test (every case keeps "[1]"),
    // or drop its IReadOnlyList<T> arm (the read-only list keeps "[1]").
    [Theory]
    [InlineData("ImmutableArray<string>.Builder")]
    [InlineData("ImmutableArray<int>.Builder")]
    [InlineData("JsonArray")]
    [InlineData("IList<T> only")]
    [InlineData("IReadOnlyList<T> only")]
    public void ToFieldIdentifier_names_a_generic_only_element_by_position(string shape)
    {
        var (root, list, bound) = Census(shape);
        Assert.IsNotAssignableFrom<IList>(list);

        var element = _introspector.Resolve(root, "Tags[1]").ToFieldIdentifier(root, "Tags[1]");

        Assert.Same(list, element.Model);
        Assert.Equal("1", element.FieldName);
        Assert.Equal(bound, element);
        Assert.True(ResolvedFieldExtensions.IsIndexedElement(element));
    }

    // A pin: every list the non-generic IList already named by its index keeps that naming.
    // Mutation that must break it: drop the non-generic IList arm from the list test, and the
    // ArrayList, which implements no generic list interface, reads "[1]".
    [Theory]
    [InlineData("List<T>")]
    [InlineData("T[]")]
    [InlineData("Collection<T>")]
    [InlineData("ObservableCollection<T>")]
    [InlineData("ReadOnlyCollection<T>")]
    [InlineData("ImmutableList<T>")]
    [InlineData("ImmutableList<T>.Builder")]
    [InlineData("KeyedCollection<string, T> by position")]
    [InlineData("ArrayList")]
    public void A_list_named_by_index_keeps_its_index_naming(string shape)
    {
        var (root, list, bound) = Census(shape);
        Assert.IsAssignableFrom<IList>(list);

        var element = _introspector.Resolve(root, "Tags[1]").ToFieldIdentifier(root, "Tags[1]");

        Assert.Same(list, element.Model);
        Assert.Equal("1", element.FieldName);
        Assert.Equal(bound, element);
    }

    // A dictionary's entry keeps its brackets whichever dictionary interface it implements, a
    // generic-only one that is also a read-only list of its pairs included, and so does an element
    // of a set or of a collection read by id through an int indexer: none of these names its
    // elements by position. Mutations that must break it: drop the dictionary interfaces from the
    // generic list test (the ordered map's entry reads "1"), or accept any enumerable (every case
    // reads "1").
    [Theory]
    [InlineData("IDictionary<TKey, TValue> only")]
    [InlineData("IReadOnlyDictionary and IReadOnlyList of its pairs")]
    [InlineData("JsonObject")]
    [InlineData("HashSet<T>")]
    [InlineData("int indexer read by id")]
    public void A_dictionary_a_set_or_a_collection_read_by_id_keeps_its_brackets(string shape)
    {
        var (root, list, _) = Census(shape);

        var element = _introspector.Resolve(root, "Tags[1]").ToFieldIdentifier(root, "Tags[1]");

        Assert.Same(list, element.Model);
        Assert.Equal("[1]", element.FieldName);
        Assert.False(ResolvedFieldExtensions.IsIndexedList(list));
    }

    /// <summary>A model holding <paramref name="shape"/>'s collection at <c>Tags</c>, the collection, and the field an input bound to <c>Tags[1]</c> names.</summary>
    private static (object Root, object List, FieldIdentifier Bound) Census(string shape)
    {
        string?[] seed = ["a", "b"];
        switch (shape)
        {
            case "ImmutableArray<string>.Builder":
            {
                var builder = ImmutableArray.CreateBuilder<string?>();
                builder.AddRange(seed);
                return Of(new Holder<ImmutableArray<string?>.Builder> { Tags = builder }, m => () => m.Tags[1]);
            }

            case "ImmutableArray<int>.Builder":
            {
                var builder = ImmutableArray.CreateBuilder<int>();
                builder.AddRange(1, 2);
                return Of(new Holder<ImmutableArray<int>.Builder> { Tags = builder }, m => () => m.Tags[1]);
            }

            case "JsonArray":
                return Of(new Holder<JsonArray> { Tags = new JsonArray("a", "b") }, m => () => m.Tags[1]);
            case "IList<T> only":
                return Of(new Holder<GenericListRowTests.GenericOnlyList<string?>> { Tags = new(seed) }, m => () => m.Tags[1]);
            case "IReadOnlyList<T> only":
                return Of(new Holder<GenericListRowTests.ReadOnlyOnlyList<string?>> { Tags = new(seed) }, m => () => m.Tags[1]);
            case "List<T>":
                return Of(new Holder<List<string?>> { Tags = [.. seed] }, m => () => m.Tags[1]);
            case "T[]":
                return Of(new Holder<string?[]> { Tags = [.. seed] }, m => () => m.Tags[1]);
            case "Collection<T>":
                return Of(new Holder<Collection<string?>> { Tags = [.. seed] }, m => () => m.Tags[1]);
            case "ObservableCollection<T>":
                return Of(new Holder<ObservableCollection<string?>> { Tags = [.. seed] }, m => () => m.Tags[1]);
            case "ReadOnlyCollection<T>":
                return Of(new Holder<ReadOnlyCollection<string?>> { Tags = new([.. seed]) }, m => () => m.Tags[1]);
            case "ImmutableList<T>":
                return Of(new Holder<ImmutableList<string?>> { Tags = [.. seed] }, m => () => m.Tags[1]);
            case "ImmutableList<T>.Builder":
            {
                var builder = ImmutableList.CreateBuilder<string?>();
                builder.AddRange(seed);
                return Of(new Holder<ImmutableList<string?>.Builder> { Tags = builder }, m => () => m.Tags[1]);
            }

            case "KeyedCollection<string, T> by position":
                return Of(new Holder<StringKeyed> { Tags = ["a", "b"] }, m => () => m.Tags[1]);
            case "ArrayList":
                return Of(new Holder<ArrayList> { Tags = new ArrayList { "a", "b" } }, m => () => m.Tags[1]);
            case "IDictionary<TKey, TValue> only":
                return Of(new Holder<GenericOnlyDictionary> { Tags = new() { [0] = "a", [1] = "b" } }, m => () => m.Tags[1]);
            case "IReadOnlyDictionary and IReadOnlyList of its pairs":
                return Of(new Holder<OrderedMap> { Tags = new OrderedMap(("x", "a"), ("y", "b")) }, m => () => m.Tags[1]);
            case "JsonObject":
                return Of(new Holder<JsonObject> { Tags = new JsonObject { ["x"] = "a", ["y"] = "b" } }, m => () => m.Tags["y"]);
            case "HashSet<T>":
                return Of(new Holder<HashSet<string?>> { Tags = [.. seed] }, m => () => m.Tags.Count);
            case "int indexer read by id":
                return Of(new Holder<GenericListRowTests.IdIndexedBag> { Tags = new() }, m => () => m.Tags[0]);
            default:
                throw new ArgumentOutOfRangeException(nameof(shape), shape, null);
        }

        static (object, object, FieldIdentifier) Of<TList>(Holder<TList> root, Func<Holder<TList>, Expression<Func<object?>>> accessor)
            where TList : class =>
            (root, root.Tags, FieldIdentifier.Create(accessor(root)));
    }

    public sealed class Holder<TList>
    {
        public required TList Tags { get; set; }
    }

    public sealed class StringKeyed : KeyedCollection<string, string>
    {
        protected override string GetKeyForItem(string item) => item;
    }

    /// <summary>A dictionary that implements only the generic <see cref="IDictionary{TKey, TValue}"/>.</summary>
    public sealed class GenericOnlyDictionary : IDictionary<int, string?>
    {
        private readonly Dictionary<int, string?> _inner = [];

        public string? this[int key] { get => _inner[key]; set => _inner[key] = value; }

        public ICollection<int> Keys => _inner.Keys;

        public ICollection<string?> Values => _inner.Values;

        public int Count => _inner.Count;

        public bool IsReadOnly => false;

        public void Add(int key, string? value) => _inner.Add(key, value);

        public void Add(KeyValuePair<int, string?> item) => _inner.Add(item.Key, item.Value);

        public void Clear() => _inner.Clear();

        public bool Contains(KeyValuePair<int, string?> item) => _inner.Contains(item);

        public bool ContainsKey(int key) => _inner.ContainsKey(key);

        public void CopyTo(KeyValuePair<int, string?>[] array, int arrayIndex) => ((ICollection<KeyValuePair<int, string?>>)_inner).CopyTo(array, arrayIndex);

        public IEnumerator<KeyValuePair<int, string?>> GetEnumerator() => _inner.GetEnumerator();

        public bool Remove(int key) => _inner.Remove(key);

        public bool Remove(KeyValuePair<int, string?> item) => ((ICollection<KeyValuePair<int, string?>>)_inner).Remove(item);

        public bool TryGetValue(int key, [MaybeNullWhen(false)] out string? value) => _inner.TryGetValue(key, out value);

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>A map that implements only the generic read-only interfaces: a dictionary by key, and a list of its pairs by position.</summary>
    public sealed class OrderedMap(params (string Key, string? Value)[] entries)
        : IReadOnlyDictionary<string, string?>, IReadOnlyList<KeyValuePair<string, string?>>
    {
        private readonly List<KeyValuePair<string, string?>> _entries = [.. entries.Select(entry => KeyValuePair.Create(entry.Key, entry.Value))];

        public string? this[string key] => _entries.Single(entry => entry.Key == key).Value;

        public KeyValuePair<string, string?> this[int index] => _entries[index];

        public IEnumerable<string> Keys => _entries.Select(entry => entry.Key);

        public IEnumerable<string?> Values => _entries.Select(entry => entry.Value);

        public int Count => _entries.Count;

        public bool ContainsKey(string key) => _entries.Any(entry => entry.Key == key);

        public bool TryGetValue(string key, [MaybeNullWhen(false)] out string? value)
        {
            var index = _entries.FindIndex(entry => entry.Key == key);
            value = index >= 0 ? _entries[index].Value : null;
            return index >= 0;
        }

        public IEnumerator<KeyValuePair<string, string?>> GetEnumerator() => _entries.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class ScalarRows
    {
        public List<string> List { get; set; } = [];

        public string[] Array { get; set; } = [];

        public Dictionary<int, string> Keyed { get; } = [];

        public OrderedDictionary<int, string?> Ordered { get; } = new();

        public List<KeyValuePair<int, string?>> Pairs { get; set; } = [];
    }
}
