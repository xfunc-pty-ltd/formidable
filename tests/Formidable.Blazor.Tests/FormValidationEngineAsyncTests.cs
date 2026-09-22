using Formidable.Blazor.Tests.Fixtures;
using Formidable.Introspection;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Time.Testing;

namespace Formidable.Blazor.Tests;

public class FormValidationEngineAsyncTests
{
    [Fact]
    public async Task Newer_submit_supersedes_and_cancels_older_pass()
    {
        var order = new EngineOrder();
        var validator = new GatedValidator();
        var editContext = new EditContext(order);
        using var engine = new FormValidationEngine<EngineOrder>(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(validator),
            new ReflectionModelIntrospector(),
            new FormidableOptions { DisclosureOverride = _ => true },
            new FakeTimeProvider());

        var first = engine.ValidateForSubmitAsync();
        Assert.True(engine.IsValidating);
        var firstToken = validator.LastToken;
        validator.Reset();

        var second = engine.ValidateForSubmitAsync();
        Assert.True(firstToken.IsCancellationRequested); // older pass cancelled

        validator.Gate.SetResult();
        var secondOutcome = await second;
        var firstOutcome = await first;

        Assert.False(secondOutcome.CanProceed);
        Assert.False(firstOutcome.CanProceed);          // superseded outcome reports blocked
        Assert.Equal(2, validator.Started);
        Assert.False(engine.IsValidating);
        Assert.NotEmpty(editContext.GetValidationMessages(new FieldIdentifier(order, nameof(EngineOrder.Description))));
    }

    [Fact]
    public async Task Caller_cancellation_propagates_without_corrupting_state()
    {
        var order = new EngineOrder();
        var validator = new GatedValidator();
        using var engine = new FormValidationEngine<EngineOrder>(
            order, new EditContext(order),
            new FluentValidationModelValidator<EngineOrder>(validator),
            new ReflectionModelIntrospector(),
            new FormidableOptions { DisclosureOverride = _ => true },
            new FakeTimeProvider());
        using var cts = new CancellationTokenSource();

        var pending = engine.ValidateForSubmitAsync(cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.False(engine.IsValidating);
    }

    [Fact]
    public async Task IsValidating_stays_true_while_a_newer_pass_supersedes()
    {
        var order = new EngineOrder();
        var validator = new GatedValidator();
        using var engine = new FormValidationEngine<EngineOrder>(
            order, new EditContext(order),
            new FluentValidationModelValidator<EngineOrder>(validator),
            new ReflectionModelIntrospector(),
            new FormidableOptions { DisclosureOverride = _ => true },
            new FakeTimeProvider());

        var first = engine.ValidateForSubmitAsync();
        validator.Reset();
        var second = engine.ValidateForSubmitAsync();

        Assert.True(engine.IsValidating); // superseded first pass must not flip it false

        validator.Gate.SetResult();
        await second;
        await first;

        Assert.False(engine.IsValidating);
    }

    [Fact]
    public async Task Field_change_does_not_cancel_in_flight_submit()
    {
        var order = new EngineOrder();
        var validator = new GatedValidator();
        var editContext = new EditContext(order);
        using var engine = new FormValidationEngine<EngineOrder>(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(validator),
            new ReflectionModelIntrospector(),
            new FormidableOptions { DisclosureOverride = _ => true },
            new FakeTimeProvider());

        var submit = engine.ValidateForSubmitAsync();
        var submitToken = validator.LastToken;

        editContext.NotifyFieldChanged(new FieldIdentifier(order, nameof(EngineOrder.Description)));

        Assert.False(submitToken.IsCancellationRequested); // live pass must not supersede the submit

        validator.Gate.SetResult();
        var outcome = await submit;

        Assert.False(outcome.CanProceed);
        Assert.NotEmpty(outcome.Report.Issues); // real report, not the quiet ValidationReport.Empty
    }

    [Fact]
    public async Task Throwing_live_rule_surfaces_form_level_fault()
    {
        var order = new EngineOrder();
        var validator = new ThrowingValidator();
        var editContext = new EditContext(order);
        using var engine = new FormValidationEngine<EngineOrder>(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(validator),
            new ReflectionModelIntrospector(),
            new FormidableOptions(), new FakeTimeProvider());
        Exception? observed = null;
        engine.ValidationFaulted += ex => observed = ex;

        editContext.NotifyFieldChanged(new FieldIdentifier(order, nameof(EngineOrder.Description)));
        await Task.Yield();

        Assert.NotNull(observed);
        Assert.Contains(
            editContext.GetValidationMessages(new FieldIdentifier(order, string.Empty)),
            m => m.Contains("could not run to completion"));

        validator.Throw = false;
        editContext.NotifyFieldChanged(new FieldIdentifier(order, nameof(EngineOrder.Description)));
        await Task.Yield();

        Assert.Empty(editContext.GetValidationMessages(new FieldIdentifier(order, string.Empty)));
    }
}
