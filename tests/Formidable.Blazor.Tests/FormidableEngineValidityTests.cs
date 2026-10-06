using Formidable.Blazor.Tests.Fixtures;
using Formidable.Introspection;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Time.Testing;

namespace Formidable.Blazor.Tests;

public class FormidableEngineValidityTests
{
    private readonly EngineOrder _order = new();
    private readonly EditContext _editContext;
    private readonly FormidableEngine<EngineOrder> _engine;
    private readonly FakeTimeProvider _time = new();

    public FormidableEngineValidityTests()
    {
        _editContext = new EditContext(_order);
        _engine = new FormidableEngine<EngineOrder>(
            _order,
            _editContext,
            new FluentValidationModelValidator<EngineOrder>(new EngineOrderValidator()),
            new ReflectionModelIntrospector(),
            new FormidableOptions { TrackFormValidity = true },
            _time);
    }

    private FieldIdentifier DescriptionField => new(_order, nameof(EngineOrder.Description));

    /// <summary>
    /// Yields once. Every validator this file's main fixture uses (<see cref="EngineOrderValidator"/>,
    /// <see cref="ThrowingValidator"/>) is fully synchronous, so a fire-and-forget probe against it
    /// has already run to completion by the time <c>NotifyFieldChanged</c> returns — this yield
    /// changes nothing for those assertions. It is kept for symmetry with the engine's other
    /// async-pass test idioms; the genuinely-async overlap test below uses its own dedicated
    /// completion signal instead, precisely because a single yield is not a safe wait for a
    /// validator that really does suspend.
    /// </summary>
    private static async Task FlushAsync() => await Task.Yield();

    /// <summary>
    /// Releases <paramref name="gate"/> and waits for the <see cref="GatedValidator.Released"/>
    /// signal it causes, then yields twice — the rule itself has answered at that point, but the
    /// remaining chain (FluentValidation's own result assembly, then the probe's write-back
    /// dispatch) is not guaranteed to have run yet, and there is no awaitable handle to that
    /// fire-and-forget probe to wait on directly. Subscribes BEFORE releasing the gate,
    /// deliberately: a <see cref="TaskCompletionSource"/> created with no options runs its
    /// continuations synchronously on the thread that calls <c>SetResult</c>, so releasing first
    /// and subscribing after can miss the signal outright — it may already have fired into an
    /// empty subscriber list before the subscription exists.
    /// </summary>
    private static async Task ReleaseAndWaitAsync(GatedValidator validator, TaskCompletionSource gate)
    {
        var tcs = new TaskCompletionSource();
        void Handler() => tcs.TrySetResult();
        validator.Released += Handler;
        try
        {
            gate.SetResult();
            await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            validator.Released -= Handler;
        }

        await Task.Yield();
        await Task.Yield();
    }

    [Fact]
    public async Task Tracking_probes_on_field_change_and_flips_IsFormValid()
    {
        Assert.False(_engine.IsFormValid); // pristine invalid model (no Description, no Customer)

        _order.Description = "Valid";
        _order.Customer = new EngineCustomer();

        _editContext.NotifyFieldChanged(DescriptionField);
        await FlushAsync();

        Assert.True(_engine.IsFormValid);
    }

    // The probe feeds the per-rule VERDICT store — that is what makes it cheap — but the
    // verdict store is not a disclosure channel: nothing the probe executes may surface as an
    // issue, a message-store entry, a suppression diagnostic, or a pending indicator.
    [Fact]
    public async Task Probe_writes_no_visible_issues_and_no_disclosure()
    {
        var suppressedCount = 0;
        _engine.Options.SuppressedIssueDiagnostic = _ => suppressedCount++;

        // The live channel is narrowed to the draft bucket, which the empty description passes,
        // so anything disclosed below could only have come from the probe. Without the narrowing
        // the live pass would legitimately disclose the description's own required-field error
        // and there would be nothing left to attribute.
        _engine.Options.LiveProfile = ValidationProfile.Draft;

        _editContext.NotifyFieldChanged(DescriptionField); // model still invalid
        await FlushAsync();

        Assert.False(_engine.IsFormValid);
        Assert.Empty(_engine.GetVisibleIssues()); // nothing disclosed by the probe
        Assert.Empty(_editContext.GetValidationMessages(DescriptionField)); // no message-store write
        Assert.Empty(_editContext.GetValidationMessages(new FieldIdentifier(_order, string.Empty)));
        Assert.Equal(0, suppressedCount); // the probe's own errors never reach disclosure at all
        Assert.False(_engine.IsValidating); // no pending-indicator flip left standing
    }

