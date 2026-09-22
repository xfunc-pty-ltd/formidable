using FluentValidation;
using Formidable.Blazor.Tests.Fixtures;
using Formidable.Introspection;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Time.Testing;

namespace Formidable.Blazor.Tests;

public class FormValidationEngineAsyncTests
{
    private sealed class GatedValidator : DraftSubmitValidator<EngineOrder>
    {
        public TaskCompletionSource Gate { get; private set; } = new();
        public int Started;
        public CancellationToken LastToken { get; private set; }

        protected override void ConfigureDraftRules()
        {
        }

        protected override void ConfigureSubmitRules() =>
            RuleFor(x => x.Description).MustAsync(async (_, ct) =>
            {
                Started++;
                LastToken = ct;
                await Gate.Task.WaitAsync(ct);
                return false;
            }).WithMessage("async says no");

        public void Reset() => Gate = new TaskCompletionSource();
    }

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
}
