using FluentValidation;
using Formidable.Blazor.Tests.Fixtures;
using Formidable.Introspection;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Time.Testing;

namespace Formidable.Blazor.Tests;

/// <summary>
/// <see cref="FormidableOptions.TrackFormValidity"/> under a <see cref="FormidableOptions.LiveDebounce"/>
/// of <see cref="Timeout.InfiniteTimeSpan"/>, a window that never closes. Before the first submit
/// or server reply, a committed change arms the validity check at
/// <see cref="FormidableOptions.RefreshDebounce"/>, and each change after it re-arms the same
/// check, so a burst of changes runs one check that long after the last. The check counts as one
/// on its way for the valid class while it is armed and tracking stays on. Once it runs, it counts
/// whatever tracking says, until it lands, faults or is cancelled, or until
/// <c>SubmitCoverageTracker.HeldVouchBound</c> has passed since it started. Every engine here is
/// built directly on a fake clock, so each timer lands only when a test advances it.
/// </summary>
public class NeverClosingWindowValidityTests
{
    private static readonly TimeSpan Refresh = new FormidableOptions().RefreshDebounce;

    private static FormidableOptions NeverClosing() =>
        new() { TrackFormValidity = true, LiveDebounce = Timeout.InfiniteTimeSpan };

    private static FormidableEngine<EngineOrder> Build(
        EngineOrder order,
        EditContext editContext,
        IModelValidator<EngineOrder> validator,
        FormidableOptions options,
        FakeTimeProvider time,
        Func<Func<Task>, Task>? renderDispatch = null) =>
        new(order, editContext, validator, new ReflectionModelIntrospector(), options, time, renderDispatch);

    private static FieldIdentifier Description(EngineOrder order) =>
        new(order, nameof(EngineOrder.Description));

    private static FieldIdentifier CustomerName(EngineOrder order) =>
        new(order.Customer!, nameof(EngineCustomer.Name));

    /// <summary>
    /// Yields until <paramref name="condition"/> holds, for at most five seconds. A released gate's
    /// continuation is posted to the test's own context, so the check behind it lands only once the
    /// test yields; the caller asserts afterwards, so a condition that never comes true fails there.
    /// </summary>
    private static async Task UntilAsync(Func<bool> condition)
    {
        for (var waited = 0; waited < 500 && !condition(); waited++)
        {
            await Task.Delay(10);
        }
    }

    // Mutation: arm the validity check at LiveDebounce instead of RefreshDebounce. Under a window
    // that never closes that timer never fires, so IsFormValid stays false.
    [Fact]
    public void Under_a_never_closing_window_a_change_checks_validity_after_RefreshDebounce()
    {
        var order = new EngineOrder { Customer = new EngineCustomer { Name = "Bo" } };
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        using var engine = Build(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(new CountingTwoFieldValidator()),
            NeverClosing(), time);
        Assert.False(engine.IsFormValid);

        order.Description = "ok";
        editContext.NotifyFieldChanged(Description(order));

        time.Advance(Refresh - TimeSpan.FromTicks(1));
        Assert.False(engine.IsFormValid);

        time.Advance(TimeSpan.FromTicks(1));
        Assert.True(engine.IsFormValid);
    }

    // Mutation: give each change a timer of its own rather than re-arming the one. The first
    // change's timer then still fires 300 ms after it, and the second's 300 ms after that: two
    // checks where one was owed.
    [Fact]
    public void Changes_inside_the_wait_coalesce_into_one_check()
    {
        var order = new EngineOrder { Customer = new EngineCustomer { Name = "Bo" } };
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        var counting = new CountingValidator<EngineOrder>(
            new FluentValidationModelValidator<EngineOrder>(new CountingTwoFieldValidator()));
        using var engine = Build(order, editContext, counting, NeverClosing(), time);
        var afterBuild = counting.CallCount; // the check every tracked form runs as it is built

        order.Description = "o";
        editContext.NotifyFieldChanged(Description(order));
        time.Advance(TimeSpan.FromMilliseconds(200));
        order.Description = "ok";
        editContext.NotifyFieldChanged(Description(order));
        time.Advance(Refresh);

        Assert.Equal(afterBuild + 1, counting.CallCount);

        time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(afterBuild + 1, counting.CallCount);
        Assert.True(engine.IsFormValid);
    }