    [Fact]
    public async Task StateChanged_fires_only_on_a_flip()
    {
        var raised = 0;
        _engine.StateChanged += (_, _) => raised++;

        // First edit: DescriptionField is newly touched (its own StateChanged) and the model
        // stays invalid — the probe reports false again, no flip.
        _editContext.NotifyFieldChanged(DescriptionField);
        await FlushAsync();
        var afterFirst = raised;

        // Second edit: same field, already touched (no touch-raise this time), model still
        // invalid — the probe still reports false, no flip. The delta from here on is the
        // constant contribution of the pass lifecycle plus the probe's own landing publication
        // (each edit strands the coverage, so each probe re-executes the stale submit selection
        // and publishes the landing); a FLIP is the one thing that adds a raise beyond it.
        _editContext.NotifyFieldChanged(DescriptionField);
        await FlushAsync();
        var afterSecond = raised;
        var noFlipDelta = afterSecond - afterFirst;

        // Third edit: make the model submit-valid — the probe flips IsFormValid false -> true,
        // exactly one StateChanged beyond the no-flip baseline just measured.
        _order.Description = "Valid";
        _order.Customer = new EngineCustomer();
        _editContext.NotifyFieldChanged(DescriptionField);
        await FlushAsync();
        var afterThird = raised;
        var flipDelta = afterThird - afterSecond;

        Assert.True(_engine.IsFormValid);
        Assert.Equal(noFlipDelta + 1, flipDelta);

        // Fourth edit: still valid — the probe reports true again, no flip, delta back to
        // the same no-flip baseline.
        _editContext.NotifyFieldChanged(DescriptionField);
        await FlushAsync();
        var afterFourth = raised;
        Assert.Equal(noFlipDelta, afterFourth - afterThird);
    }

    [Fact]
    public async Task Tracking_off_by_default_probe_never_runs()
    {
        var order = new EngineOrder();
        var editContext = new EditContext(order);
        var options = new FormidableOptions(); // TrackFormValidity defaults to false
        var counting = new CountingValidator<EngineOrder>(
            new FluentValidationModelValidator<EngineOrder>(new EngineOrderValidator()));
        using var engine = new FormidableEngine<EngineOrder>(
            order,
            editContext,
            counting,
            new ReflectionModelIntrospector(),
            options,
            _time);

        editContext.NotifyFieldChanged(new FieldIdentifier(order, nameof(EngineOrder.Description)));
        await FlushAsync();

        Assert.False(engine.IsFormValid);
        // Exactly one call total — the field change's own live pass, and nothing else. Tracking
        // on would make it three: a probe at construction, the live pass, and the change's own
        // probe. Counting every call rather than the calls at one profile is what keeps this
        // discriminating, since the live channel runs the submit profile itself by default.
        Assert.Equal(1, counting.CallCount);
    }

