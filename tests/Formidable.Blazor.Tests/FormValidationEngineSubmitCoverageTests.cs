using Formidable.Blazor.Tests.Fixtures;
using Formidable.Introspection;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Time.Testing;

namespace Formidable.Blazor.Tests;

/// <summary>
/// Submit coverage: the verdict store answers "would this model pass submit" for the validity
/// probe and for the Valid state class alike. The probe on a rule-capable validator executes
/// only the submit-selected rules with no fresh verdict and lands what it ran back into the
/// store, so an edit's probe and its live pass share one execution per rule rather than each
/// running their own; where every selected rule is already answered, the probe executes nothing
/// and <c>IsFormValid</c> is a read. The Valid class requires that same coverage to be fresh at the current edit stamp AND
/// clean for the field — a field whose submit rules have not answered for the model as it
/// stands earns no green, however error-free its disclosed issues look. A capability-less
/// validator keeps the whole-profile probe, and its coverage is the last completed
/// submit-profile evaluation, current at the edit stamp.
/// </summary>
/// <remarks>
/// The execution counters are what carry the probe pins: a rule run twice leaves exactly the
/// issues a single run leaves, so only a counter can tell a store-served answer from a
/// re-execution. The class pins read the same two surfaces the shipped seams read —
/// <c>EditContext.FieldCssClass</c> for a native input, <c>FormidableCss.Compute</c> over
/// <c>GetFieldState</c> for a kit input — and assert them equal, because the honest-valid rule
/// is one decision both seams share, not two that happen to agree.
/// </remarks>
public class FormValidationEngineSubmitCoverageTests
{
    private static FieldIdentifier Description(EngineOrder order) =>
        new(order, nameof(EngineOrder.Description));

    private static string KitClass(FormValidationEngine<EngineOrder> engine, FieldIdentifier field) =>
        FormidableCss.Compute(engine.GetFieldState(field), engine.Options.CssClasses);

    /// <summary>
    /// Counts completed render dispatches, standing in for the engine's <c>renderDispatch</c>.
    /// A fire-and-forget probe hands back no awaitable handle, but its write-back is one
    /// dispatch — so "the landing has run" is observable as the completion count moving past
    /// where it stood before the release, which is a real signal where a fixed number of yields
    /// is a guess about continuation scheduling. Valid only while exactly one gated chain is
    /// pending: with a live pass (or a second probe) also blocked, the first dispatch to
    /// complete after a release could be the other chain's, and the wait would report the
    /// wrong landing.
    /// </summary>
    private sealed class DispatchCounter
    {
        private int _completed;

        public int Completed => Volatile.Read(ref _completed);

        public async Task Dispatch(Func<Task> work)
        {
            await work().ConfigureAwait(false);
            Interlocked.Increment(ref _completed);
        }

        public async Task WaitUntilAbove(int count)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (Completed <= count)
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException("The awaited dispatch never completed.");
                }

