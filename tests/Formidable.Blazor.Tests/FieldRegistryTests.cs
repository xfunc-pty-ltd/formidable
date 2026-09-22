using Formidable.Blazor.Tests.Fixtures;
using Microsoft.AspNetCore.Components.Forms;

namespace Formidable.Blazor.Tests;

public class FieldRegistryTests
{
    private readonly FieldRegistry _registry = new();
    private readonly EngineOrder _order = new();

    private FieldIdentifier Field(string name) => new(_order, name);

    [Fact]
    public void Unregistered_field_is_not_revealed()
    {
        Assert.False(_registry.IsRevealed(Field("Description")));
    }

    [Fact]
    public void Registered_field_is_revealed_until_disposed()
    {
        var registration = _registry.Register(Field("Description"));

        Assert.True(_registry.IsRevealed(Field("Description")));

        registration.Dispose();

        Assert.False(_registry.IsRevealed(Field("Description")));
    }

    [Fact]
    public void Duplicate_registrations_are_ref_counted()
    {
        var first = _registry.Register(Field("Description"));
        var second = _registry.Register(Field("Description"));

        first.Dispose();
        Assert.True(_registry.IsRevealed(Field("Description")));

        second.Dispose();
        Assert.False(_registry.IsRevealed(Field("Description")));
    }

    [Fact]
    public void Keep_registered_field_stays_revealed_after_dispose()
    {
        var registration = _registry.Register(Field("Description"), keepRegistered: true);

        registration.Dispose();

        Assert.True(_registry.IsRevealed(Field("Description")));
    }

    [Fact]
    public void Identity_is_instance_keyed()
    {
        var itemA = new EngineItem();
        var itemB = new EngineItem();
        _registry.Register(new FieldIdentifier(itemA, "Sku"));

        Assert.True(_registry.IsRevealed(new FieldIdentifier(itemA, "Sku")));
        Assert.False(_registry.IsRevealed(new FieldIdentifier(itemB, "Sku")));
    }

    [Fact]
    public void Double_dispose_is_idempotent()
    {
        var registration = _registry.Register(Field("Description"));

        registration.Dispose();
        registration.Dispose();

        Assert.False(_registry.IsRevealed(Field("Description")));
    }

    [Fact]
    public void Later_non_kept_disposal_reverses_an_earlier_keep()
    {
        _registry.Register(Field("Description"), keepRegistered: true).Dispose();
        Assert.True(_registry.IsRevealed(Field("Description")));

        var second = _registry.Register(Field("Description"));
        second.Dispose();

        Assert.False(_registry.IsRevealed(Field("Description")));
    }
}
