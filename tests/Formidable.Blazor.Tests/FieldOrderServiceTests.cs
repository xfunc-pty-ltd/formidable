using Bunit;
using Formidable.Blazor.Tests.Fixtures;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;

namespace Formidable.Blazor.Tests;

/// <summary>
/// Pins the JS-backed <see cref="IFormidableFieldOrderService"/> the way FocusServiceTests pins
/// the focus service. The seam speaks fields; the element ids the browser is asked about, and the
/// mapping back from the ids it answers with, belong to this implementation alone.
/// </summary>
public class FieldOrderServiceTests : BunitContext
{
    private readonly EngineOrder _order = new() { Customer = new EngineCustomer() };

    [Fact]
    public async Task Order_asks_about_the_ids_in_one_argument_and_answers_with_fields()
    {
        var module = SetUpModule([CustomerNameId, DescriptionId]);
        var service = Services.GetRequiredService<IFormidableFieldOrderService>();

        var ordered = await service.OrderAsync([DescriptionField, CustomerNameField]);

        // One argument, holding both ids: the JS function takes the list, so ids arriving as a
        // separate parameter each would leave it iterating a single string's characters.
        var argument = Assert.Single(module.VerifyInvoke("orderFields").Arguments);
        Assert.Equal([DescriptionId, CustomerNameId], Assert.IsAssignableFrom<IReadOnlyList<string>>(argument));

        // And the answer comes back in the currency the caller asked in, in the module's order.
        Assert.Equal([CustomerNameField, DescriptionField], ordered);
    }

    [Fact]
    public async Task An_id_that_was_never_asked_about_is_not_reported_as_a_field()
    {
        SetUpModule(["formidable-00000000-invented", DescriptionId]);
        var service = Services.GetRequiredService<IFormidableFieldOrderService>();

        var ordered = await service.OrderAsync([DescriptionField, CustomerNameField]);

        // The ids that come back are the ids that went out, so anything else names no field here.
        Assert.Equal([DescriptionField], ordered);
    }

    [Fact]
    public async Task An_answer_that_arrives_as_nothing_is_not_an_order()
    {
        SetUpModule(null);
        var service = Services.GetRequiredService<IFormidableFieldOrderService>();

        var ordered = await service.OrderAsync([DescriptionField, CustomerNameField]);

        // Null rather than empty: an empty answer means the page placed none of these fields, and
        // a host that read this one that way would stop asking.
        Assert.Null(ordered);
    }

    [Fact]
    public async Task A_page_that_placed_none_of_the_fields_is_an_empty_order()
    {
        SetUpModule([]);
        var service = Services.GetRequiredService<IFormidableFieldOrderService>();

        var ordered = await service.OrderAsync([DescriptionField, CustomerNameField]);

        // The other half of the distinction above: nothing matched is an answer, and one a host
        // can act on. Collapsing it to null would leave a form re-resolving on every render.
        Assert.NotNull(ordered);
        Assert.Empty(ordered);
    }

    private FieldIdentifier DescriptionField => new(_order, nameof(EngineOrder.Description));

    private FieldIdentifier CustomerNameField => new(_order.Customer!, nameof(EngineCustomer.Name));

    private string DescriptionId => FormidableFieldId.For(DescriptionField);

    private string CustomerNameId => FormidableFieldId.For(CustomerNameField);

    private BunitJSModuleInterop SetUpModule(IReadOnlyList<string>? answer)
    {
        Services.AddFormidableBlazor();
        var module = JSInterop.SetupModule("./_content/Formidable.Blazor/formidable.js");
        module.Setup<IReadOnlyList<string>>("orderFields", _ => true).SetResult(answer!);
        return module;
    }
}
