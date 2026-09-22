using Formidable.Blazor.Tests.Fixtures;
using Formidable.Introspection;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Time.Testing;
using static Formidable.Blazor.Tests.Fixtures.EngineTestSync;

namespace Formidable.Blazor.Tests;

public class FormValidationEngineValidatingScopeTests
{
    [Fact]
    public async Task Live_pass_scopes_the_pending_flag_to_the_triggering_field()
    {
        var order = new EngineOrder();
        var validator = new GatedValidator();
        var editContext = new EditContext(order);
        using var engine = new FormValidationEngine<EngineOrder>(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(validator),
            new ReflectionModelIntrospector(),
            // GatedValidator's async gate lives on the Submit ruleset; running the live pass under
            // it is what lets this test hold a live pass open long enough to assert mid-flight.
            new FormidableOptions { LiveProfile = ValidationProfile.Submit, DisclosureOverride = _ => true },
            new FakeTimeProvider());

        var fieldA = new FieldIdentifier(order, nameof(EngineOrder.Description));
        var fieldB = new FieldIdentifier(order, nameof(EngineOrder.Customer));

        editContext.NotifyFieldChanged(fieldA);

        Assert.True(engine.GetFieldState(fieldA).IsValidating);  // triggering field: scoped flag set
        Assert.False(engine.GetFieldState(fieldB).IsValidating); // untouched field: scope excludes it
        Assert.True(engine.IsValidating);                        // engine-level flag stays form-wide

        // Subscribe only now the pass is confirmed in flight — MarkTouched's own StateChanged
        // notification (fired before the pass flips IsValidating true) would otherwise resolve
        // quiescence prematurely.
        var quiescent = new TaskCompletionSource();
        engine.StateChanged += () =>
        {
            if (!engine.IsValidating)
            {
                quiescent.TrySetResult();
            }
        };

        validator.Gate.SetResult();
        await quiescent.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(engine.GetFieldState(fieldA).IsValidating);
        Assert.False(engine.GetFieldState(fieldB).IsValidating);
    }

    [Fact]
    public async Task Live_pass_on_one_async_field_does_not_mark_a_sibling_async_field_validating()
    {
        // Two independent async draft rules (like the sample's Handle validator). Before
        // any submit, trigger a live pass on field B and, while it is in flight, assert
        // IsValidating is true for B and FALSE for A. Pins the field-scoped live pass the
        // docs promise; the post-submit refresh's own scoping is pinned separately below.
        var order = new EngineOrder { Customer = new EngineCustomer() };
        var validator = new TwoAsyncFieldsValidator();
        var editContext = new EditContext(order);
        using var engine = new FormValidationEngine<EngineOrder>(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(validator),
            new ReflectionModelIntrospector(),
            new FormidableOptions(),
            new FakeTimeProvider());

        var fieldA = new FieldIdentifier(order, nameof(EngineOrder.Description));
        var fieldB = new FieldIdentifier(order.Customer, nameof(EngineCustomer.Name));

        editContext.NotifyFieldChanged(fieldB);

        Assert.True(engine.GetFieldState(fieldB).IsValidating);  // triggering field: scoped flag set
        Assert.False(engine.GetFieldState(fieldA).IsValidating); // sibling async field: scope excludes it
        Assert.True(engine.IsValidating);                        // engine-level flag stays form-wide

        // Subscribe only now the pass is confirmed in flight — MarkTouched's own StateChanged
        // notification (fired before the pass flips IsValidating true) would otherwise resolve
        // quiescence prematurely.
        var quiescent = new TaskCompletionSource();
        engine.StateChanged += () =>
        {
            if (!engine.IsValidating)
            {
                quiescent.TrySetResult();
            }
        };

        validator.CustomerNameGate.SetResult();
        await quiescent.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(engine.GetFieldState(fieldA).IsValidating);
        Assert.False(engine.GetFieldState(fieldB).IsValidating);
    }

