using Formidable.Blazor.Tests.Fixtures;
using Formidable.Introspection;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Time.Testing;

namespace Formidable.Blazor.Tests;

public class FormValidationEngineValidityTests
{
    private readonly EngineOrder _order = new();
    private readonly EditContext _editContext;
    private readonly FormValidationEngine<EngineOrder> _engine;
    private readonly FakeTimeProvider _time = new();

    public FormValidationEngineValidityTests()
    {
        _editContext = new EditContext(_order);
        _engine = new FormValidationEngine<EngineOrder>(
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
        _engine.StateChanged += () => raised++;

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
        var counting = new CountingValidator(
            new FluentValidationModelValidator<EngineOrder>(new EngineOrderValidator()));
        using var engine = new FormValidationEngine<EngineOrder>(
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
        using var engine = new FormValidationEngine<EngineOrder>(
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
        using var engine = new FormValidationEngine<EngineOrder>(
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
        engine.StateChanged += () =>
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
        using var engine = new FormValidationEngine<EngineOrder>(
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

    [Fact]
    public async Task Probe_fault_routes_to_ValidationFaulted_and_leaves_IsFormValid_unchanged()
    {
        var order = new EngineOrder();
        var validator = new ThrowingValidator();
        var editContext = new EditContext(order);
        using var engine = new FormValidationEngine<EngineOrder>(
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
        engine.ValidationFaulted += ex => observed = ex;

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
        using var engine = new FormValidationEngine<EngineOrder>(
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