    [Fact]
    public void TrackFormValidity_with_LiveDebounce_defers_the_probe_until_the_window_closes()
    {
        var order = new EngineOrder { Description = "Valid", Customer = new EngineCustomer() };
        var editContext = new EditContext(order);
        using var engine = new FormidableEngine<EngineOrder>(
            order,
            editContext,
            new FluentValidationModelValidator<EngineOrder>(new EngineOrderValidator()),
            new ReflectionModelIntrospector(),
            new FormidableOptions { TrackFormValidity = true, LiveDebounce = TimeSpan.FromMilliseconds(400) },
            _time);

        Assert.True(engine.IsFormValid); // the constructor's own probe already settled this

        // Break submit-validity, then edit — the debounced probe must not react before the
        // window closes.
        order.Description = string.Empty;
        editContext.NotifyFieldChanged(new FieldIdentifier(order, nameof(EngineOrder.Description)));
        Assert.True(engine.IsFormValid); // window open: no probe has run yet

        _time.Advance(TimeSpan.FromMilliseconds(399));
        Assert.True(engine.IsFormValid);

        _time.Advance(TimeSpan.FromMilliseconds(1)); // window closes -> debounced pass + probe run

        Assert.False(engine.IsFormValid);
    }

    [Fact]
    public void An_all_departed_window_still_probes_form_validity()
    {
        var order = new EngineOrder { Description = "Valid", Customer = new EngineCustomer() };
        var editContext = new EditContext(order);
        using var engine = new FormidableEngine<EngineOrder>(
            order,
            editContext,
            new FluentValidationModelValidator<EngineOrder>(new EngineOrderValidator()),
            new ReflectionModelIntrospector(),
            // The departure below arms a refresh as well, and a refresh IS a pass: held past the
            // window, so what closes into the empty snapshot is the window alone and the
            // indicator has only the probe to answer for.
            new FormidableOptions
            {
                TrackFormValidity = true,
                LiveDebounce = TimeSpan.FromMilliseconds(400),
                RefreshDebounce = TimeSpan.FromSeconds(30),
            },
            _time);

        Assert.True(engine.IsFormValid); // the constructor's own probe already settled this

        var descriptionField = new FieldIdentifier(order, nameof(EngineOrder.Description));
        var registration = engine.Registry.Register(descriptionField);

        // Breaks submit-validity, accumulating the field into the open window rather than
        // probing yet.
        order.Description = string.Empty;
        editContext.NotifyFieldChanged(descriptionField);
        Assert.True(engine.IsFormValid); // window open: no probe has run yet

        // The field the window opened for leaves the page before the window closes; its (now
        // empty) value stays on the model — the shape a collapsed section leaves behind.
        registration.Dispose();
        engine.OnRenderedFieldsChanged();

        var everValidating = false;
        engine.StateChanged += (_, _) =>
            everValidating |= engine.IsValidating || engine.GetFieldState(descriptionField).IsValidating;

        _time.Advance(TimeSpan.FromMilliseconds(400)); // window closes into an empty engine-pass snapshot

        // No engine pass ran for the departed field — the pending indicator never lit — but the
        // probe, which is not a pass, still validated the model as it now stands and caught the
        // edit that survived on it.
        Assert.False(everValidating);
        Assert.False(engine.IsValidating);
        Assert.False(engine.IsFormValid);
    }

    [Fact]
    public async Task Submit_adopts_IsFormValid_immediately_without_a_separate_probe()
    {
        // Make the model submit-valid, but go straight to submit without ever notifying a field
        // change - a submit does not run through HandleFieldChanged, so the only route by which
        // IsFormValid could reflect this is the submit's own adoption of its own report.
        _order.Description = "Valid";
        _order.Customer = new EngineCustomer();

        var outcome = await _engine.ValidateForSubmitAsync();

        Assert.True(outcome.CanProceed);
        Assert.True(_engine.IsFormValid);
    }