    // A pin: it holds whether or not a never-closing window arms a check, and guards the cadence
    // every other window keeps.
    // Mutation: drop the never-closing condition, so every LiveDebounce arms the validity check.
    // Each value here then pays one more whole-form check RefreshDebounce after the change, on top
    // of the live check and the validity check its own cadence already ran.
    [Theory]
    [InlineData(null)]
    [InlineData(150)]
    public void A_null_or_finite_LiveDebounce_keeps_its_cadence(int? liveDebounceMs)
    {
        var order = new EngineOrder { Customer = new EngineCustomer { Name = "Bo" } };
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        var counting = new CountingValidator<EngineOrder>(
            new FluentValidationModelValidator<EngineOrder>(new CountingTwoFieldValidator()));
        var options = new FormidableOptions
        {
            TrackFormValidity = true,
            LiveDebounce = liveDebounceMs is { } ms ? TimeSpan.FromMilliseconds(ms) : null
        };
        using var engine = Build(order, editContext, counting, options, time);
        var afterBuild = counting.CallCount;

        order.Description = "ok";
        editContext.NotifyFieldChanged(Description(order));
        time.Advance(TimeSpan.FromMilliseconds(150)); // closes the finite window; nothing waits on null

        // The validator here hides its rule-by-rule capability, so each check is one call: the
        // change's live check and the validity check that rides its cadence.
        var afterCadence = counting.CallCount;
        Assert.Equal(afterBuild + 2, afterCadence);

        time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(afterCadence, counting.CallCount);
    }

    // Mutation: the fire files nothing (it clears the flag and returns without starting the
    // check). The edited field's submit rules then never answer for its new value, so it never
    // wears green.
    [Fact]
    public void A_passing_edited_field_wears_valid_when_the_check_lands()
    {
        var order = new EngineOrder { Customer = new EngineCustomer { Name = "Bo" } };
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        using var engine = Build(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(new CountingTwoFieldValidator()),
            NeverClosing(), time);
        var description = Description(order);

        order.Description = "ok";
        editContext.NotifyFieldChanged(description);

        Assert.False(engine.GetFieldState(description).WouldPassSubmit);
        Assert.Equal(string.Empty, editContext.FieldCssClass(description));

        time.Advance(Refresh);

        Assert.True(engine.GetFieldState(description).WouldPassSubmit);
        Assert.Equal("formidable-valid", editContext.FieldCssClass(description));
    }

    // Mutation: drop the armed-check arm from ReAnswerOnItsWay. With no live check ever starting
    // under this window, the armed validity check is the only re-answer on its way, so without it
    // the other field's green goes the moment the edit moves the model.
    [Fact]
    public void A_green_field_keeps_green_across_another_fields_edit()
    {
        var order = new EngineOrder { Description = "ok", Customer = new EngineCustomer { Name = "Bo" } };
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        using var engine = Build(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(new CountingTwoFieldValidator()),
            NeverClosing(), time);
        var description = Description(order);
        var name = CustomerName(order);

        // The name earns its green: a committed change, then the check that change armed.
        order.Customer!.Name = "Bea";
        editContext.NotifyFieldChanged(name);
        time.Advance(Refresh);
        Assert.Equal("formidable-valid", editContext.FieldCssClass(name));

        order.Description = "still ok";
        editContext.NotifyFieldChanged(description);

        // Before the check this edit armed has landed.
        Assert.True(engine.GetFieldState(name).WouldPassSubmit);
        Assert.Equal("formidable-valid", editContext.FieldCssClass(name));
        Assert.False(engine.GetFieldState(description).WouldPassSubmit);

        time.Advance(Refresh);

        Assert.True(engine.GetFieldState(name).WouldPassSubmit);
        Assert.True(engine.GetFieldState(description).WouldPassSubmit);
    }

    // A pin: it holds whether or not a never-closing window arms a check, and guards the one rule
    // run per edit a submit leaves to the whole-form re-check.
    // Mutation: arm the validity check whatever a submit says, at both guards (the arm site's
    // HasSubmitted test in HandleFieldChanged and the fire's answered-submit test). The check then
    // fires beside the whole-form re-check the same change armed, and with the async rule still
    // out neither can reuse the other's answer, so the rule runs twice. After a client submit,
    // dropping either guard alone leaves the other one holding, and the test passes.
    [Fact]
    public async Task After_a_submit_one_change_runs_an_async_rule_once()
    {
        var order = new EngineOrder { Description = "ok", Customer = new EngineCustomer { Name = "Bo" } };
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        var validator = new GatedCountingValidator();
        validator.Gate.SetResult();
        using var engine = Build(
            order, editContext, new FluentValidationModelValidator<EngineOrder>(validator),
            NeverClosing(), time);
        Assert.True((await engine.ValidateForSubmitAsync()).CanProceed);
        Assert.True(engine.IsFormValid);

        validator.Reset();
        order.Description = string.Empty;
        editContext.NotifyFieldChanged(Description(order));
        time.Advance(Refresh);

        Assert.Equal(1, validator.Runs);

        // The whole-form re-check lands and answers IsFormValid for the emptied value.
        validator.Gate.SetResult();
        await UntilAsync(() => !engine.IsFormValid);
        Assert.Equal(1, validator.Runs);
        Assert.False(engine.IsFormValid);
    }

