using Formidable.Introspection;
using Formidable.Tests.Fixtures;

namespace Formidable.Tests;

public class ReflectionModelIntrospectorTests
{
    private readonly ReflectionModelIntrospector _introspector = new();

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
}