                await Task.Delay(5);
            }
        }
    }

    // ---------------------------------------------------------------------------------------
    // The fed probe (capability path)
    // ---------------------------------------------------------------------------------------

    // Mutation this breaks: a probe that validates the whole submit profile on its own —
    // bypassing the verdict store — re-runs the draft rule the live pass just answered, and the
    // draft counter reads one higher than the single run the store makes sufficient.
    [Fact]
    public void An_edits_probe_executes_only_what_the_live_pass_left_stale()
    {
        var order = new EngineOrder { Description = "ok", Customer = new EngineCustomer() };
        var validator = new RuleRunCountingValidator();
        var editContext = new EditContext(order);
        using var engine = new FormValidationEngine<EngineOrder>(
            order,
            editContext,
            new FluentValidationModelValidator<EngineOrder>(validator),
            new ReflectionModelIntrospector(),
            new FormidableOptions { TrackFormValidity = true },
            new FakeTimeProvider());

        // The construction probe answers a pristine store: every submit-selected rule executes
        // exactly once.
        Assert.Equal(1, validator.DraftRuleRuns);
        Assert.Equal(1, validator.SubmitRuleRuns);
        Assert.True(engine.IsFormValid);

        // One edit: the live pass runs the draft bucket, and the probe that follows it finds
        // that verdict already in the store — it executes the submit bucket alone.
        editContext.NotifyFieldChanged(Description(order));

        Assert.Equal(2, validator.DraftRuleRuns);
        Assert.Equal(2, validator.SubmitRuleRuns);
        Assert.True(engine.IsFormValid);
    }

    // Mutation this breaks: a probe whose executions never reach the store — the live pass that
    // fires in the same debounce window would re-run the draft rule the probe just answered.
    [Fact]
    public void A_debounced_windows_probe_feeds_the_live_pass_that_follows_it()
    {
        var order = new EngineOrder { Description = "ok", Customer = new EngineCustomer() };
        var validator = new RuleRunCountingValidator();
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        using var engine = new FormValidationEngine<EngineOrder>(
            order,
            editContext,
            new FluentValidationModelValidator<EngineOrder>(validator),
            new ReflectionModelIntrospector(),
            new FormidableOptions { TrackFormValidity = true, LiveDebounce = TimeSpan.FromMilliseconds(400) },
            time);

        Assert.Equal(1, validator.DraftRuleRuns);
        Assert.Equal(1, validator.SubmitRuleRuns);

        // The window fire runs the probe first, then the live pass. The probe executes the
        // whole stale selection — draft and submit buckets, once each — and the live pass then
        // finds its own draft selection served from the store, executing nothing.
        editContext.NotifyFieldChanged(Description(order));
        time.Advance(TimeSpan.FromMilliseconds(400));

        Assert.Equal(2, validator.DraftRuleRuns);
        Assert.Equal(2, validator.SubmitRuleRuns);
    }

    // Mutation this breaks: a probe that validates regardless of freshness. After the refresh
    // has answered every submit-selected rule at the current stamp, IsFormValid is a read over
    // those verdicts — the refresh's own apply lands it, and the probe that fires when the live
    // window closes finds nothing stale and executes nothing.
    [Fact]
    public async Task A_refresh_lands_form_validity_as_a_read_and_the_following_probe_executes_nothing()
    {
        var order = new EngineOrder { Customer = new EngineCustomer() };
        var validator = new RuleRunCountingValidator();
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        using var engine = new FormValidationEngine<EngineOrder>(
            order,
            editContext,
            new FluentValidationModelValidator<EngineOrder>(validator),
            new ReflectionModelIntrospector(),
            new FormidableOptions { TrackFormValidity = true, LiveDebounce = TimeSpan.FromMilliseconds(400) },
            time);

        Assert.False(engine.IsFormValid); // construction probe: the description is empty

        // Submit re-runs the whole selection by fiat and blocks on the empty description.
        Assert.False((await engine.ValidateForSubmitAsync()).CanProceed);
        Assert.Equal(2, validator.DraftRuleRuns);
        Assert.Equal(2, validator.SubmitRuleRuns);

        // The fixing edit opens both windows: the refresh (300 ms) fires before the live
        // debounce (400 ms), executes the stale selection once, and its apply adopts the
        // report's validity — the flip lands with no probe having run at all.
        order.Description = "ok";
        editContext.NotifyFieldChanged(Description(order));
        time.Advance(TimeSpan.FromMilliseconds(301));
        await Task.Yield();

        Assert.True(engine.IsFormValid);
        Assert.Equal(3, validator.DraftRuleRuns);
        Assert.Equal(3, validator.SubmitRuleRuns);

        // The live window closes: the probe finds every submit-selected rule fresh and executes
        // nothing, and the live pass finds its draft selection equally answered.
        time.Advance(TimeSpan.FromMilliseconds(100));
        await Task.Yield();

        Assert.True(engine.IsFormValid);
        Assert.Equal(3, validator.DraftRuleRuns);
        Assert.Equal(3, validator.SubmitRuleRuns);
    }

    // Mutation this breaks: a probe landing that stamps its verdicts with the edit stamp at
    // LANDING time rather than the one captured at its begin. Probe A ran against the model as
    // it stood before the second edit; its clean answer must land — if at all — as a verdict
    // for THAT model state, stale and never served, so the coverage read cannot vouch for a
    // model the rule never saw. Its IsFormValid answer is discarded by the probe-stamp
    // discipline for the same reason.
    [Fact]
    public async Task An_edit_mid_probe_leaves_its_answer_stale_and_unserved()
    {
        var order = new EngineOrder();
        var validator = new GatedValidator();
        var editContext = new EditContext(order);
        var dispatches = new DispatchCounter();
        using var engine = new FormValidationEngine<EngineOrder>(
            order,
            editContext,
            new FluentValidationModelValidator<EngineOrder>(validator),
            new ReflectionModelIntrospector(),
            new FormidableOptions { TrackFormValidity = true },
            new FakeTimeProvider(),
            dispatches.Dispatch);
        var description = Description(order);

        // The construction probe pends on the validator's very first gate; nothing releases
        // it, and Dispose's cancellation cleans it up.

        // Probe A: begun against the first edit's model state and held open across the second
        // edit, so its answer arrives for a model that no longer exists.
        validator.Reset();
        var gateA = validator.Gate;
        order.Description = "edit-a";
        editContext.NotifyFieldChanged(description);

        validator.Reset();
        var gateB = validator.Gate;
        order.Description = "edit-b";
        editContext.NotifyFieldChanged(description);

        // Everything not blocked on a gate has settled; the next dispatch to complete after a
        // release is that probe's own landing.
        validator.ShouldPass = true;
        var beforeA = dispatches.Completed;
        gateA.SetResult();
        await dispatches.WaitUntilAbove(beforeA);

        // A's passing answer lands after the second edit: IsFormValid must not adopt it, and
        // the coverage read must not treat its clean verdict as current — the field stays
        // unvouched until an evaluation of the model as it stands answers.
        Assert.False(engine.IsFormValid);
        Assert.False(engine.GetFieldState(description).WouldPassSubmit);

        // Probe B is the current one; its landing settles both.
        var beforeB = dispatches.Completed;
        gateB.SetResult();
        await dispatches.WaitUntilAbove(beforeB);

        Assert.True(engine.IsFormValid);
        Assert.True(engine.GetFieldState(description).WouldPassSubmit);
    }

    // ---------------------------------------------------------------------------------------
    // The fallback probe (capability-less path)
    // ---------------------------------------------------------------------------------------

    // Mutation this breaks: routing a capability-less validator anywhere near the rule plan. A
    // validator that cannot execute rule by rule keeps the whole-profile probe — one
    // SubmitProfile validation per probe, the same shape and the same cost.
    [Fact]
    public void A_capability_less_validator_keeps_the_whole_profile_probe()
    {
        var order = new EngineOrder { Description = "ok", Customer = new EngineCustomer() };
        var editContext = new EditContext(order);
        var options = new FormidableOptions { TrackFormValidity = true };
        var counting = new CountingValidator(
            new FluentValidationModelValidator<EngineOrder>(new EngineOrderValidator()), options.SubmitProfile);
        using var engine = new FormValidationEngine<EngineOrder>(
            order,
            editContext,
            counting,
            new ReflectionModelIntrospector(),
            options,
            new FakeTimeProvider());

        Assert.Equal(1, counting.SubmitProfileCallCount); // the construction probe

        editContext.NotifyFieldChanged(Description(order));

        // One live pass (LiveProfile) plus one whole-profile probe (SubmitProfile).
        Assert.Equal(2, counting.SubmitProfileCallCount);
        Assert.True(engine.IsFormValid);
    }

    // ---------------------------------------------------------------------------------------
    // Honest valid (capability path)
    // ---------------------------------------------------------------------------------------

    // Mutation this breaks: the stale-blind Valid predicate — touched-or-modified with no
    // disclosed issues earning green while the submit rules that decide the field's fate have
    // never answered for the model as it stands. The archetype: a required field, touched and
    // then emptied, wearing the confirmation border on a value a submit would reject.
    [Fact]
    public void A_touched_then_emptied_required_field_earns_no_valid_class_while_its_submit_rule_is_stale()
    {
        var order = new EngineOrder();
        var editContext = new EditContext(order);
        using var engine = new FormValidationEngine<EngineOrder>(
            order,
            editContext,
            new FluentValidationModelValidator<EngineOrder>(new EngineOrderValidator()),
            new ReflectionModelIntrospector(),
            new FormidableOptions(),
            new FakeTimeProvider());
        var description = Description(order);
        using var registration = engine.Registry.Register(description);

        order.Description = "draft";
        editContext.NotifyFieldChanged(description);
        order.Description = string.Empty;
        editContext.NotifyFieldChanged(description);

        // Touched, modified, and free of disclosed issues — but the submit-selected NotEmpty
        // rule has no verdict at this stamp, so nothing can vouch that the field would pass.
        Assert.True(editContext.IsModified(description));
        Assert.Empty(editContext.GetValidationMessages(description));
        Assert.Equal(string.Empty, editContext.FieldCssClass(description));
        Assert.Equal(string.Empty, KitClass(engine, description));
    }

    // The other half of the honest rule: green is reachable, through any pass that answers the
    // submit selection at the current stamp — a post-submit refresh being the everyday route.
    [Fact]
    public async Task Green_returns_when_the_refresh_answers_fresh_and_clean()
    {
        var order = new EngineOrder();
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        using var engine = new FormValidationEngine<EngineOrder>(
            order,
            editContext,
            new FluentValidationModelValidator<EngineOrder>(new EngineOrderValidator()),
            new ReflectionModelIntrospector(),
            new FormidableOptions(),
            time);
        var description = Description(order);
        using var registration = engine.Registry.Register(description);

        order.Description = "draft";
        editContext.NotifyFieldChanged(description);
        order.Description = string.Empty;
        editContext.NotifyFieldChanged(description);

        // A blocked submit discloses the required-field error: invalid, ungated, both seams.
        Assert.False((await engine.ValidateForSubmitAsync()).CanProceed);
        Assert.Equal("formidable-invalid", editContext.FieldCssClass(description));
        Assert.Equal("formidable-invalid", KitClass(engine, description));

        // The fixing edit re-strands the coverage; the refresh re-answers the whole submit
        // selection at the fixed model's stamp, fresh and clean — green, on both seams.
        order.Description = "ok";
        order.Customer = new EngineCustomer();
        editContext.NotifyFieldChanged(description);
        time.Advance(TimeSpan.FromMilliseconds(301));
        await Task.Yield();

        Assert.Equal("formidable-valid", editContext.FieldCssClass(description));
        Assert.Equal("formidable-valid", KitClass(engine, description));
    }

    // Mutation this breaks: a coverage read that recognises only submit and refresh answers.
    // The probe's landed verdicts are coverage like any other — a page that tracks validity
    // shows live green with no submit anywhere in its history.
    [Fact]
    public void The_fed_probe_freshens_green_without_a_submit()
    {
        var order = new EngineOrder { Description = "ok", Customer = new EngineCustomer() };
        var editContext = new EditContext(order);
        using var engine = new FormValidationEngine<EngineOrder>(
            order,
            editContext,
            new FluentValidationModelValidator<EngineOrder>(new EngineOrderValidator()),
            new ReflectionModelIntrospector(),
            new FormidableOptions { TrackFormValidity = true },
            new FakeTimeProvider());
        var description = Description(order);
        using var registration = engine.Registry.Register(description);

        editContext.NotifyFieldChanged(description);

        Assert.True(engine.IsFormValid);
        Assert.Equal("formidable-valid", editContext.FieldCssClass(description));
        Assert.Equal("formidable-valid", KitClass(engine, description));
    }

    // ---------------------------------------------------------------------------------------
    // Honest valid (capability-less path)
    // ---------------------------------------------------------------------------------------

    // The fallback rule, walked end to end: with no rule verdicts to read, coverage is the last
    // COMPLETED submit-profile evaluation — submit, refresh, or probe — and it counts exactly
    // while it is current at the edit stamp. Stale: no green, however clean the field looks.
    // Current but carrying an error for the field: no green — the answer says submit would
    // reject it. Current and clean: green, on both seams.
    [Fact]
    public void A_capability_less_validators_green_follows_the_last_whole_profile_answer()
    {
        var order = new EngineOrder { Description = "x" };
        var editContext = new EditContext(order);
        using var engine = new FormValidationEngine<EngineOrder>(
            order,
            editContext,
            new CapabilityHidingModelValidator<EngineOrder>(
                new FluentValidationModelValidator<EngineOrder>(new EngineOrderValidator())),
            new ReflectionModelIntrospector(),
            new FormidableOptions(),
            new FakeTimeProvider());
        var description = Description(order);
        using var registration = engine.Registry.Register(description);

        // No submit-profile evaluation has ever completed: coverage is stale, green withheld.
        editContext.NotifyFieldChanged(description);
        Assert.Empty(editContext.GetValidationMessages(description));
        Assert.Equal(string.Empty, editContext.FieldCssClass(description));
        Assert.Equal(string.Empty, KitClass(engine, description));

        // Tracking makes each edit's probe a completed submit-profile evaluation. The emptied
        // description leaves that answer CURRENT but dirty for the field — the whole-profile
        // report carries its NotEmpty error — so green stays withheld even though nothing about
        // the error is disclosed.
        engine.Options.TrackFormValidity = true;
        order.Description = string.Empty;
        editContext.NotifyFieldChanged(description);
        Assert.Empty(editContext.GetValidationMessages(description));
        Assert.Equal(string.Empty, editContext.FieldCssClass(description));
        Assert.Equal(string.Empty, KitClass(engine, description));

        // Fixed model, fresh answer, no error for the field: green on both seams.
        order.Description = "ok";
        order.Customer = new EngineCustomer();
        editContext.NotifyFieldChanged(description);
        Assert.True(engine.IsFormValid);
        Assert.Equal("formidable-valid", editContext.FieldCssClass(description));
        Assert.Equal("formidable-valid", KitClass(engine, description));
    }
}