    // A pin: it holds whether or not a never-closing window arms a check, and guards the fire's
    // stand-down once a submit has answered.
    // Mutation: drop the answered-submit test from the fire. A check armed by a change before the
    // submit then still runs once the submit has answered, one more whole-form check for nothing.
    [Fact]
    public async Task A_check_armed_before_a_submit_stands_down_once_the_submit_answers()
    {
        var order = new EngineOrder { Customer = new EngineCustomer { Name = "Bo" } };
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        var counting = new CountingValidator<EngineOrder>(
            new FluentValidationModelValidator<EngineOrder>(new CountingTwoFieldValidator()));
        using var engine = Build(order, editContext, counting, NeverClosing(), time);

        order.Description = "ok";
        editContext.NotifyFieldChanged(Description(order));
        time.Advance(TimeSpan.FromMilliseconds(100));
        Assert.True((await engine.ValidateForSubmitAsync()).CanProceed);
        var afterSubmit = counting.CallCount;

        time.Advance(Refresh);

        Assert.Equal(afterSubmit, counting.CallCount);
        Assert.True(engine.IsFormValid);
    }

    // A pin: a change to which fields are on screen already re-checks the whole form after
    // RefreshDebounce at any point in the form's life, and that re-check answers IsFormValid, so
    // a row a visitor adds moves it before any submit under this window too.
    // Mutation: OnRenderedFieldsChanged arms no refresh. IsFormValid then keeps the answer the
    // form was built with.
    [Fact]
    public void A_field_set_change_moves_IsFormValid_under_a_never_closing_window()
    {
        var order = new EngineOrder { Description = "Valid", Customer = new EngineCustomer { Name = "Bo" } };
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        using var engine = Build(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(new EngineOrderValidator()),
            NeverClosing(), time);
        Assert.True(engine.IsFormValid);

        // A row arrives with an empty SKU: a value no field-changed notification announces.
        order.Items.Add(new EngineItem());
        engine.OnRenderedFieldsChanged();
        Assert.True(engine.IsFormValid);

        time.Advance(Refresh);

        Assert.False(engine.IsFormValid);
    }

    // A pin of the edge the docs state: with RefreshDebounce never closing as well, the armed
    // check never fires, so IsFormValid waits for a submit (or a load of values).
    // Mutation: arm the check at a fixed 300 ms rather than at RefreshDebounce. It then fires
    // within the hour and IsFormValid moves before the submit.
    [Fact]
    public async Task Both_windows_never_closing_leave_IsFormValid_to_a_submit_or_load()
    {
        var order = new EngineOrder { Customer = new EngineCustomer { Name = "Bo" } };
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        var options = NeverClosing();
        options.RefreshDebounce = Timeout.InfiniteTimeSpan;
        using var engine = Build(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(new CountingTwoFieldValidator()),
            options, time);

        order.Description = "ok";
        editContext.NotifyFieldChanged(Description(order));
        time.Advance(TimeSpan.FromHours(1));
        Assert.False(engine.IsFormValid);

        Assert.True((await engine.ValidateForSubmitAsync()).CanProceed);
        Assert.True(engine.IsFormValid);
    }

    // A pin: it holds whether or not a never-closing window arms a check, and guards the promise
    // of a timer that never fires.
    // Mutation: drop the finite-RefreshDebounce conjunct from the armed-check arm of
    // ReAnswerOnItsWay. A check armed on a timer that never fires would then hold the other
    // field's green for good.
    [Fact]
    public void Both_windows_never_closing_hold_no_green_across_an_edit()
    {
        var order = new EngineOrder { Description = "ok", Customer = new EngineCustomer { Name = "Bo" } };
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        var options = NeverClosing();
        options.RefreshDebounce = Timeout.InfiniteTimeSpan;
        using var engine = Build(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(new CountingTwoFieldValidator()),
            options, time);
        var name = CustomerName(order);

        // The check the form ran as it was built answers at this edit stamp, and this read is
        // the one that holds it.
        Assert.True(engine.GetFieldState(name).WouldPassSubmit);

        order.Description = "still ok";
        editContext.NotifyFieldChanged(Description(order));

        Assert.False(engine.GetFieldState(name).WouldPassSubmit);
    }

