using Formidable.Blazor.Tests.Fixtures;
using Formidable.Introspection;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Time.Testing;

namespace Formidable.Blazor.Tests;

public class FormidableEngineAsyncTests
{
    [Fact]
    public async Task Newer_submit_supersedes_and_cancels_older_pass()
    {
        var order = new EngineOrder();
        var validator = new GatedValidator();
        var editContext = new EditContext(order);
        using var engine = new FormidableEngine<EngineOrder>(
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
        using var engine = new FormidableEngine<EngineOrder>(
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

    // The rule never reads its token, so the caller's cancellation cannot stop it short: the rule
    // runs to completion and fails. The call still throws for the caller's own token, and nothing
    // the rule answered lands.
    // Mutation: drop the token check at the verdict dispatch, and the call returns a blocked
    // outcome with HasSubmitted true and the rule's message showing.
    [Fact]
    public async Task A_submit_cancelled_while_a_token_ignoring_rule_runs_throws_and_lands_nothing()
    {
        var order = new EngineOrder();
        var validator = new CancellationIgnoringValidator { ShouldPass = false };
        using var engine = new FormidableEngine<EngineOrder>(
            order, new EditContext(order),
            new FluentValidationModelValidator<EngineOrder>(validator),
            new ReflectionModelIntrospector(),
            new FormidableOptions { DisclosureOverride = _ => true },
            new FakeTimeProvider());
        using var cts = new CancellationTokenSource();

        var pending = engine.ValidateForSubmitAsync(cts.Token);
        Assert.Equal(1, validator.Started);
        cts.Cancel();
        validator.Gate.SetResult(); // the rule answers (and fails) whatever the token says

        var thrown = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(cts.Token, thrown.CancellationToken);
        Assert.False(engine.HasSubmitted);
        Assert.Empty(engine.GetVisibleIssues());
        Assert.False(engine.IsValidating);
    }

    // A pin: a token cancelled once the answer has landed changes nothing. The cancel comes from a
    // handler of the landing's own notification, after every write and before the call returns.
    // Mutation: throw whenever the caller's token reads cancelled after the landing dispatch, not
    // only when that dispatch found it cancelled, and the call throws OperationCanceledException.
    [Fact]
    public async Task A_token_cancelled_after_the_answer_lands_changes_nothing()
    {
        var order = new EngineOrder();
        using var engine = new FormidableEngine<EngineOrder>(
            order, new EditContext(order),
            new FluentValidationModelValidator<EngineOrder>(new EngineOrderValidator()),
            new ReflectionModelIntrospector(),
            new FormidableOptions { DisclosureOverride = _ => true },
            new FakeTimeProvider());
        using var cts = new CancellationTokenSource();
        var sawValidating = false;
        engine.StateChanged += (_, _) =>
        {
            if (engine.IsValidating)
            {
                sawValidating = true;
            }
            else if (sawValidating)
            {
                cts.Cancel(); // the landing's rebuild: the answer is written
            }
        };

        var outcome = await engine.ValidateForSubmitAsync(cts.Token);

        Assert.True(cts.IsCancellationRequested); // the control: the cancel came before the return
        Assert.False(outcome.CanProceed);
        Assert.NotEmpty(outcome.VisibleErrorSummary);
        Assert.True(engine.HasSubmitted);
        Assert.NotEmpty(engine.GetVisibleIssues());
    }

    // A caller who cancelled hears so even when a newer submit also displaced the check, as it
    // would from a rule that read the token. The newer submit's own answer is untouched.
    // Mutation: test the version before the token at the verdict dispatch, and the first call
    // returns the displaced, blocked outcome instead of throwing.
    [Fact]
    public async Task A_submit_both_displaced_and_cancelled_throws_for_its_caller()
    {
        var order = new EngineOrder();
        var validator = new CancellationIgnoringValidator();
        using var engine = new FormidableEngine<EngineOrder>(
            order, new EditContext(order),
            new FluentValidationModelValidator<EngineOrder>(validator),
            new ReflectionModelIntrospector(),
            new FormidableOptions { DisclosureOverride = _ => true },
            new FakeTimeProvider());
        using var cts = new CancellationTokenSource();

        var first = engine.ValidateForSubmitAsync(cts.Token);
        var second = engine.ValidateForSubmitAsync();
        Assert.Equal(2, validator.Started);
        cts.Cancel();
        validator.Gate.SetResult();

        var thrown = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Equal(cts.Token, thrown.CancellationToken);
        Assert.True((await second).CanProceed);
        Assert.True(engine.HasSubmitted);
        Assert.False(engine.IsValidating);
    }

    // Disposal moves the engine on from the pass in flight, whether or not its rule reads the
    // token the disposal cancels. This rule ignores it and passes, so only the disposal can keep
    // the answer from landing.
    // Mutation: leave the version alone in Dispose, and the submit reports CanProceed=true, and
    // the load lands and then throws ObjectDisposedException from the live check it starts on
    // the disposed engine.
    // Mutation: leave the pass unretired in Dispose, and IsValidating reads true for good.
    [Theory]
    [InlineData("submit")]
    [InlineData("load")]
    public async Task A_pass_in_flight_when_the_engine_is_disposed_never_lands(string kind)
    {
        var order = new EngineOrder { Description = "Loaded" };
        var validator = new CancellationIgnoringValidator(); // passes once released
        var engine = new FormidableEngine<EngineOrder>(
            order, new EditContext(order),
            new FluentValidationModelValidator<EngineOrder>(validator),
            new ReflectionModelIntrospector(),
            new FormidableOptions { DisclosureOverride = _ => true },
            new FakeTimeProvider());
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));

        if (kind == "submit")
        {
            var pending = engine.ValidateForSubmitAsync();
            Assert.Equal(1, validator.Started);
            engine.Dispose();
            validator.Gate.SetResult();

            var outcome = await pending;

            Assert.False(outcome.CanProceed);
            Assert.False(engine.HasSubmitted);
        }
        else
        {
            var pending = engine.DiscloseLoadedValuesAsync();
            Assert.Equal(1, validator.Started);
            engine.Dispose();
            validator.Gate.SetResult();

            await pending;

            Assert.False(engine.GetFieldState(description).IsTouched);
            Assert.Empty(engine.GetVisibleIssues());
        }

        Assert.False(engine.IsValidating);
    }

    [Fact]
    public async Task IsValidating_stays_true_while_a_newer_pass_supersedes()
    {
        var order = new EngineOrder();
        var validator = new GatedValidator();
        using var engine = new FormidableEngine<EngineOrder>(
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
        using var engine = new FormidableEngine<EngineOrder>(
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
        using var engine = new FormidableEngine<EngineOrder>(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(validator),
            new ReflectionModelIntrospector(),
            new FormidableOptions(), new FakeTimeProvider());
        Exception? observed = null;
        engine.ValidationFaulted += (_, e) => observed = e.Exception;

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

    // The mirror of the live-fault test above, and the half that is easy to lose: a submit is a
    // pass someone is awaiting, so a validator that throws under it belongs to that caller. The
    // pass DiscloseLoadedValuesAsync runs is awaited too and rethrows identically.
    // Live and refresh are fire-and-forget, which is why their faults become form state plus the
    // ValidationFaulted event instead - a submit must do neither, or a caller's try/catch silently
    // stops seeing failures it used to handle and gets a quietly-blocked outcome in their place.
    [Fact]
    public async Task Throwing_submit_rule_propagates_to_the_caller_instead_of_reporting_a_fault()
    {
        var order = new EngineOrder();
        var validator = new ThrowingValidator();
        var editContext = new EditContext(order);
        using var engine = new FormidableEngine<EngineOrder>(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(validator),
            new ReflectionModelIntrospector(),
            // ThrowingValidator's rule sits on the Draft ruleset; running the submit pass under
            // that profile is what puts the throw on the submit path rather than the live one.
            new FormidableOptions { SubmitProfile = ValidationProfile.Draft },
            new FakeTimeProvider());
        Exception? observed = null;
        engine.ValidationFaulted += (_, e) => observed = e.Exception;

        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.ValidateForSubmitAsync());

        Assert.Null(observed); // no fault event: the exception went to the caller, not to a subscriber
        Assert.DoesNotContain(
            editContext.GetValidationMessages(new FieldIdentifier(order, string.Empty)),
            m => m.Contains("could not run to completion"));
        Assert.False(engine.IsValidating); // the pass still ended, however it left
    }
}
