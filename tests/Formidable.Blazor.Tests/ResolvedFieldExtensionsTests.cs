using System.Collections;
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

    private sealed class ScalarRows
    {
        public List<string> List { get; set; } = [];

        public string[] Array { get; set; } = [];

        public Dictionary<int, string> Keyed { get; } = [];

        public OrderedDictionary<int, string?> Ordered { get; } = new();

        public List<KeyValuePair<int, string?>> Pairs { get; set; } = [];
    }
}