    // A pin: it holds whether or not a never-closing window arms a check, and guards the fire's
    // stand-down once tracking is off.
    // Mutation: drop the TrackFormValidity test from the fire. The check then runs its rules
    // against the fixed value, and IsFormValid flips to true with tracking off.
    [Fact]
    public void An_armed_validity_check_runs_nothing_once_tracking_is_off()
    {
        var order = new EngineOrder { Customer = new EngineCustomer { Name = "Bo" } };
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        var validator = new CountingTwoFieldValidator();
        var options = NeverClosing();
        using var engine = Build(
            order, editContext, new FluentValidationModelValidator<EngineOrder>(validator),
            options, time);
        Assert.False(engine.IsFormValid);
        var faulted = 0;
        engine.ValidationFaulted += (_, _) => faulted++;

        order.Description = "ok";
        editContext.NotifyFieldChanged(Description(order));
        var runsBeforeFire = validator.Runs;
        options.TrackFormValidity = false;

        var thrown = Record.Exception(() => time.Advance(Refresh));

        Assert.Null(thrown);
        Assert.Equal(runsBeforeFire, validator.Runs);
        Assert.Equal(0, faulted);

        // Read with tracking back on, so the read itself writes no untracked-read note.
        options.TrackFormValidity = true;
        Assert.False(engine.IsFormValid);
    }

    // Mutation: drop the TrackFormValidity conjunct from the armed-check arm of
    // ReAnswerOnItsWay. A check whose fire will stand down would then still hold the other
    // field's green across the edit.
    [Fact]
    public void An_armed_check_holds_no_green_once_tracking_is_off()
    {
        var order = new EngineOrder { Description = "ok", Customer = new EngineCustomer { Name = "Bo" } };
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        var options = NeverClosing();
        using var engine = Build(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(new CountingTwoFieldValidator()),
            options, time);
        var name = CustomerName(order);
        Assert.True(engine.GetFieldState(name).WouldPassSubmit);

        order.Description = "still ok";
        editContext.NotifyFieldChanged(Description(order));
        Assert.True(engine.GetFieldState(name).WouldPassSubmit);

        options.TrackFormValidity = false;

        Assert.False(engine.GetFieldState(name).WouldPassSubmit);
    }

    // A pin: it holds whether or not a never-closing window arms a check, and guards the fire's
    // stand-down after disposal.
    // Mutation: drop both the timer's disposal and the fire's disposed test. The fire then reaches
    // the check, whose token read on the disposed source throws, and that surfaces as
    // ValidationFaulted. Dropping either one alone leaves the other holding.
    [Fact]
    public void An_armed_validity_check_runs_nothing_after_the_engine_is_disposed()
    {
        var order = new EngineOrder { Customer = new EngineCustomer { Name = "Bo" } };
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        var validator = new CountingTwoFieldValidator();
        var engine = Build(
            order, editContext, new FluentValidationModelValidator<EngineOrder>(validator),
            NeverClosing(), time);
        Assert.False(engine.IsFormValid);
        var faulted = 0;
        engine.ValidationFaulted += (_, _) => faulted++;

        order.Description = "ok";
        editContext.NotifyFieldChanged(Description(order));
        var runsBeforeFire = validator.Runs;
        engine.Dispose();

        var thrown = Record.Exception(() => time.Advance(Refresh));

        Assert.Null(thrown);
        Assert.Equal(runsBeforeFire, validator.Runs);
        Assert.Equal(0, faulted);
        Assert.False(engine.IsFormValid);
    }

    // Builds the scene the next four tests share: the name has earned its green, the gate is
    // closed, a description edit has armed the check, and the check's fire has started it, so it
    // now waits on the name's async rule.
    private static FormidableEngine<EngineOrder> BuildRunningCheck(
        EngineOrder order,
        EditContext editContext,
        GatedNameValidator validator,
        FakeTimeProvider time,
        Func<Func<Task>, Task>? renderDispatch = null)
    {
        validator.Gate.SetResult();
        var engine = Build(
            order, editContext, new FluentValidationModelValidator<EngineOrder>(validator),
            NeverClosing(), time, renderDispatch);
        var name = CustomerName(order);

        order.Customer!.Name = "Bea";
        editContext.NotifyFieldChanged(name);
        time.Advance(Refresh);
        Assert.Equal("formidable-valid", editContext.FieldCssClass(name));

        validator.Reset();
        order.Description = "still ok";
        editContext.NotifyFieldChanged(Description(order));
        time.Advance(Refresh);
        Assert.Equal(1, validator.Started);
        return engine;
    }

    // Mutation: count only the armed timer, not the check its fire started. Once the fire has
    // started the check, nothing counts as on its way while the async rule is out, so a re-render
    // of the name drops its green before any answer has arrived.
    [Fact]
    public async Task A_running_check_keeps_other_fields_green_until_it_lands_and_its_answer_decides()
    {
        var order = new EngineOrder { Description = "ok", Customer = new EngineCustomer { Name = "Bo" } };
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        var validator = new GatedNameValidator();
        using var engine = BuildRunningCheck(order, editContext, validator, time);
        var name = CustomerName(order);

        // A re-render while the rule is out.
        Assert.True(engine.GetFieldState(name).WouldPassSubmit);
        Assert.Equal("formidable-valid", editContext.FieldCssClass(name));

        // The check lands failing the name: the border it held gives way to the answer.
        validator.ShouldPass = false;
        validator.Gate.SetResult();
        await UntilAsync(() => !engine.GetFieldState(name).WouldPassSubmit);

        Assert.False(engine.GetFieldState(name).WouldPassSubmit);
        Assert.Equal(string.Empty, editContext.FieldCssClass(name));
    }