    [Fact]
    public async Task Submit_pass_marks_every_field_validating()
    {
        var order = new EngineOrder();
        var validator = new GatedValidator();
        using var engine = new FormValidationEngine<EngineOrder>(
            order, new EditContext(order),
            new FluentValidationModelValidator<EngineOrder>(validator),
            new ReflectionModelIntrospector(),
            new FormidableOptions { DisclosureOverride = _ => true },
            new FakeTimeProvider());

        var fieldA = new FieldIdentifier(order, nameof(EngineOrder.Description));
        var fieldB = new FieldIdentifier(order, nameof(EngineOrder.Customer));

        var pending = engine.ValidateForSubmitAsync();

        Assert.True(engine.GetFieldState(fieldA).IsValidating); // submit pass is form-wide
        Assert.True(engine.GetFieldState(fieldB).IsValidating); // ...even for a field the pass never touches

        validator.Gate.SetResult();
        await pending;

        Assert.False(engine.GetFieldState(fieldA).IsValidating);
        Assert.False(engine.GetFieldState(fieldB).IsValidating);
    }

    [Fact]
    public async Task Submit_flags_all_fields_while_in_flight()
    {
        // Distinct from Submit_pass_marks_every_field_validating above: here field A is edited
        // WHILE the submit is in flight (the edit's own live pass defers to the higher-intent
        // submit and no-ops), which is also the exact condition that now accumulates a field into
        // the refresh scope. Submit's own IsValidating must stay form-wide regardless - the new
        // accumulator must not leak into or narrow the submit pass's scope.
        var order = new EngineOrder();
        var validator = new GatedValidator();
        var editContext = new EditContext(order);
        using var engine = new FormValidationEngine<EngineOrder>(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(validator),
            new ReflectionModelIntrospector(),
            new FormidableOptions { DisclosureOverride = _ => true },
            new FakeTimeProvider());

        var fieldA = new FieldIdentifier(order, nameof(EngineOrder.Description));
        var fieldB = new FieldIdentifier(order, nameof(EngineOrder.Customer));

        var pending = engine.ValidateForSubmitAsync();
        editContext.NotifyFieldChanged(fieldA); // edited mid-submit

        Assert.True(engine.GetFieldState(fieldA).IsValidating); // edited field: still flagged, form-wide
        Assert.True(engine.GetFieldState(fieldB).IsValidating); // untouched field: also flagged, form-wide

        validator.Gate.SetResult();
        await pending;

        Assert.False(engine.GetFieldState(fieldA).IsValidating);
        Assert.False(engine.GetFieldState(fieldB).IsValidating);
    }

    [Fact]
    public async Task Refresh_flags_only_the_fields_edited_in_the_debounce_window()
    {
        // Two independent async draft rules, as the sibling-field live-pass test above uses. The
        // refresh pass runs only what the live pass left out, which is why holding one in flight
        // means holding a rule from the fixture's SUBMIT bucket: gating the draft bucket alone
        // would hold live passes and let the refresh run straight through.
        var order = new EngineOrder { Customer = new EngineCustomer() };
        var validator = new TwoAsyncFieldsValidator();
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        using var engine = new FormValidationEngine<EngineOrder>(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(validator),
            new ReflectionModelIntrospector(),
            new FormidableOptions(),
            time);

        var fieldA = new FieldIdentifier(order, nameof(EngineOrder.Description));
        var fieldB = new FieldIdentifier(order.Customer, nameof(EngineCustomer.Name));

        // Submit once (both fields flagged - that is the submit pass, not under test here) and
        // let it complete.
        var submit = engine.ValidateForSubmitAsync();
        validator.CustomerNameGate.SetResult();
        await submit;
        validator.Reset();

        // Notify a change for field B only. The edit's own live pass runs first and the refresh
        // defers to it, so settle that pass and re-gate the validator before advancing the fake
        // clock past the 300ms debounce - that is what leaves the REFRESH pass, this test's
        // subject, sitting in flight.
        editContext.NotifyFieldChanged(fieldB);
        var live = Quiescence(engine);
        validator.CustomerNameGate.SetResult();
        await live;
        validator.Reset();

        time.Advance(TimeSpan.FromMilliseconds(301));

        Assert.True(engine.GetFieldState(fieldB).IsValidating);  // edited in the window: scoped flag set
        Assert.False(engine.GetFieldState(fieldA).IsValidating); // untouched: scope excludes it
        Assert.True(engine.IsValidating);                        // engine-level flag stays form-wide

        var settled = Quiescence(engine);
        validator.CustomerNameGate.SetResult();
        await settled;

        Assert.False(engine.GetFieldState(fieldA).IsValidating);
        Assert.False(engine.GetFieldState(fieldB).IsValidating);
    }

