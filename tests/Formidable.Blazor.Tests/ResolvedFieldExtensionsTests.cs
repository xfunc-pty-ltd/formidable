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
}