    // Mutation: drop the bound from the running check's arm of ReAnswerOnItsWay. A check whose
    // rule never answers would then hold the name's green for good.
    [Fact]
    public void A_running_check_holds_other_fields_green_only_for_the_bound()
    {
        var order = new EngineOrder { Description = "ok", Customer = new EngineCustomer { Name = "Bo" } };
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        var validator = new GatedNameValidator();
        using var engine = BuildRunningCheck(order, editContext, validator, time);
        var name = CustomerName(order);

        time.Advance(SubmitCoverageTracker.HeldVouchBound - TimeSpan.FromTicks(1));
        Assert.True(engine.GetFieldState(name).WouldPassSubmit);

        time.Advance(TimeSpan.FromTicks(1));
        Assert.False(engine.GetFieldState(name).WouldPassSubmit);
    }

    // Mutations: (a) the fault exit leaves the check counted, so the name keeps its green until
    // the bound; (b) the fault exit drops the hold without publishing, so no surface hears it.
    [Fact]
    public async Task A_running_check_that_throws_drops_the_hold_and_says_so()
    {
        var order = new EngineOrder { Description = "ok", Customer = new EngineCustomer { Name = "Bo" } };
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        var validator = new GatedNameValidator();
        using var engine = BuildRunningCheck(order, editContext, validator, time);
        var name = CustomerName(order);
        var faulted = 0;
        var published = 0;
        engine.ValidationFaulted += (_, _) => faulted++;
        engine.StateChanged += (_, _) => published++;

        validator.ThrowOnRelease = true;
        validator.Gate.SetResult();
        await UntilAsync(() => faulted > 0 && published > 0);

        Assert.Equal(1, faulted);
        Assert.True(published > 0);
        Assert.False(engine.GetFieldState(name).WouldPassSubmit);
    }

    // Mutation: raise ValidationFaulted before ending the check. A handler that throws then skips
    // the end, so the check stays counted and the name keeps its green on a check that has already
    // died, until the bound.
    [Fact]
    public async Task A_running_check_that_throws_drops_the_hold_even_when_a_fault_handler_throws()
    {
        var order = new EngineOrder { Description = "ok", Customer = new EngineCustomer { Name = "Bo" } };
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        var validator = new GatedNameValidator();
        using var engine = BuildRunningCheck(order, editContext, validator, time);
        var name = CustomerName(order);
        var faulted = 0;
        var published = 0;
        engine.StateChanged += (_, _) => published++;
        engine.ValidationFaulted += (_, _) =>
        {
            faulted++;
            throw new InvalidOperationException("handler blew up");
        };

        validator.ThrowOnRelease = true;
        validator.Gate.SetResult();
        await UntilAsync(() => faulted > 0);

        Assert.Equal(1, faulted);
        Assert.True(published > 0);
        Assert.False(engine.GetFieldState(name).WouldPassSubmit);
    }

    // Mutations: (a) the cancel exit leaves the check counted, so a rule that cancels itself, with
    // the engine still live, holds the name's green until the bound; (b) the cancel exit drops the
    // hold without publishing.
    [Fact]
    public async Task A_running_check_whose_rule_cancels_drops_the_hold()
    {
        var order = new EngineOrder { Description = "ok", Customer = new EngineCustomer { Name = "Bo" } };
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        var validator = new GatedNameValidator();
        using var engine = BuildRunningCheck(order, editContext, validator, time);
        var name = CustomerName(order);
        var faulted = 0;
        var published = 0;
        engine.ValidationFaulted += (_, _) => faulted++;
        engine.StateChanged += (_, _) => published++;

        validator.CancelOnRelease = true;
        validator.Gate.SetResult();
        await UntilAsync(() => published > 0);

        Assert.Equal(0, faulted);
        Assert.True(published > 0);
        Assert.False(engine.GetFieldState(name).WouldPassSubmit);
    }

    // Mutation: the landing leaves the check counted. A check that has already answered would
    // then hold the name's green across a later edit that nothing will answer, here one made with
    // tracking off, until the bound.
    [Fact]
    public void A_landed_check_promises_nothing_more()
    {
        var order = new EngineOrder { Description = "ok", Customer = new EngineCustomer { Name = "Bo" } };
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        var options = NeverClosing();
        using var engine = Build(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(new CountingTwoFieldValidator()),
            options, time);
        var name = CustomerName(order);

        order.Customer!.Name = "Bea";
        editContext.NotifyFieldChanged(name);
        time.Advance(Refresh);
        Assert.True(engine.GetFieldState(name).WouldPassSubmit);

        options.TrackFormValidity = false;
        order.Description = "still ok";
        editContext.NotifyFieldChanged(Description(order));

        Assert.False(engine.GetFieldState(name).WouldPassSubmit);
    }