    [Fact]
    public async Task Submit_outranks_a_stale_in_flight_probe_regardless_of_finish_order()
    {
        var order = new EngineOrder();
        var validator = new GatedValidator();
        var editContext = new EditContext(order);
        var descriptionField = new FieldIdentifier(order, nameof(EngineOrder.Description));
        using var engine = new FormidableEngine<EngineOrder>(
            order,
            editContext,
            new FluentValidationModelValidator<EngineOrder>(validator),
            new ReflectionModelIntrospector(),
            new FormidableOptions { TrackFormValidity = true },
            _time);

        // A stale probe: scheduled by a field change, well before the submit below, and held
        // open so it can be released AFTER the submit lands.
        validator.Reset();
        var staleGate = validator.Gate;
        order.Description = "edit-stale";
        editContext.NotifyFieldChanged(descriptionField);

        // The submit's own pass, on its own fresh gate.
        validator.Reset();
        var submitGate = validator.Gate;
        validator.ShouldPass = true; // the submit's own report says "valid"
        var submitTask = engine.ValidateForSubmitAsync();

        await ReleaseAndWaitAsync(validator, submitGate);
        var outcome = await submitTask;

        Assert.True(outcome.CanProceed);
        Assert.True(engine.IsFormValid); // the submit's own answer landed immediately

        // Now let the stale probe finish, well after the submit — it must not win.
        validator.ShouldPass = false; // what the stale probe would (wrongly) write if it won
        await ReleaseAndWaitAsync(validator, staleGate);

        Assert.True(engine.IsFormValid); // still the submit's answer; the stale probe was discarded
    }

    // A load of values is out when an edit lands; the load answers for the values before the
    // edit. Rows a and b (LiveDebounce null, the edit emptying or fixing the field) leave the edit
    // with no check of its own, since the edit's check stood down for the load; c (200 ms) and d
    // (never) leave it to the window's or the validity check's own check; e (null, RefreshDebounce
    // never) leaves no timer that could run a check. Each settles where a submit says the form
    // stands. Mutations: (a) adopt the load's answer whatever edit came after it began, and row a
    // settles valid with the description empty; (b) drop the check behind a landing whose answer
    // predates an edit, and row a settles valid; (c) run that check only through the validity
    // timer, and row e settles valid.
    [Theory]
    [InlineData("a", 0, true, false)]
    [InlineData("b", 0, false, false)]
    [InlineData("c", 200, true, false)]
    [InlineData("d", -1, true, false)]
    [InlineData("e", 0, true, true)]
    public async Task A_load_with_an_edit_in_flight_leaves_IsFormValid_on_the_edited_model(
        string run, int liveDebounceMs, bool startValid, bool refreshNever)
    {
        _ = run;
        var order = new EngineOrder
        {
            Description = startValid ? "ok" : string.Empty,
            Customer = new EngineCustomer { Name = "Bo" },
        };
        var editContext = new EditContext(order);
        var validator = new PerRunGatedValidator();
        var options = new FormidableOptions
        {
            TrackFormValidity = true,
            LiveDebounce = liveDebounceMs switch
            {
                0 => null,
                -1 => Timeout.InfiniteTimeSpan,
                _ => TimeSpan.FromMilliseconds(liveDebounceMs),
            },
            RefreshDebounce = refreshNever ? Timeout.InfiniteTimeSpan : TimeSpan.FromMilliseconds(300),
        };
        var dispatch = new CountedDispatch();
        using var engine = new FormidableEngine<EngineOrder>(
            order, editContext, new FluentValidationModelValidator<EngineOrder>(validator),
            new ReflectionModelIntrospector(), options, _time, dispatch.Dispatch);
        var built = dispatch.Completed;
        validator.Release(0); // the check every tracked form runs as it is built
        await dispatch.WaitUntilAtLeastAsync(built + 1);
        Assert.Equal(startValid, engine.IsFormValid);

        var load = engine.DiscloseLoadedValuesAsync(); // run 1
        order.Description = startValid ? string.Empty : "ok";
        editContext.NotifyFieldChanged(DescriptionOf(order));

        // The load lands older than the edit, its end arms whatever is owed to the edit, and the
        // live check that follows a load then waits on a run of its own. The load's task completes
        // only after all of that, so once it has, every wait the end armed is armed, and the clock
        // can move.
        await ReleaseUntilAsync(validator, () => load.IsCompleted);
        await load;
        _time.Advance(TimeSpan.FromSeconds(1));
        await ReleaseUntilAsync(validator, () => engine.IsFormValid == !startValid && !engine.IsValidating);

        var settled = engine.IsFormValid;
        Assert.Equal(!startValid, settled);
        Assert.Equal(await SubmitSaysAsync(engine, validator), settled);
    }