    [Fact]
    public async Task Refresh_flags_every_field_edited_in_the_window()
    {
        var order = new EngineOrder { Customer = new EngineCustomer() };
        var validator = new TwoAsyncFieldsValidator();
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        using var engine = new FormValidationEngine<EngineOrder>(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(validator),
            new ReflectionModelIntrospector(),
            new FormidableOptions(),
            time);

        var fieldA = new FieldIdentifier(order, nameof(EngineOrder.Description));
        var fieldB = new FieldIdentifier(order.Customer, nameof(EngineCustomer.Name));

        var submit = engine.ValidateForSubmitAsync();
        validator.CustomerNameGate.SetResult();
        await submit;
        validator.Reset();

        // Notify changes for BOTH fields before the debounce elapses, settling each edit's own
        // live pass (the refresh defers to one that is still running) so the refresh is what
        // sits in flight when the clock advances.
        editContext.NotifyFieldChanged(fieldA);
        var liveA = Quiescence(engine);
        validator.CustomerNameGate.SetResult();
        await liveA;
        validator.Reset();

        editContext.NotifyFieldChanged(fieldB);
        var liveB = Quiescence(engine);
        validator.CustomerNameGate.SetResult();
        await liveB;
        validator.Reset();

        time.Advance(TimeSpan.FromMilliseconds(301));

        Assert.True(engine.GetFieldState(fieldA).IsValidating); // both edited in the window
        Assert.True(engine.GetFieldState(fieldB).IsValidating);
        Assert.True(engine.IsValidating);

        var settled = Quiescence(engine);
        validator.CustomerNameGate.SetResult();
        await settled;

        Assert.False(engine.GetFieldState(fieldA).IsValidating);
        Assert.False(engine.GetFieldState(fieldB).IsValidating);
    }

    [Fact]
    public async Task Live_pass_superseding_an_in_flight_refresh_takes_the_pending_indicator_with_it()
    {
        // Post-submit, edit B and let its debounced refresh (#1) start and sit in flight
        // (scope {B}). Editing A mid-flight supersedes refresh #1 exactly as a live pass
        // supersedes any prior pass - refresh #1's scope goes with it, deliberately, rather
        // than being unioned into whatever runs next (mirrors pre-submit live-pass
        // supersession, where editing B then A also unflags B). Refresh #2, fired by A's own
        // debounce, then flags only A - this also pins that a field edited while a refresh is
        // in flight (A here) accumulates for the NEXT window instead of being lost.
        var order = new EngineOrder { Customer = new EngineCustomer() };
        var validator = new TwoAsyncFieldsValidator();
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        using var engine = new FormValidationEngine<EngineOrder>(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(validator),
            new ReflectionModelIntrospector(),
            new FormidableOptions(),
            time);

        var fieldA = new FieldIdentifier(order, nameof(EngineOrder.Description));
        var fieldB = new FieldIdentifier(order.Customer, nameof(EngineCustomer.Name));

        var submit = engine.ValidateForSubmitAsync();
        validator.CustomerNameGate.SetResult();
        await submit;
        validator.Reset();

        // Edit B and settle the edit's own live pass first (a refresh defers to one still in
        // flight), so its refresh (#1) is what starts on the debounce and sits in flight,
        // scoped to {B}.
        editContext.NotifyFieldChanged(fieldB);
        var liveB = Quiescence(engine);
        validator.CustomerNameGate.SetResult();
        await liveB;
        validator.Reset();

        time.Advance(TimeSpan.FromMilliseconds(301));
        Assert.True(engine.GetFieldState(fieldB).IsValidating); // refresh #1 in flight, scoped to B

        // Edit A mid-flight: supersedes refresh #1 (live passes never defer to refreshes),
        // takes the pending indicator with it, and re-arms the debounce for a second refresh.
        editContext.NotifyFieldChanged(fieldA);

        Assert.True(engine.GetFieldState(fieldA).IsValidating);  // the superseding live pass: scoped to the new edit
        Assert.False(engine.GetFieldState(fieldB).IsValidating); // B's scope was dropped by the supersession
        Assert.True(engine.IsValidating);                        // engine-level flag stays form-wide

        // Settle A's live pass so refresh #2, which its edit re-armed, can run: it flags A alone,
        // pinning that a field edited while a refresh was in flight accumulates for the NEXT
        // window instead of being lost.
        var liveA = Quiescence(engine);
        validator.CustomerNameGate.SetResult();
        await liveA;
        validator.Reset();

        time.Advance(TimeSpan.FromMilliseconds(301));

        Assert.True(engine.GetFieldState(fieldA).IsValidating);  // refresh #2: scoped to the new edit
        Assert.False(engine.GetFieldState(fieldB).IsValidating);
        Assert.True(engine.IsValidating);

        var settled = Quiescence(engine);
        validator.CustomerNameGate.SetResult();
        await settled;

        Assert.False(engine.GetFieldState(fieldA).IsValidating);
        Assert.False(engine.GetFieldState(fieldB).IsValidating);
    }