    // Mutation: end the running check from any probe, whichever fire started it. The older
    // check's landing would then end the newer one, still out on its rule, and the name's green
    // would drop before any answer for the second edit had arrived.
    [Fact]
    public async Task An_older_check_landing_leaves_the_newer_one_counted()
    {
        var order = new EngineOrder { Description = "ok", Customer = new EngineCustomer { Name = "Bo" } };
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        var validator = new GatedNameValidator();
        var dispatched = 0;
        using var engine = BuildRunningCheck(
            order, editContext, validator, time,
            work =>
            {
                var done = work();
                Interlocked.Increment(ref dispatched);
                return done;
            });
        var name = CustomerName(order);
        var firstGate = validator.Gate;

        // A second edit, and its check fires on a gate of its own while the first is still out.
        validator.Reset();
        order.Description = "edited again";
        editContext.NotifyFieldChanged(Description(order));
        time.Advance(Refresh);
        Assert.Equal(1, validator.Started);

        // The first check lands; its filing is refused, since an edit came after it began.
        var before = Volatile.Read(ref dispatched);
        firstGate.SetResult();
        await UntilAsync(() => Volatile.Read(ref dispatched) > before);
        Assert.True(Volatile.Read(ref dispatched) > before);

        Assert.True(engine.GetFieldState(name).WouldPassSubmit);
    }

    // Mutation: stand the fire down behind a load in flight, as it stands down once a submit has
    // answered. The load's answer predates the edit made while it ran, and nothing then answers
    // for that edit.
    [Fact]
    public async Task A_check_that_fires_during_a_load_waits_for_it_and_then_runs()
    {
        var order = new EngineOrder { Customer = new EngineCustomer { Name = "Bo" } };
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        var validator = new GatedCountingValidator();
        validator.Gate.SetResult();
        using var engine = Build(
            order, editContext, new FluentValidationModelValidator<EngineOrder>(validator),
            NeverClosing(), time);
        Assert.False(engine.IsFormValid);

        // The load reads the empty description and waits on the rule.
        validator.Reset();
        var load = engine.DiscloseLoadedValuesAsync();

        order.Description = "ok";
        editContext.NotifyFieldChanged(Description(order));
        time.Advance(Refresh); // the check fires while the load is still out

        validator.Gate.SetResult();
        await load;
        Assert.False(engine.IsFormValid); // the load's answer, for the value it read

        time.Advance(Refresh);
        await UntilAsync(() => engine.IsFormValid);

        Assert.True(engine.IsFormValid);
    }

    // Mutation: let the fire start the check behind a submit in flight, where the check stands
    // down at once. A submit the caller cancels then answers nothing, and nothing answers for the
    // edit made before it.
    [Fact]
    public async Task A_check_that_fires_during_a_submit_runs_once_the_submit_ends_unanswered()
    {
        var order = new EngineOrder { Customer = new EngineCustomer { Name = "Bo" } };
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        var validator = new GatedCountingValidator();
        validator.Gate.SetResult();
        using var engine = Build(
            order, editContext, new FluentValidationModelValidator<EngineOrder>(validator),
            NeverClosing(), time);
        Assert.False(engine.IsFormValid);

        order.Description = "ok";
        editContext.NotifyFieldChanged(Description(order));
        validator.Reset();
        using var cancel = new CancellationTokenSource();
        var submit = engine.ValidateForSubmitAsync(cancel.Token);
        time.Advance(Refresh); // the check fires while the submit is still out

        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => submit);
        Assert.False(engine.HasSubmitted);

        validator.Gate.SetResult();
        time.Advance(Refresh);
        await UntilAsync(() => engine.IsFormValid);