    // The first submit is out when an edit empties the field. The edit's own check stands down for
    // the submit, and the whole-form re-check the edit would arm never fires under a
    // RefreshDebounce that never passes, so the submit's landing, older than the edit, starts the
    // check itself. Mutation: run that check only through the validity timer, which never fires
    // here, and IsFormValid keeps the submit's answer for the value before the edit.
    [Fact]
    public async Task An_edit_during_a_first_submit_under_an_infinite_refresh_leaves_IsFormValid_on_the_edited_model()
    {
        var order = new EngineOrder { Description = "ok", Customer = new EngineCustomer { Name = "Bo" } };
        var editContext = new EditContext(order);
        var validator = new PerRunGatedValidator();
        var dispatch = new CountedDispatch();
        using var engine = new FormidableEngine<EngineOrder>(
            order, editContext, new FluentValidationModelValidator<EngineOrder>(validator),
            new ReflectionModelIntrospector(),
            new FormidableOptions { TrackFormValidity = true, RefreshDebounce = Timeout.InfiniteTimeSpan },
            _time,
            dispatch.Dispatch);
        var built = dispatch.Completed;
        validator.Release(0);
        await dispatch.WaitUntilAtLeastAsync(built + 1);
        Assert.True(engine.IsFormValid);

        var submit = engine.ValidateForSubmitAsync(); // run 1
        order.Description = string.Empty;
        editContext.NotifyFieldChanged(DescriptionOf(order));
        validator.Release(1);
        Assert.True((await submit).CanProceed); // the submit answered for the value it read

        // The submit's task completes after its end has started the check, which waits on run 2.
        await ReleaseUntilAsync(validator, () => !engine.IsFormValid);

        Assert.False(engine.IsFormValid);
    }

    // A pin of the stand-down rules: once a submit has answered, the validity check stands down,
    // and the whole-form re-check an edit arms answers IsFormValid. An edit during a second submit
    // is answered by that re-check alone. Mutation: run the check behind the submit's landing
    // without asking whether an armed re-check answers the edit, and without the check's own
    // stand-down for an answered submit, and the async rule is reached a second time for the edit.
    [Fact]
    public async Task An_edit_during_a_submit_is_answered_by_the_refresh_alone()
    {
        var order = new EngineOrder { Description = "ok", Customer = new EngineCustomer { Name = "Bo" } };
        var editContext = new EditContext(order);
        var validator = new GatedRuleRunCountingValidator();
        validator.Gate.SetResult();
        using var engine = new FormidableEngine<EngineOrder>(
            order, editContext, new FluentValidationModelValidator<EngineOrder>(validator),
            new ReflectionModelIntrospector(),
            new FormidableOptions { TrackFormValidity = true, RefreshDebounce = TimeSpan.FromMilliseconds(300) },
            _time,
            EngineTestSync.OneAtATime());
        Assert.True((await engine.ValidateForSubmitAsync()).CanProceed);
        Assert.True(engine.IsFormValid);

        validator.Reset();
        var submitGate = validator.Gate;
        var submit = engine.ValidateForSubmitAsync(); // reaches the async rule and waits
        order.Description = string.Empty;
        editContext.NotifyFieldChanged(DescriptionOf(order)); // arms the re-check, due at 300 ms
        _time.Advance(TimeSpan.FromMilliseconds(100));

        validator.Reset();
        submitGate.SetResult();
        await submit; // its answer predates the edit
        var afterSubmit = validator.DraftRuleRuns;

        _time.Advance(TimeSpan.FromMilliseconds(200)); // the re-check starts and waits on the rule
        _time.Advance(TimeSpan.FromMilliseconds(100)); // where a check armed at the submit's end would come due
        Assert.Equal(afterSubmit + 1, validator.DraftRuleRuns);

        validator.Gate.SetResult();
        for (var waited = 0; waited < 500 && engine.IsFormValid; waited++)
        {
            await Task.Delay(10);
        }

        Assert.False(engine.IsFormValid);
        Assert.Equal(afterSubmit + 1, validator.DraftRuleRuns);
    }