    [Fact]
    public async Task LiveDebounce_pending_fields_survive_a_submit_that_starts_inside_the_window()
    {
        // The debounce timer is a plain ITimer, independent of pass supersession (like the
        // refresh timer) - it keeps counting through the submit and fires afterward, running
        // the live pass its own accumulator collected. This is "the deferred pass" landing:
        // the field was never added to _pendingRefreshFields (HasSubmitted/SubmitInFlight were
        // both false at edit time), so the post-submit refresh set is not what saves it here.
        var order = new EngineOrder { Description = "ok", Customer = new EngineCustomer() };
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        using var engine = new FormValidationEngine<EngineOrder>(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(new EngineOrderValidator()),
            new ReflectionModelIntrospector(),
            new FormidableOptions { LiveDebounce = TimeSpan.FromMilliseconds(400) },
            time);

        var descriptionField = new FieldIdentifier(order, nameof(EngineOrder.Description));

        editContext.NotifyFieldChanged(descriptionField); // opens the debounce window
        time.Advance(TimeSpan.FromMilliseconds(100));      // window still open

        // Submit starts and completes fully inside the open window, while Description is still
        // valid for both the Submit ruleset and the (IncludeDefaultRules) draft rule it also
        // runs, so nothing here masks the assertion below.
        var outcome = await engine.ValidateForSubmitAsync();
        Assert.True(outcome.CanProceed);

        // Made invalid AFTER the submit that read it - the deferred pass below reads the
        // model's current value, not a snapshot taken when the edit that armed it happened.
        order.Description = new string('x', 11);

        time.Advance(TimeSpan.FromMilliseconds(300)); // 400ms since the edit: the deferred pass runs

        // The live pass the original edit armed still fires and catches the draft rule's
        // MaximumLength(10) failure - the pending field was not silently dropped by the submit.
        Assert.NotEmpty(editContext.GetValidationMessages(descriptionField));
    }

