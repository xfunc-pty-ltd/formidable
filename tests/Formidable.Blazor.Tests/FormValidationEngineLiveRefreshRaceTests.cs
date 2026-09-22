using Formidable.Blazor.Tests.Fixtures;
using Formidable.Introspection;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Time.Testing;
using static Formidable.Blazor.Tests.Fixtures.EngineTestSync;

namespace Formidable.Blazor.Tests;

/// <summary>
/// One edit after a submit arms two channels at once: the live pass it starts immediately, and
/// the debounced refresh. These pin that the two cannot eat each other's answer — neither across
/// that pair, nor between two live passes in quick succession.
/// </summary>
public class FormValidationEngineLiveRefreshRaceTests
{
    [Fact]
    public async Task Live_verdict_survives_a_refresh_armed_by_the_same_edit()
    {
        var customer = new EngineCustomer();
        var order = new EngineOrder { Customer = customer };
        var validator = new SlowLiveRuleValidator();
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        using var engine = new FormValidationEngine<EngineOrder>(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(validator),
            new ReflectionModelIntrospector(),
            new FormidableOptions(),
            time);

        var customerName = new FieldIdentifier(customer, nameof(EngineCustomer.Name));

        // A clean submit: no field is an error site, so a refresh would discard its own verdict
        // for any of them - which makes the live pass the only channel that can answer.
        var submit = engine.ValidateForSubmitAsync();
        validator.Gate.SetResult();
        Assert.True((await submit).CanProceed);
        validator.Reset();

        // One edit, both channels: the live pass it starts is held open by the async rule while
        // the refresh the same edit armed comes due.
        customer.Name = "far too long";
        editContext.NotifyFieldChanged(customerName);
        time.Advance(TimeSpan.FromMilliseconds(301));

        var settled = Quiescence(engine);
        validator.Gate.SetResult();
        await settled;

        Assert.Contains(
            engine.GetVisibleIssues(),
            v => v.Field.Equals(customerName) && v.Issue.Message == "Customer name is too long");
    }

    [Fact]
    public async Task Refresh_defers_and_rearms_while_a_live_pass_is_in_flight()
    {
        var customer = new EngineCustomer { Name = "far too long" };
        var order = new EngineOrder { Customer = customer };
        var validator = new SlowLiveRuleValidator();
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        using var engine = new FormValidationEngine<EngineOrder>(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(validator),
            new ReflectionModelIntrospector(),
            new FormidableOptions { DisclosureOverride = _ => true },
            time);

        var customerName = new FieldIdentifier(customer, nameof(EngineCustomer.Name));

        // Submit with the field failing, so it is an error site the refresh - and only the
        // refresh - keeps current.
        var submit = engine.ValidateForSubmitAsync();
        validator.Gate.SetResult();
        await submit;
        Assert.NotEmpty(editContext.GetValidationMessages(customerName));
        validator.Reset();

        // Fix it. The refresh this edit armed comes due while the edit's own live pass is still
        // running.
        customer.Name = "ok";
        editContext.NotifyFieldChanged(customerName);
        time.Advance(TimeSpan.FromMilliseconds(301));

        var liveSettled = Quiescence(engine);
        validator.Gate.SetResult();
        await liveSettled;

        // Deferred, not cancelled: the live pass is the one that just landed, and it does not
        // own submit issues, so the submit-time error still stands.
        Assert.NotEmpty(editContext.GetValidationMessages(customerName));

        var refreshSettled = Quiescence(engine);
        time.Advance(TimeSpan.FromMilliseconds(301)); // the re-armed refresh runs
        await refreshSettled;

        Assert.Empty(editContext.GetValidationMessages(customerName));
    }

    [Fact]
    public async Task Winning_live_pass_carries_superseded_fields_verdicts()
    {
        var customer = new EngineCustomer();
        var item = new EngineItem();
        var order = new EngineOrder { Customer = customer, Items = [item] };
        var validator = new SlowLiveRuleValidator();
        var editContext = new EditContext(order);
        using var engine = new FormValidationEngine<EngineOrder>(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(validator),
            new ReflectionModelIntrospector(),
            new FormidableOptions(),
            new FakeTimeProvider());

        var customerName = new FieldIdentifier(customer, nameof(EngineCustomer.Name));
        var sku = new FieldIdentifier(item, nameof(EngineItem.Sku));

        // No submit here: this is two live passes in quick succession, the shape a fast typist
        // produces across two fields.
        customer.Name = "far too long";
        editContext.NotifyFieldChanged(customerName); // live pass held open by the async rule

        item.Sku = "far too long";
        editContext.NotifyFieldChanged(sku); // supersedes it before it could write a verdict

        var settled = Quiescence(engine);
        validator.Gate.SetResult();
        await settled;

        Assert.Contains(engine.GetIssues(customerName), i => i.Message == "Customer name is too long");
        Assert.Contains(engine.GetIssues(sku), i => i.Message == "SKU is too long");
    }
}