    // With no LiveDebounce an edit made during a load starts no check of its own, so the load's
    // outdated landing arms a validity check for RefreshDebounce after its end. A second edit
    // inside that wait starts its own live check and validity check, the documented pair, and the
    // armed check then has nothing left to answer. Mutation: let the armed check run whatever
    // check is already out for the latest edit, and the async rule is reached a third time for
    // that edit.
    [Fact]
    public async Task The_check_behind_an_outdated_load_stands_down_for_a_later_edit_s_own_check()
    {
        var customer = new EngineCustomer { Name = "Bo" };
        var order = new EngineOrder { Description = "ok", Customer = customer };
        var editContext = new EditContext(order);
        var validator = new GatedRuleRunCountingValidator();
        validator.Gate.SetResult();
        using var engine = new FormidableEngine<EngineOrder>(
            order, editContext, new FluentValidationModelValidator<EngineOrder>(validator),
            new ReflectionModelIntrospector(),
            new FormidableOptions { TrackFormValidity = true, RefreshDebounce = TimeSpan.FromMilliseconds(300) },
            _time,
            EngineTestSync.OneAtATime());
        await UntilAsync(() => engine.IsFormValid);
        var name = new FieldIdentifier(customer, nameof(EngineCustomer.Name));

        validator.Reset();
        var loadGate = validator.Gate;
        var load = engine.DiscloseLoadedValuesAsync(); // waits on the async rule
        customer.Name = "Bea";
        editContext.NotifyFieldChanged(name); // its checks stand down for the load
        var beforeLanding = validator.DraftRuleRuns;

        validator.Reset();
        loadGate.SetResult(); // the load lands outdated; the live check after it waits on the rule
        await UntilAsync(() => validator.DraftRuleRuns == beforeLanding + 1);

        _time.Advance(TimeSpan.FromMilliseconds(100));
        customer.Name = "far too long";
        editContext.NotifyFieldChanged(name); // its live check and validity check both reach the rule
        var reached = validator.DraftRuleRuns;
        Assert.Equal(beforeLanding + 3, reached);

        _time.Advance(TimeSpan.FromMilliseconds(200)); // where the check the landing armed comes due
        Assert.Equal(reached, validator.DraftRuleRuns);

        validator.Gate.SetResult();
        await UntilAsync(() => !engine.IsFormValid && load.IsCompleted);
        Assert.False(engine.IsFormValid);
        Assert.Equal(reached, validator.DraftRuleRuns);
    }

    private static FieldIdentifier DescriptionOf(EngineOrder order) => new(order, nameof(EngineOrder.Description));

    /// <summary>Yields until <paramref name="condition"/> holds, for at most five seconds; the caller asserts afterwards.</summary>
    private static async Task UntilAsync(Func<bool> condition)
    {
        for (var waited = 0; waited < 500 && !condition(); waited++)
        {
            await Task.Delay(10);
        }
    }

    /// <summary>Releases every run of the async rule until <paramref name="condition"/> holds, for at most five seconds; the caller asserts afterwards.</summary>
    private static async Task ReleaseUntilAsync(PerRunGatedValidator validator, Func<bool> condition)
    {
        for (var waited = 0; waited < 500; waited++)
        {
            validator.ReleaseAll();
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }
    }

    /// <summary>What a submit says of the form as it stands, the truth <see cref="FormidableEngine{TModel}.IsFormValid"/> must settle on.</summary>
    private static async Task<bool> SubmitSaysAsync(FormidableEngine<EngineOrder> engine, PerRunGatedValidator validator)
    {
        var submit = engine.ValidateForSubmitAsync();
        while (!submit.IsCompleted)
        {
            validator.ReleaseAll();
            await Task.Delay(10);
        }

        return (await submit).CanProceed;
    }