    [Fact]
    public async Task Debounced_live_pass_defers_to_an_in_flight_refresh_and_still_lands_afterward()
    {
        // A single IsValidating/scope assertion cannot fail-distinguish deferral from the bug:
        // both a wrongly-superseding live pass and a correctly-deferring one leave the same
        // field flagged validating right after the fire. What differs is whether the refresh
        // that was in flight ever gets to write its own verdict - so this asserts the SUBMIT
        // channel content the refresh owns, and that the deferred live pass still lands once it
        // is finally let through.
        //
        // With LiveDebounce set, a refresh armed by the SAME edit that opened a live window
        // always comes due after that window, so a refresh cannot be caught in flight by the live
        // pass its own edit triggered. To get a refresh genuinely running when a live window
        // closes, the refresh here belongs to an EARLIER edit (naming the customer) whose own
        // live pass already landed; a SECOND edit then opens a new live window while that earlier
        // refresh is still blocked on its own async rule.
        var customer = new EngineCustomer { Name = "" };
        var order = new EngineOrder { Description = "Quarterly refresh", Customer = customer };
        var validator = new GatedChannelSeparatingValidator();
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        using var engine = new FormValidationEngine<EngineOrder>(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(validator),
            new ReflectionModelIntrospector(),
            new FormidableOptions
            {
                LiveDebounce = TimeSpan.FromMilliseconds(100),
                RefreshDebounce = TimeSpan.FromMilliseconds(50),
                DisclosureOverride = _ => true,
            },
            time);

        const string DraftMessage = "Customer name must be four characters or fewer";
        const string SubmitMessage = "Description needs a named customer";

        var customerName = new FieldIdentifier(customer, nameof(EngineCustomer.Name));
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));

        // Submit with no customer name, so the submit rule fails and Description becomes the
        // field only the post-submit refresh can resurface.
        var submit = engine.ValidateForSubmitAsync();
        validator.Gate.SetResult();
        Assert.False((await submit).CanProceed);
        Assert.Contains(engine.GetIssues(description), i => i.Message == SubmitMessage);
        validator.Reset();

        // Naming the customer arms a live pass, and — since HasSubmitted — a refresh that
        // follows it rather than racing it.
        customer.Name = "ok";
        editContext.NotifyFieldChanged(customerName);

        var liveASettled = Quiescence(engine);
        time.Advance(TimeSpan.FromMilliseconds(100)); // the live window closes and blocks
        validator.Gate.SetResult(); // "ok" passes the draft rule
        await liveASettled;
        validator.Reset();

        var refreshASettled = Quiescence(engine);
        time.Advance(TimeSpan.FromMilliseconds(50)); // the refresh follows and blocks on its own rule
        Assert.True(engine.IsValidating);

        // A second edit lands while that refresh is still in flight. It opens a new live window
        // (and, since HasSubmitted, re-arms its own refresh too) without disturbing the refresh
        // already running — a debounced edit never cancels anything in flight.
        customer.Name = "far too long";
        editContext.NotifyFieldChanged(customerName);

        time.Advance(TimeSpan.FromMilliseconds(100)); // the new live window closes against the in-flight refresh

        // Deferred, not clobbered: the refresh is still the pass in flight, uninterrupted.
        Assert.True(engine.IsValidating);

        // Releases the in-flight refresh onto ITS gate — deliberately not reset again. The second
        // edit also re-armed its OWN refresh (now due 50 ms further on), which will find the
        // retained live report stale (the edit moved the model on after it was taken) and fall
        // back to the full profile; leaving the gate already completed is what lets that fallback
        // - and the deferred live pass right behind it - both resolve without a rendezvous of
        // their own, exactly as an already-settled async rule would for a real validator.
        validator.Gate.SetResult();
        await refreshASettled;

        // The first refresh's own verdict landed - had the debounced pass cancelled it instead,
        // this stays stuck on the stale submit-time message forever (nothing left re-arms a
        // cancelled refresh under LiveDebounce - see RefreshInFlight's remarks).
        Assert.DoesNotContain(engine.GetIssues(description), i => i.Message == SubmitMessage);

        // The second edit's own refresh comes due next and, finding its retained report stale,
        // runs (and instantly clears, on the already-completed gate) the full profile.
        time.Advance(TimeSpan.FromMilliseconds(50));

        // The debounced live pass was deferred, not dropped: it re-arms itself once more and
        // still owes Customer.Name a verdict for the second edit - captured only now, after the
        // second refresh's own transient pass has come and gone.
        var liveBSettled = Quiescence(engine);
        time.Advance(TimeSpan.FromMilliseconds(50));
        await liveBSettled;

        Assert.Contains(engine.GetIssues(customerName), i => i.Message == DraftMessage);
    }

    [Fact]
    public async Task Debounced_live_pass_defers_while_a_submit_is_in_flight_and_still_lands_afterward()
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
            new FormidableOptions { LiveDebounce = TimeSpan.FromMilliseconds(400), DisclosureOverride = _ => true },
            time);

        var customerName = new FieldIdentifier(customer, nameof(EngineCustomer.Name));

        editContext.NotifyFieldChanged(customerName); // opens the debounce window; Name is still valid

        // A submit starts and holds itself open - Submit's IncludeDefaultRules runs the same
        // gated Description rule, so releasing it is what lets the submit (and later the live
        // pass) finish.
        var submit = engine.ValidateForSubmitAsync();

        time.Advance(TimeSpan.FromMilliseconds(400)); // window closes while the submit is in flight

        // Deferred, not dropped: SubmitInFlight is still true, so the fire re-arms itself
        // instead of snapshotting the accumulator or starting a pass.
        Assert.True(engine.IsValidating); // the submit, uninterrupted

        validator.Gate.SetResult();
        await submit; // Name was "" throughout - the submit's own report has nothing to say about it
        validator.Reset();

        // Mutated only after the submit fully finished reading it, so this failure can only
        // ever reach the store through the deferred live pass, not the submit's own
        // (already-complete) report.
        customer.Name = "far too long";

        // The re-armed debounce still owes Customer.Name a verdict.
        var liveSettled = Quiescence(engine);
        time.Advance(TimeSpan.FromMilliseconds(400));
        validator.Gate.SetResult();
        await liveSettled;

        Assert.Contains(engine.GetIssues(customerName), i => i.Message == "Customer name is too long");
    }
}