        Assert.True(engine.IsFormValid);
    }

    // Mutation: stand the fire down on HasSubmitted, which a server reply sets, rather than on a
    // submit having answered. The check armed just before the reply then never runs, and nothing
    // answers IsFormValid for that edit.
    [Fact]
    public void A_server_reply_leaves_a_check_already_armed_to_run()
    {
        var order = new EngineOrder { Customer = new EngineCustomer { Name = "Bo" } };
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        using var engine = Build(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(new CountingTwoFieldValidator()),
            NeverClosing(), time);
        Assert.False(engine.IsFormValid);

        order.Description = "ok";
        editContext.NotifyFieldChanged(Description(order));
        time.Advance(TimeSpan.FromMilliseconds(100));
        engine.ApplyServerIssues([]);

        time.Advance(Refresh);

        Assert.True(engine.IsFormValid);
    }

    // A pin: it holds whether or not the arm site tests HasSubmitted while the fire stands down on
    // it, and guards the arm site once the fire no longer does.
    // Mutation: arm the validity check whatever HasSubmitted says, at the arm site alone. After a
    // server reply the check comes due with the whole-form re-check the same change armed, fires
    // once that re-check has begun and so is no longer armed for it to stand down for, and runs
    // the async rule a second time.
    [Fact]
    public async Task After_a_server_reply_one_change_runs_an_async_rule_once()
    {
        var order = new EngineOrder { Description = "ok", Customer = new EngineCustomer { Name = "Bo" } };
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        var validator = new GatedCountingValidator();
        validator.Gate.SetResult();
        using var engine = Build(
            order, editContext, new FluentValidationModelValidator<EngineOrder>(validator),
            NeverClosing(), time);
        engine.ApplyServerIssues([]);

        validator.Reset();
        order.Description = "changed";
        editContext.NotifyFieldChanged(Description(order));
        time.Advance(Refresh);

        Assert.Equal(1, validator.Runs);

        validator.Gate.SetResult();
        await Task.Yield();
        Assert.Equal(1, validator.Runs);
    }

    // Mutation: drop the armed whole-form re-check from the fire's stand-down. The check armed by
    // the change before the reply then fires beside the re-check the change after it armed, and
    // with the async rule still out neither can reuse the other's answer, so the rule runs twice.
    [Fact]
    public async Task A_check_armed_before_a_server_reply_stands_down_for_the_recheck_an_edit_after_it_armed()
    {
        var order = new EngineOrder { Customer = new EngineCustomer { Name = "Bo" } };
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        var validator = new GatedCountingValidator();
        validator.Gate.SetResult();
        using var engine = Build(
            order, editContext, new FluentValidationModelValidator<EngineOrder>(validator),
            NeverClosing(), time);
        Assert.False(engine.IsFormValid);

        validator.Reset();
        order.Description = "ok";
        editContext.NotifyFieldChanged(Description(order)); // arms the check
        engine.ApplyServerIssues([]);
        time.Advance(TimeSpan.FromMilliseconds(100));
        order.Description = "still ok";
        editContext.NotifyFieldChanged(Description(order)); // arms the whole-form re-check

        time.Advance(Refresh - TimeSpan.FromMilliseconds(100)); // the check comes due
        Assert.Equal(0, validator.Runs);
        time.Advance(TimeSpan.FromMilliseconds(100)); // the re-check comes due
        Assert.Equal(1, validator.Runs);

        // The re-check lands and answers IsFormValid for both edits.
        validator.Gate.SetResult();
        await UntilAsync(() => engine.IsFormValid);
        Assert.True(engine.IsFormValid);
        Assert.Equal(1, validator.Runs);
    }

    // Mutation: drop the edit-armed re-check from ReAnswerOnItsWay. Once the check has stood down
    // for the re-check, nothing then counts as on its way until the re-check begins, so a
    // re-render of the name in that interval drops its green before any answer has arrived.
    [Fact]
    public async Task A_green_field_keeps_green_while_a_check_stands_down_for_the_recheck()
    {
        var order = new EngineOrder { Description = "ok", Customer = new EngineCustomer { Name = "Bo" } };
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        var validator = new GatedNameValidator();
        validator.Gate.SetResult();
        using var engine = Build(
            order, editContext, new FluentValidationModelValidator<EngineOrder>(validator),
            NeverClosing(), time);
        var name = CustomerName(order);
        order.Customer!.Name = "Bea";
        editContext.NotifyFieldChanged(name);
        time.Advance(Refresh);
        Assert.Equal("formidable-valid", editContext.FieldCssClass(name));

        validator.Reset();
        order.Description = "still ok";
        editContext.NotifyFieldChanged(Description(order)); // arms the check
        engine.ApplyServerIssues([]);
        time.Advance(TimeSpan.FromMilliseconds(100));
        order.Description = "edited again";
        editContext.NotifyFieldChanged(Description(order)); // arms the whole-form re-check

        time.Advance(Refresh - TimeSpan.FromMilliseconds(100)); // the check stands down
        Assert.Equal(0, validator.Started);
        Assert.Equal("formidable-valid", editContext.FieldCssClass(name));

        time.Advance(TimeSpan.FromMilliseconds(100)); // the re-check begins
        Assert.Equal(1, validator.Started);
        Assert.Equal("formidable-valid", editContext.FieldCssClass(name));

        validator.Gate.SetResult();
        await UntilAsync(() => engine.IsFormValid);
        Assert.Equal("formidable-valid", editContext.FieldCssClass(name));
    }

    // Mutation: drop the armed whole-form re-check from the fire's stand-down. The check that came
    // due during the submit then re-arms behind it, and fires while the re-check the edit during
    // the submit armed is still out on the async rule, so the rule runs a third time.
    [Fact]
    public async Task A_check_armed_before_a_cancelled_submit_stands_down_for_the_recheck_an_edit_during_it_armed()
    {
        var order = new EngineOrder { Customer = new EngineCustomer { Name = "Bo" } };
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        var validator = new GatedCountingValidator();
        validator.Gate.SetResult();
        using var engine = Build(
            order, editContext, new FluentValidationModelValidator<EngineOrder>(validator),
            NeverClosing(), time);
        Assert.False(engine.IsFormValid);

        validator.Reset();
        order.Description = "ok";
        editContext.NotifyFieldChanged(Description(order)); // arms the check
        using var cancel = new CancellationTokenSource();
        var submit = engine.ValidateForSubmitAsync(cancel.Token); // the rule's first run
        time.Advance(TimeSpan.FromMilliseconds(100));
        order.Description = "still ok";
        editContext.NotifyFieldChanged(Description(order)); // arms the whole-form re-check

        time.Advance(Refresh - TimeSpan.FromMilliseconds(100)); // the check comes due mid-submit
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => submit);
        Assert.False(engine.HasSubmitted);

        time.Advance(TimeSpan.FromMilliseconds(100)); // the re-check starts: the second run
        time.Advance(Refresh); // where a re-armed check would come due
        Assert.Equal(2, validator.Runs);

        validator.Gate.SetResult();
        await UntilAsync(() => engine.IsFormValid);
        Assert.True(engine.IsFormValid);
        Assert.Equal(2, validator.Runs);
    }

    /// <summary>Two synchronous submit rules, one per field, each counting its executions.</summary>
    private sealed class CountingTwoFieldValidator : DraftSubmitValidator<EngineOrder>
    {
        public int Runs;

        protected override void ConfigureDraftRules()
        {
        }

        protected override void ConfigureSubmitRules()
        {
            RuleFor(x => x.Description)
                .Must(description =>
                {
                    Interlocked.Increment(ref Runs);
                    return description.Length > 0;
                })
                .WithMessage("Description is required");

            RuleFor(x => x.Customer!.Name)
                .Must(name =>
                {
                    Interlocked.Increment(ref Runs);
                    return name.Length > 0;
                })
                .WithMessage("Customer name is required")
                .When(x => x.Customer is not null);
        }
    }

    /// <summary>
    /// A synchronous description rule and an asynchronous name rule that waits on
    /// <see cref="Gate"/>, so an edit to the description starts a check that stays out on the
    /// name. The name rule's outcome is chosen per release: pass or fail, throw, or cancel itself.
    /// </summary>
    private sealed class GatedNameValidator : DraftSubmitValidator<EngineOrder>
    {
        public int Started;

        public TaskCompletionSource Gate { get; private set; } = new();

        public bool ShouldPass { get; set; } = true;

        public bool ThrowOnRelease { get; set; }

        public bool CancelOnRelease { get; set; }

        public void Reset()
        {
            Gate = new TaskCompletionSource();
            Started = 0;
        }

        protected override void ConfigureDraftRules()
        {
        }

        protected override void ConfigureSubmitRules()
        {
            RuleFor(x => x.Description).NotEmpty().WithMessage("Description is required");

            RuleFor(x => x.Customer!.Name)
                .MustAsync(async (_, ct) =>
                {
                    Interlocked.Increment(ref Started);
                    await Gate.Task.WaitAsync(ct);
                    if (ThrowOnRelease)
                    {
                        throw new InvalidOperationException("rule blew up");
                    }

                    if (CancelOnRelease)
                    {
                        throw new OperationCanceledException("rule cancelled itself");
                    }

                    return ShouldPass;
                })
                .WithMessage("Customer name failed its async check")
                .When(x => x.Customer is not null);
        }
    }

    /// <summary>One asynchronous submit rule that counts each execution as it starts, then waits on <see cref="Gate"/>.</summary>
    private sealed class GatedCountingValidator : DraftSubmitValidator<EngineOrder>
    {
        public int Runs;

        public TaskCompletionSource Gate { get; private set; } = new();

        public void Reset()
        {
            Gate = new TaskCompletionSource();
            Runs = 0;
        }

        protected override void ConfigureDraftRules()
        {
        }

        protected override void ConfigureSubmitRules() =>
            RuleFor(x => x.Description)
                .MustAsync(async (description, ct) =>
                {
                    Interlocked.Increment(ref Runs);
                    await Gate.Task.WaitAsync(ct);
                    return description.Length > 0;
                })
                .WithMessage("Description is required");
    }
}