    [Fact]
    public async Task Probe_fault_routes_to_ValidationFaulted_and_leaves_IsFormValid_unchanged()
    {
        var order = new EngineOrder();
        var validator = new ThrowingValidator();
        var editContext = new EditContext(order);
        using var engine = new FormidableEngine<EngineOrder>(
            order,
            editContext,
            new FluentValidationModelValidator<EngineOrder>(validator),
            new ReflectionModelIntrospector(),
            new FormidableOptions
            {
                TrackFormValidity = true,
                // Isolates the probe's own fault from the live pass, so an observed
                // ValidationFaulted below is attributable to the probe and nothing else: this
                // profile selects the "Submit" ruleset (registered on ThrowingValidator, but
                // EMPTY there) and excludes default rules, so it runs zero rules and never
                // reaches the throwing one — that rule lives in the default ruleset, which
                // only the probe (always SubmitProfile, default rules included) still runs.
                LiveProfile = ValidationProfile.Named(
                    "EmptyLive", includeDefaultRules: false, ValidationProfile.SubmitRuleSetName),
            },
            _time);

        // The constructor's own initial probe has already faulted (Throw defaults to true) and
        // its exception was swallowed by nobody being subscribed yet; flush it and record the
        // value it left IsFormValid frozen at before wiring the assertion subscription.
        await FlushAsync();
        var priorValue = engine.IsFormValid;

        Exception? observed = null;
        engine.ValidationFaulted += (_, e) => observed = e.Exception;

        editContext.NotifyFieldChanged(new FieldIdentifier(order, nameof(EngineOrder.Description)));
        await FlushAsync();

        Assert.IsType<InvalidOperationException>(observed);
        Assert.Equal(priorValue, engine.IsFormValid); // frozen, not corrupted by the fault
        // No disclosure from the probe's own fault: no fault issue reaches the message store.
        Assert.Empty(editContext.GetValidationMessages(new FieldIdentifier(order, string.Empty)));
    }

    [Fact]
    public async Task Overlapping_probes_last_write_wins_by_stamp_not_completion_order()
    {
        var order = new EngineOrder();
        var validator = new GatedValidator();
        var editContext = new EditContext(order);
        var descriptionField = new FieldIdentifier(order, nameof(EngineOrder.Description));
        using var engine = new FormidableEngine<EngineOrder>(
            order,
            editContext,
            new FluentValidationModelValidator<EngineOrder>(validator),
            new ReflectionModelIntrospector(),
            // GatedValidator's gate is a submit-bucket rule, and the probe builds its own plan
            // with no dedupe against a pass already running — so an unnarrowed live channel would
            // put TWO chains on each gate and ReleaseAndWaitAsync's margin, written for one,
            // could return before the probe's own landing. The empty draft bucket makes the
            // narrowing exact: the live pass runs nothing and blocks on nothing.
            new FormidableOptions { TrackFormValidity = true, LiveProfile = ValidationProfile.Draft },
            _time);

        // The constructor's own initial probe is left pending on the validator's very first
        // Gate — nothing in this test releases it, and Dispose's own cancellation cleans it up.

        // Probe A: scheduled first (the older stamp), released LAST, and would leave
        // IsFormValid false if it were allowed to win.
        validator.Reset();
        var gateA = validator.Gate;
        order.Description = "edit-a";
        editContext.NotifyFieldChanged(descriptionField);

        // Probe B: scheduled after A (the newer stamp), released FIRST, and leaves
        // IsFormValid true.
        validator.Reset();
        var gateB = validator.Gate;
        order.Description = "edit-b";
        editContext.NotifyFieldChanged(descriptionField);

        validator.ShouldPass = true;
        await ReleaseAndWaitAsync(validator, gateB);

        Assert.True(engine.IsFormValid); // B's (newer) answer landed

        validator.ShouldPass = false;
        await ReleaseAndWaitAsync(validator, gateA);

        // A finishes last in wall-clock time, but it is the OLDER probe — the stamp guard must
        // discard its answer rather than let a late finisher overwrite a newer one.
        Assert.True(engine.IsFormValid);
    }
}
