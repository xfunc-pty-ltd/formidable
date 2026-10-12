using FluentValidation;
using Formidable.Blazor.Tests.Fixtures;
using Formidable.Introspection;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Time.Testing;
using static Formidable.Blazor.Tests.Fixtures.EngineTestSync;

namespace Formidable.Blazor.Tests;

public class FormidableEngineValidatingScopeTests
{
    [Fact]
    public async Task Live_pass_scopes_the_pending_flag_to_the_triggering_field()
    {
        var order = new EngineOrder();
        var validator = new GatedValidator();
        var editContext = new EditContext(order);
        using var engine = new FormidableEngine<EngineOrder>(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(validator),
            new ReflectionModelIntrospector(),
            // GatedValidator's async gate lives on the Submit ruleset, which the live channel
            // selects on its own — that is what lets this test hold a live pass open long enough
            // to assert mid-flight.
            new FormidableOptions { DisclosureOverride = _ => true },
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
        engine.StateChanged += (_, _) =>
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
        using var engine = new FormidableEngine<EngineOrder>(
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
        engine.StateChanged += (_, _) =>
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
        using var engine = new FormidableEngine<EngineOrder>(
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
        using var engine = new FormidableEngine<EngineOrder>(
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
        // would hold live passes and let the refresh run straight through. The live channel is
        // narrowed to that draft bucket to leave the submit-bucket rule for the refresh — an
        // unnarrowed one consumes it in the live pass and the refresh finds nothing to block on.
        var order = new EngineOrder { Customer = new EngineCustomer() };
        var validator = new TwoAsyncFieldsValidator();
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        using var engine = new FormidableEngine<EngineOrder>(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(validator),
            new ReflectionModelIntrospector(),
            new FormidableOptions { LiveProfile = ValidationProfile.Draft },
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
        using var engine = new FormidableEngine<EngineOrder>(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(validator),
            new ReflectionModelIntrospector(),
            // Narrowed as the test above is, and for the same reason: the refresh has to be left
            // a rule of its own to block on.
            new FormidableOptions { LiveProfile = ValidationProfile.Draft },
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
        // supersedes any prior pass. The live channel is narrowed, so A's live pass does not
        // answer the re-check refresh #1 was running for B: B is unlit while it runs, and waits
        // for the next refresh. Refresh #2, fired by A's own debounce, then flags A and B, since
        // it answers both. This also pins that a field edited while a refresh is in flight (A
        // here) accumulates for the NEXT window instead of being lost.
        var order = new EngineOrder { Customer = new EngineCustomer() };
        var validator = new TwoAsyncFieldsValidator();
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        using var engine = new FormidableEngine<EngineOrder>(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(validator),
            new ReflectionModelIntrospector(),
            // Narrowed as the two tests above are, so each refresh has a submit-bucket rule of
            // its own to block on rather than finding the live pass has already answered it.
            new FormidableOptions { LiveProfile = ValidationProfile.Draft },
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
        Assert.False(engine.GetFieldState(fieldB).IsValidating); // the narrowed live pass does not answer B's re-check
        Assert.True(engine.IsValidating);                        // engine-level flag stays form-wide

        // Settle A's live pass so refresh #2, which its edit re-armed, can run. It flags A, pinning
        // that a field edited while a refresh was in flight accumulates for the NEXT window instead
        // of being lost, and B, whose re-check refresh #1 was still running when A's edit cancelled it.
        var liveA = Quiescence(engine);
        validator.CustomerNameGate.SetResult();
        await liveA;
        validator.Reset();

        time.Advance(TimeSpan.FromMilliseconds(301));

        Assert.True(engine.GetFieldState(fieldA).IsValidating);  // refresh #2: the new edit
        Assert.True(engine.GetFieldState(fieldB).IsValidating);  // ...and the re-check it answers in refresh #1's place
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
        // The submit profile deliberately excludes the draft bucket: a submit populates the
        // verdict store for whatever it runs, and the deferred pass proving it reads the model
        // CURRENT needs its rule to be one no earlier pass has answered at this stamp. The live
        // channel is pointed at that draft bucket explicitly, since tracking the submit profile
        // would leave it running the very selection the submit has already answered.
        var order = new EngineOrder { Description = "ok", Customer = new EngineCustomer() };
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        using var engine = new FormidableEngine<EngineOrder>(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(new EngineOrderValidator()),
            new ReflectionModelIntrospector(),
            new FormidableOptions
            {
                LiveDebounce = TimeSpan.FromMilliseconds(400),
                LiveProfile = ValidationProfile.Draft,
                SubmitProfile = ValidationProfile.Named("SubmitOnly", includeDefaultRules: false, "Submit"),
            },
            time);

        var descriptionField = new FieldIdentifier(order, nameof(EngineOrder.Description));

        editContext.NotifyFieldChanged(descriptionField); // opens the debounce window
        time.Advance(TimeSpan.FromMilliseconds(100));      // window still open

        // Submit starts and completes fully inside the open window, while Description is still
        // valid for the Submit ruleset it runs, so nothing here masks the assertion below.
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
        // To get a refresh genuinely running when a live window closes, the refresh here belongs
        // to an EARLIER edit (naming the customer) whose own live pass already landed and stored
        // the draft rule's verdict — so the refresh executes only the submit rule and blocks on
        // it; a SECOND edit then opens a new live window while that refresh is still in flight.
        var customer = new EngineCustomer { Name = "" };
        var order = new EngineOrder { Description = "Quarterly refresh", Customer = customer };
        var validator = new GatedChannelSeparatingValidator();
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        using var engine = new FormidableEngine<EngineOrder>(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(validator),
            new ReflectionModelIntrospector(),
            new FormidableOptions
            {
                LiveDebounce = TimeSpan.FromMilliseconds(100),
                RefreshDebounce = TimeSpan.FromMilliseconds(300),
                // The two channels have to stay on separate rules for the refresh to be holdable
                // in flight at all: narrowed to the draft bucket, the live pass leaves the gated
                // submit-bucket rule for the refresh to block on.
                LiveProfile = ValidationProfile.Draft,
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

        // Naming the customer arms a live pass and a refresh behind it.
        customer.Name = "ok";
        editContext.NotifyFieldChanged(customerName);

        var liveASettled = Quiescence(engine);
        time.Advance(TimeSpan.FromMilliseconds(100)); // the live window closes and blocks
        validator.Gate.SetResult(); // "ok" passes the draft rule
        await liveASettled;
        validator.Reset();

        var refreshASettled = Quiescence(engine);
        time.Advance(TimeSpan.FromMilliseconds(200)); // the refresh follows and blocks on its own rule
        Assert.True(engine.IsValidating);

        // A second edit lands while that refresh is still in flight. It opens a new live window
        // (and, since HasSubmitted, re-arms its own refresh too) without disturbing the refresh
        // already running — a debounced edit never cancels anything in flight.
        customer.Name = "far too long";
        editContext.NotifyFieldChanged(customerName);

        time.Advance(TimeSpan.FromMilliseconds(100)); // the new live window closes against the in-flight refresh

        // Deferred, not clobbered: the refresh is still the pass in flight, uninterrupted.
        Assert.True(engine.IsValidating);

        // Releases the in-flight refresh onto ITS gate — deliberately not reset again. Leaving
        // the gate already completed is what lets the deferred live pass, and the second edit's
        // own refresh behind it, both resolve without a rendezvous of their own, exactly as an
        // already-settled async rule would for a real validator.
        validator.Gate.SetResult();
        await refreshASettled;

        // The first refresh's own verdict landed - had the debounced pass cancelled it instead,
        // this stays stuck on the stale submit-time message forever (nothing left re-arms a
        // cancelled refresh under LiveDebounce - see RefreshInFlight's remarks).
        Assert.DoesNotContain(engine.GetIssues(description), i => i.Message == SubmitMessage);

        // The debounced live pass was deferred, not dropped: its re-armed window closes and the
        // pass it starts still owes Customer.Name a verdict for the second edit — the draft
        // rule's stored verdict carries the FIRST edit's stamp, so it executes afresh.
        var liveBSettled = Quiescence(engine);
        time.Advance(TimeSpan.FromMilliseconds(100));
        await liveBSettled;

        Assert.Contains(engine.GetIssues(customerName), i => i.Message == DraftMessage);
    }

    [Fact]
    public async Task Debounced_live_pass_defers_while_a_submit_is_in_flight_and_still_lands_afterward()
    {
        // No DisclosureOverride and nothing registered, so the submit channel SUPPRESSES the
        // name failure the submit computes — which is what makes the deferred pass's own landing
        // observable: only a live pass's verdict apply, where engagement rather than
        // registration decides disclosure, can put the message on the field.
        var customer = new EngineCustomer { Name = "far too long" };
        var order = new EngineOrder { Customer = customer };
        var validator = new SlowLiveRuleValidator();
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        using var engine = new FormidableEngine<EngineOrder>(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(validator),
            new ReflectionModelIntrospector(),
            new FormidableOptions { LiveDebounce = TimeSpan.FromMilliseconds(400) },
            time);

        var customerName = new FieldIdentifier(customer, nameof(EngineCustomer.Name));

        editContext.NotifyFieldChanged(customerName); // opens the debounce window; the name already fails

        // A submit starts and holds itself open - Submit's IncludeDefaultRules runs the same
        // gated Description rule, so releasing it is what lets the submit finish.
        var submit = engine.ValidateForSubmitAsync();

        time.Advance(TimeSpan.FromMilliseconds(400)); // window closes while the submit is in flight

        // Deferred, not dropped: SubmitInFlight is still true, so the fire waits for the
        // submit's end instead of snapshotting the accumulator or starting a pass.
        Assert.True(engine.IsValidating); // the submit, uninterrupted

        validator.Gate.SetResult();
        await submit;

        // The submit computed the failure and suppressed it at the channel: the field shows
        // nothing yet, so whatever appears below is the deferred pass's own doing.
        Assert.Empty(engine.GetIssues(customerName));

        // The re-armed debounce still owes Customer.Name a verdict. The pass it starts finds
        // every rule answered at its own stamp — the submit populated the store — so it
        // executes nothing and publishes the stored verdict into the live channel.
        var liveSettled = Quiescence(engine);
        time.Advance(TimeSpan.FromMilliseconds(400));
        await liveSettled;

        Assert.Contains(engine.GetIssues(customerName), i => i.Message == "Customer name is too long");
    }

    // A load nobody asked for lights no pending indicator. The pass is form-wide - it answers
    // for the whole model, and IsValidating says so, which is what a page-level spinner reads -
    // but no field is waiting on it, so the field-scoped flag stays down everywhere, including
    // on a field no rule mentions at all. The submit at the end is the positive control: the
    // same field, the same gated validator, form-wide because the visitor asked. Mutation this
    // breaks: hand the load pass a null scope, which IsFieldValidating reads as "every field".
    [Fact]
    public async Task A_load_pass_marks_no_field_validating_while_a_submit_marks_them_all()
    {
        var order = new EngineOrder();
        var validator = new GatedValidator();
        var editContext = new EditContext(order);
        using var engine = new FormidableEngine<EngineOrder>(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(validator),
            new ReflectionModelIntrospector(),
            new FormidableOptions(),
            new FakeTimeProvider());

        var described = new FieldIdentifier(order, nameof(EngineOrder.Description));
        var unruled = new FieldIdentifier(order, nameof(EngineOrder.Location));

        var load = engine.DiscloseLoadedValuesAsync();

        Assert.True(engine.IsValidating);
        Assert.False(engine.GetFieldState(described).IsValidating);
        Assert.False(engine.GetFieldState(unruled).IsValidating);

        validator.Gate.SetResult();
        await load;

        Assert.False(engine.IsValidating);

        validator.Reset();
        var submit = engine.ValidateForSubmitAsync();

        Assert.True(engine.IsValidating);
        Assert.True(engine.GetFieldState(described).IsValidating);
        Assert.True(engine.GetFieldState(unruled).IsValidating);

        validator.Gate.SetResult();
        await submit;
    }

    // Before a submit, a whole-form re-check that comes due while a debounced edit is still
    // waiting runs that field's check itself, and the debounced pass that follows answers from
    // the stored verdict at once. So the re-check is the only moment the check is out, and the
    // field has to read as pending for it. Mutation this breaks: take the waiting fields back out
    // of the re-check's pending scope.
    [Fact]
    public async Task A_refresh_answering_a_waiting_debounced_edit_flags_that_field_before_any_submit()
    {
        var order = new EngineOrder { Customer = new EngineCustomer() };
        var validator = new FilledFieldGatedValidator();
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        using var engine = new FormidableEngine<EngineOrder>(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(validator),
            new ReflectionModelIntrospector(),
            new FormidableOptions { LiveDebounce = TimeSpan.FromMilliseconds(400) },
            time);
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));
        var customerName = new FieldIdentifier(order.Customer, nameof(EngineCustomer.Name));

        // The first render arms the re-check at the default 300 ms; the edit lands inside it and
        // opens a 400 ms debounce window that outlasts it.
        engine.OnRenderedFieldsChanged();
        order.Description = "ada";
        editContext.NotifyFieldChanged(description);
        time.Advance(TimeSpan.FromMilliseconds(301));

        Assert.Equal(1, validator.Started);
        Assert.True(engine.GetFieldState(description).IsValidating);
        Assert.False(engine.GetFieldState(customerName).IsValidating);

        var settled = Quiescence(engine);
        validator.Gate.SetResult();
        await settled;

        Assert.False(engine.GetFieldState(description).IsValidating);
    }

    // The other half of the same scope: a field that is not waiting on a debounced check is not
    // flagged by a re-check that happens to run its rules. Here the edit's debounced pass has
    // already answered, and a later change to the rendered field set empties the stored verdicts,
    // so the re-check it arms runs the rule again with nothing waiting. Mutation this breaks:
    // widen the scope to every engaged field.
    [Fact]
    public async Task A_refresh_before_any_submit_flags_no_field_that_is_not_waiting_on_a_debounced_check()
    {
        var order = new EngineOrder { Customer = new EngineCustomer() };
        var validator = new FilledFieldGatedValidator();
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        using var engine = new FormidableEngine<EngineOrder>(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(validator),
            new ReflectionModelIntrospector(),
            new FormidableOptions { LiveDebounce = TimeSpan.FromMilliseconds(400) },
            time);
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));

        order.Description = "ada";
        editContext.NotifyFieldChanged(description);
        time.Advance(TimeSpan.FromMilliseconds(401));
        Assert.True(engine.GetFieldState(description).IsValidating, "the debounced pass should be out");
        var answered = Quiescence(engine);
        validator.Gate.SetResult();
        await answered;
        validator.Reset();

        engine.OnRenderedFieldsChanged();
        time.Advance(TimeSpan.FromMilliseconds(301));

        Assert.Equal(2, validator.Started);
        Assert.True(engine.IsValidating, "the re-check should be held open by the gated rule");
        Assert.False(engine.GetFieldState(description).IsValidating);

        var settled = Quiescence(engine);
        validator.Gate.SetResult();
        await settled;
    }

    // After a submit the waiting field is lent to the re-check too: the submit cleared every
    // live verdict, so the field's own answer is still owed, and this re-check is running its
    // rule. Mutation this breaks: lend the waiting fields only before a submit.
    [Fact]
    public async Task A_post_submit_refresh_flags_a_field_still_waiting_out_its_debounce()
    {
        var order = new EngineOrder { Customer = new EngineCustomer() };
        var validator = new FilledFieldGatedValidator();
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        using var engine = new FormidableEngine<EngineOrder>(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(validator),
            new ReflectionModelIntrospector(),
            new FormidableOptions { LiveDebounce = TimeSpan.FromMilliseconds(400) },
            time);
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));
        var customerName = new FieldIdentifier(order.Customer, nameof(EngineCustomer.Name));

        // Description is edited and Submit pressed inside its window; Name is edited just after the
        // submit lands, which arms the re-check ahead of the window's own fire. (Name edited while
        // the submit runs would make the submit check again, and that check would leave the
        // re-check no rule to run.)
        order.Description = "ada";
        editContext.NotifyFieldChanged(description);
        var submit = engine.ValidateForSubmitAsync();
        validator.Gate.SetResult();
        await submit;
        order.Customer.Name = "bo";
        editContext.NotifyFieldChanged(customerName);

        validator.Reset();
        time.Advance(TimeSpan.FromMilliseconds(301));

        Assert.True(engine.IsValidating, "the re-check should be held open by the gated rule");
        Assert.True(engine.GetFieldState(description).IsValidating);

        var settled = Quiescence(engine);
        validator.Gate.SetResult();
        await settled;
    }

    // A window armed with Timeout.InfiniteTimeSpan never fires, so nothing ever drains it: a field
    // it holds waits on no check, and a re-check must not light it. Mutation this breaks: lend a
    // never-closing window's fields to the re-check's scope.
    [Fact]
    public async Task A_live_window_that_never_closes_lends_no_field_to_a_refresh()
    {
        var order = new EngineOrder { Description = "ok", Customer = new EngineCustomer() };
        var validator = new GatedValidator { ShouldPass = true };
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        using var engine = new FormidableEngine<EngineOrder>(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(validator),
            new ReflectionModelIntrospector(),
            new FormidableOptions { LiveDebounce = Timeout.InfiniteTimeSpan },
            time);
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));
        var customerName = new FieldIdentifier(order.Customer, nameof(EngineCustomer.Name));

        order.Description = "edited before the submit";
        editContext.NotifyFieldChanged(description);
        validator.Gate.SetResult();
        Assert.True((await engine.ValidateForSubmitAsync()).CanProceed);

        validator.Reset();
        order.Customer!.Name = "edited after";
        editContext.NotifyFieldChanged(customerName);
        time.Advance(TimeSpan.FromMilliseconds(301));

        Assert.True(engine.IsValidating, "the re-check should be held open by the gated rule");
        Assert.True(engine.GetFieldState(customerName).IsValidating);
        Assert.False(engine.GetFieldState(description).IsValidating);

        var settled = Quiescence(engine);
        validator.Gate.SetResult();
        await settled;
    }

    // One async rule, on A, and an edit to B while A's check runs. The edit's check cancels A's and
    // runs A's rule again, because any edit leaves every stored answer out of date, so A's answer
    // comes from the newer check and A stays lit until it lands. The control makes no edit to B. A
    // bare touch of B (entering and leaving it) starts nothing. A's rule runs once per check, the
    // carried field adding no run.
    // Mutation: begin each check with its own fields alone, dropping the cancelled check's. The edit
    // row then reads A unlit while its rule runs again.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_field_stays_checking_while_a_newer_check_reruns_its_rule(bool editB)
    {
        var gates = new FieldGates();
        var model = new Pair();
        var editContext = new EditContext(model);
        var dispatch = OneAtATime();
        using var engine = new FormidableEngine<Pair>(
            model, editContext, new FluentValidationModelValidator<Pair>(new OneAsyncRule(gates)),
            new ReflectionModelIntrospector(), new FormidableOptions(), new FakeTimeProvider(), dispatch);
        var a = new FieldIdentifier(model, nameof(Pair.A));
        var b = new FieldIdentifier(model, nameof(Pair.B));

        await OnDispatcher(dispatch, () =>
        {
            model.A = "taken";
            editContext.NotifyFieldChanged(a);
        });
        Assert.True(await UntilAsync(dispatch, () => gates.Started("A") == 1));
        Assert.True(engine.GetFieldState(a).IsValidating);

        await OnDispatcher(dispatch, () => engine.MarkTouched(b));
        Assert.Equal(1, gates.Started("A"));
        Assert.False(engine.GetFieldState(b).IsValidating);

        var seen = new List<(bool Lit, bool Answered)>();
        engine.StateChanged += (_, _) => seen.Add((engine.GetFieldState(a).IsValidating, engine.GetIssues(a).Count > 0));

        if (editB)
        {
            await OnDispatcher(dispatch, () =>
            {
                model.B = "fine";
                editContext.NotifyFieldChanged(b);
            });
            Assert.True(await UntilAsync(dispatch, () => gates.Started("A") == 2));
        }

        Assert.True(await UntilAsync(dispatch, () => gates.Waiting("A") == 1));
        Assert.Equal(editB ? 2 : 1, gates.Started("A"));
        Assert.True(engine.GetFieldState(a).IsValidating);
        Assert.Equal(editB, engine.GetFieldState(b).IsValidating);

        gates.Open("A");
        Assert.True(await UntilAsync(dispatch, () => !engine.IsValidating));

        Assert.Equal([ATaken], engine.GetIssues(a).Select(i => i.Message));
        Assert.False(engine.GetFieldState(a).IsValidating);
        Assert.Equal(editB ? 2 : 1, gates.Started("A"));
        var answeredAt = seen.FindIndex(s => s.Answered);
        Assert.True(answeredAt >= 0);
        Assert.All(seen.Take(answeredAt), s => Assert.True(s.Lit));
    }

    // After a blocked submit, with the live channel narrowed to rules that never reach A's or B's,
    // fixing B arms the whole-form re-check, which lights B while it runs A's rule. An edit to A
    // then cancels that re-check, and its narrowed check answers nothing of B's, so B waits for the
    // next re-check: the one A's edit arms lights B too, and B is lit while its rule runs there.
    // The control makes no edit to A. Each re-check runs A's rule once and B's runs once, so the
    // carried field adds no run.
    // Mutation: drop the cancelled re-check's fields when a narrowed live check cancels it. The edit
    // row then reads B unlit while its rule runs.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_recheck_superseded_by_an_edit_lights_its_fields_when_it_runs_again(bool editA)
    {
        var gates = new FieldGates();
        var model = new Pair { B = "taken" };
        var editContext = new EditContext(model);
        var time = new FakeTimeProvider();
        var dispatch = OneAtATime();
        using var engine = new FormidableEngine<Pair>(
            model, editContext, new FluentValidationModelValidator<Pair>(new TwoAsyncSubmitRules(gates)),
            new ReflectionModelIntrospector(),
            // The override stands in for a rendered field: an engine with no page reveals nothing.
            new FormidableOptions { LiveProfile = ValidationProfile.Draft, DisclosureOverride = _ => true },
            time,
            dispatch);
        var a = new FieldIdentifier(model, nameof(Pair.A));
        var b = new FieldIdentifier(model, nameof(Pair.B));

        gates.Open("A");
        gates.Open("B");
        Assert.False((await SubmitAsync(dispatch, engine)).CanProceed);
        Assert.Equal([BTaken], engine.GetIssues(b).Select(i => i.Message));
        gates.Close("A");
        gates.Close("B");
        var aBefore = gates.Started("A");
        var bBefore = gates.Started("B");

        await OnDispatcher(dispatch, () =>
        {
            model.B = "fine";
            editContext.NotifyFieldChanged(b);
        });
        Assert.True(await UntilAsync(dispatch, () => !engine.IsValidating));
        await OnDispatcher(dispatch, () => time.Advance(PastRefresh));
        Assert.True(await UntilAsync(dispatch, () => gates.Started("A") == aBefore + 1));
        Assert.True(engine.GetFieldState(b).IsValidating);

        if (editA)
        {
            await OnDispatcher(dispatch, () =>
            {
                model.A = "x";
                editContext.NotifyFieldChanged(a);
            });
            Assert.True(await UntilAsync(dispatch, () => !engine.IsValidating));
            await OnDispatcher(dispatch, () => time.Advance(PastRefresh));
            Assert.True(await UntilAsync(dispatch, () => gates.Started("A") == aBefore + 2));
        }

        gates.Open("A");
        Assert.True(await UntilAsync(dispatch, () => gates.Waiting("B") == 1));
        Assert.Equal(editA ? 2 : 1, gates.Started("A") - aBefore);
        Assert.Equal(1, gates.Started("B") - bBefore);
        Assert.True(engine.GetFieldState(b).IsValidating);

        gates.Open("B");
        Assert.True(await UntilAsync(dispatch, () => !engine.IsValidating));
        Assert.Empty(engine.GetIssues(b));
        Assert.False(engine.GetFieldState(b).IsValidating);
    }

    // A whole-form re-check that comes due while another runs cancels it and answers in its place,
    // so it lights the fields the cancelled one was running for. Here B's re-check is running when a
    // change to the rendered field set arms a second one, which lights B and runs B's rule. A's rule
    // runs once in each re-check and B's once, in the second.
    // Mutation: drop the cancelled re-check's fields when a newer re-check cancels it. B then reads
    // unlit while the second re-check runs its rule.
    [Fact]
    public async Task A_newer_recheck_lights_the_fields_of_the_one_it_cancels()
    {
        var gates = new FieldGates();
        var model = new Pair { B = "taken" };
        var editContext = new EditContext(model);
        var time = new FakeTimeProvider();
        var dispatch = OneAtATime();
        using var engine = new FormidableEngine<Pair>(
            model, editContext, new FluentValidationModelValidator<Pair>(new TwoAsyncSubmitRules(gates)),
            new ReflectionModelIntrospector(),
            new FormidableOptions { LiveProfile = ValidationProfile.Draft, DisclosureOverride = _ => true },
            time,
            dispatch);
        var b = new FieldIdentifier(model, nameof(Pair.B));

        gates.Open("A");
        gates.Open("B");
        Assert.False((await SubmitAsync(dispatch, engine)).CanProceed);
        gates.Close("A");
        gates.Close("B");
        var aBefore = gates.Started("A");
        var bBefore = gates.Started("B");

        await OnDispatcher(dispatch, () =>
        {
            model.B = "fine";
            editContext.NotifyFieldChanged(b);
        });
        Assert.True(await UntilAsync(dispatch, () => !engine.IsValidating));
        await OnDispatcher(dispatch, () => time.Advance(PastRefresh));
        Assert.True(await UntilAsync(dispatch, () => gates.Started("A") == aBefore + 1));
        Assert.True(engine.GetFieldState(b).IsValidating);

        await OnDispatcher(dispatch, engine.OnRenderedFieldsChanged);
        await OnDispatcher(dispatch, () => time.Advance(PastRefresh));
        Assert.True(await UntilAsync(dispatch, () => gates.Started("A") == aBefore + 2 && gates.Waiting("A") == 1));
        Assert.True(engine.GetFieldState(b).IsValidating);

        gates.Open("A");
        Assert.True(await UntilAsync(dispatch, () => gates.Waiting("B") == 1));
        Assert.True(engine.GetFieldState(b).IsValidating);

        gates.Open("B");
        Assert.True(await UntilAsync(dispatch, () => !engine.IsValidating));
        Assert.Empty(engine.GetIssues(b));
        Assert.False(engine.GetFieldState(b).IsValidating);
        Assert.Equal((2, 1), (gates.Started("A") - aBefore, gates.Started("B") - bBefore));
    }

    // After a submit, under the default live channel, which runs the submit rules itself. A change
    // to the rendered field set empties the stored answers, so the re-check B's edit armed runs A's
    // rule again and lights B. An edit to A then cancels it, and A's live check, running the same
    // rules, answers B's re-check in its place: B stays lit while that check runs. Once it lands, B
    // is never lit again: the re-check A's edit arms lights A alone. A's rule runs once in the
    // submit, B's check, the cancelled re-check and A's check; B's runs once in each but the
    // re-check, which never reached it.
    // Mutation: hold a cancelled re-check's fields for the next re-check, as after a narrowed live
    // check, rather than handing them to a live check that runs the submit rules. B then reads
    // unlit while A's live check runs.
    [Fact]
    public async Task A_live_check_that_takes_over_a_recheck_lights_its_fields()
    {
        var gates = new FieldGates();
        var model = new Pair { B = "taken" };
        var editContext = new EditContext(model);
        var time = new FakeTimeProvider();
        var dispatch = OneAtATime();
        using var engine = new FormidableEngine<Pair>(
            model, editContext, new FluentValidationModelValidator<Pair>(new TwoAsyncRules(gates)),
            new ReflectionModelIntrospector(),
            new FormidableOptions { DisclosureOverride = _ => true },
            time,
            dispatch);
        var a = new FieldIdentifier(model, nameof(Pair.A));
        var b = new FieldIdentifier(model, nameof(Pair.B));

        gates.Open("A");
        gates.Open("B");
        Assert.False((await SubmitAsync(dispatch, engine)).CanProceed);
        await OnDispatcher(dispatch, () =>
        {
            model.B = "fine";
            editContext.NotifyFieldChanged(b);
        });
        Assert.True(await UntilAsync(dispatch, () => !engine.IsValidating));
        Assert.Equal((2, 2), (gates.Started("A"), gates.Started("B")));

        gates.Close("A");
        await OnDispatcher(dispatch, engine.OnRenderedFieldsChanged);
        await OnDispatcher(dispatch, () => time.Advance(PastRefresh));
        Assert.True(await UntilAsync(dispatch, () => gates.Started("A") == 3));
        Assert.True(engine.GetFieldState(b).IsValidating);

        await OnDispatcher(dispatch, () =>
        {
            model.A = "x";
            editContext.NotifyFieldChanged(a);
        });
        Assert.True(await UntilAsync(dispatch, () => gates.Started("A") == 4 && gates.Waiting("A") == 1));
        Assert.Equal(2, gates.Started("B"));
        Assert.True(engine.GetFieldState(a).IsValidating);
        Assert.True(engine.GetFieldState(b).IsValidating);

        var seen = new List<(bool Validating, bool BLit)>();
        engine.StateChanged += (_, _) => seen.Add((engine.IsValidating, engine.GetFieldState(b).IsValidating));
        gates.Open("A");
        Assert.True(await UntilAsync(dispatch, () => !engine.IsValidating));
        Assert.Empty(engine.GetIssues(b));
        await OnDispatcher(dispatch, () => time.Advance(PastRefresh));
        Assert.True(await UntilAsync(dispatch, () => !engine.IsValidating));

        var landed = seen.FindIndex(s => !s.Validating);
        Assert.True(landed >= 0);
        Assert.Contains(seen.Skip(landed), s => s.Validating); // the re-check A's edit armed ran
        Assert.All(seen.Skip(landed), s => Assert.False(s.BLit));
        Assert.Equal((4, 3), (gates.Started("A"), gates.Started("B")));
    }

    // A whole-form re-check that a submit cancels as it begins is restarted when the submit ends
    // unanswered, here because its caller cancelled it, and the restarted re-check lights the fields
    // the cancelled one was running for. A's rule runs once in each re-check and once in the
    // submit, and B's once, in the restarted re-check.
    // Mutation: restart the re-check without holding its fields for it. B then reads unlit while the
    // restarted re-check runs its rule.
    [Fact]
    public async Task A_recheck_restarted_after_a_cancelled_submit_lights_its_fields()
    {
        var gates = new FieldGates();
        var model = new Pair { B = "taken" };
        var editContext = new EditContext(model);
        var time = new FakeTimeProvider();
        var dispatch = OneAtATime();
        using var engine = new FormidableEngine<Pair>(
            model, editContext, new FluentValidationModelValidator<Pair>(new TwoAsyncSubmitRules(gates)),
            new ReflectionModelIntrospector(),
            new FormidableOptions { LiveProfile = ValidationProfile.Draft, DisclosureOverride = _ => true },
            time,
            dispatch);
        var b = new FieldIdentifier(model, nameof(Pair.B));

        gates.Open("A");
        gates.Open("B");
        Assert.False((await SubmitAsync(dispatch, engine)).CanProceed);
        gates.Close("A");
        gates.Close("B");
        var aBefore = gates.Started("A");
        var bBefore = gates.Started("B");

        await OnDispatcher(dispatch, () =>
        {
            model.B = "fine";
            editContext.NotifyFieldChanged(b);
        });
        Assert.True(await UntilAsync(dispatch, () => !engine.IsValidating));
        await OnDispatcher(dispatch, () => time.Advance(PastRefresh));
        Assert.True(await UntilAsync(dispatch, () => gates.Started("A") == aBefore + 1));
        Assert.True(engine.GetFieldState(b).IsValidating);

        using var cancel = new CancellationTokenSource();
        Task<SubmitOutcome>? submit = null;
        await OnDispatcher(dispatch, () => submit = engine.ValidateForSubmitAsync(cancel.Token));
        Assert.True(await UntilAsync(dispatch, () => gates.Started("A") == aBefore + 2));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => submit!);
        Assert.True(await UntilAsync(dispatch, () => !engine.IsValidating));
        Assert.False(engine.GetFieldState(b).IsValidating);

        await OnDispatcher(dispatch, () => time.Advance(PastRefresh));
        Assert.True(await UntilAsync(dispatch, () => gates.Started("A") == aBefore + 3));
        Assert.True(engine.GetFieldState(b).IsValidating);

        gates.Open("A");
        Assert.True(await UntilAsync(dispatch, () => gates.Waiting("B") == 1));
        Assert.True(engine.GetFieldState(b).IsValidating);

        gates.Open("B");
        Assert.True(await UntilAsync(dispatch, () => !engine.IsValidating));
        Assert.Empty(engine.GetIssues(b));
        Assert.False(engine.GetFieldState(b).IsValidating);
        Assert.Equal((3, 1), (gates.Started("A") - aBefore, gates.Started("B") - bBefore));
    }

    // Until a submit or a server reply has answered, nothing waits for a whole-form re-check: each
    // field one lights has a live check of its own. Here B's open LiveDebounce window lends B to a
    // re-check that a submit cancels, and the submit is cancelled too, so the re-check restarts.
    // B's window closes first and B's live check runs, with no rule to run under the narrowed live
    // channel. The restarted re-check, running B's rule for the first time, then leaves B unlit: no
    // window holds B, and nothing was held for it. A's rule runs once in each re-check and once in
    // the submit, and B's once, in the restarted re-check.
    // Mutation: hold a cancelled re-check's fields for the next one before any submit or server
    // reply has answered as well. B then reads lit while the restarted re-check runs.
    [Fact]
    public async Task Before_any_submit_a_restarted_recheck_lights_no_field_its_window_answered()
    {
        var gates = new FieldGates();
        var model = new Pair();
        var editContext = new EditContext(model);
        var time = new FakeTimeProvider();
        var dispatch = OneAtATime();
        using var engine = new FormidableEngine<Pair>(
            model, editContext, new FluentValidationModelValidator<Pair>(new TwoAsyncSubmitRules(gates)),
            new ReflectionModelIntrospector(),
            new FormidableOptions
            {
                LiveProfile = ValidationProfile.Draft,
                LiveDebounce = TimeSpan.FromMilliseconds(400),
                DisclosureOverride = _ => true,
            },
            time,
            dispatch);
        var b = new FieldIdentifier(model, nameof(Pair.B));

        // The first render arms the re-check at the default 300 ms; B's edit inside it opens a
        // 400 ms window that outlasts it.
        await OnDispatcher(dispatch, () =>
        {
            engine.OnRenderedFieldsChanged();
            model.B = "fine";
            editContext.NotifyFieldChanged(b);
        });
        await OnDispatcher(dispatch, () => time.Advance(PastRefresh));
        Assert.True(await UntilAsync(dispatch, () => gates.Started("A") == 1));
        Assert.True(engine.GetFieldState(b).IsValidating);

        using var cancel = new CancellationTokenSource();
        Task<SubmitOutcome>? submit = null;
        await OnDispatcher(dispatch, () => submit = engine.ValidateForSubmitAsync(cancel.Token));
        Assert.True(await UntilAsync(dispatch, () => gates.Started("A") == 2));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => submit!);
        Assert.True(await UntilAsync(dispatch, () => !engine.IsValidating));
        Assert.False(engine.HasSubmitted);

        await OnDispatcher(dispatch, () => time.Advance(TimeSpan.FromMilliseconds(100))); // B's window closes
        Assert.True(await UntilAsync(dispatch, () => !engine.IsValidating));
        await OnDispatcher(dispatch, () => time.Advance(TimeSpan.FromMilliseconds(200))); // the restarted re-check
        Assert.True(await UntilAsync(dispatch, () => gates.Started("A") == 3 && gates.Waiting("A") == 1));
        Assert.False(engine.GetFieldState(b).IsValidating);

        gates.Open("A");
        Assert.True(await UntilAsync(dispatch, () => gates.Waiting("B") == 1));
        Assert.True(engine.IsValidating);
        Assert.False(engine.GetFieldState(b).IsValidating);

        gates.Open("B");
        Assert.True(await UntilAsync(dispatch, () => !engine.IsValidating));
        Assert.Equal((3, 1), (gates.Started("A"), gates.Started("B")));
    }

    // The fields a cancelled re-check leaves waiting for the next one are answered by a submit, or
    // a load of values, that lands first, so the re-check after it does not light them. Here a
    // narrowed live check cancels B's re-check, and a submit or a load then runs both rules. The
    // re-check A's edit armed still runs after it and lights A, but not B. A's rule runs in the
    // cancelled re-check and the submit or load, B's in the submit or load alone, and the last
    // re-check finds both answered.
    // Mutation: keep the waiting fields through the landing of a submit (the submit row) or of a
    // load (the load row). B then reads lit as the last re-check begins.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_field_a_submit_or_load_answers_is_not_lit_by_the_next_recheck(bool load)
    {
        var gates = new FieldGates();
        var model = new Pair { B = "taken" };
        var editContext = new EditContext(model);
        var time = new FakeTimeProvider();
        var dispatch = OneAtATime();
        using var engine = new FormidableEngine<Pair>(
            model, editContext, new FluentValidationModelValidator<Pair>(new TwoAsyncSubmitRules(gates)),
            new ReflectionModelIntrospector(),
            new FormidableOptions { LiveProfile = ValidationProfile.Draft, DisclosureOverride = _ => true },
            time,
            dispatch);
        var a = new FieldIdentifier(model, nameof(Pair.A));
        var b = new FieldIdentifier(model, nameof(Pair.B));

        gates.Open("A");
        gates.Open("B");
        Assert.False((await SubmitAsync(dispatch, engine)).CanProceed);
        gates.Close("A");
        gates.Close("B");
        var aBefore = gates.Started("A");
        var bBefore = gates.Started("B");

        await OnDispatcher(dispatch, () =>
        {
            model.B = "fine";
            editContext.NotifyFieldChanged(b);
        });
        Assert.True(await UntilAsync(dispatch, () => !engine.IsValidating));
        await OnDispatcher(dispatch, () => time.Advance(PastRefresh));
        Assert.True(await UntilAsync(dispatch, () => gates.Waiting("A") == 1));
        Assert.True(engine.GetFieldState(b).IsValidating);

        await OnDispatcher(dispatch, () =>
        {
            model.A = "x";
            editContext.NotifyFieldChanged(a);
        });
        Assert.True(await UntilAsync(dispatch, () => !engine.IsValidating));

        gates.Open("A");
        gates.Open("B");
        if (load)
        {
            Task? loading = null;
            await OnDispatcher(dispatch, () => loading = engine.DiscloseLoadedValuesAsync());
            await loading!;
        }
        else
        {
            Assert.True((await SubmitAsync(dispatch, engine)).CanProceed);
        }

        var seen = new List<(bool Validating, bool ALit, bool BLit)>();
        engine.StateChanged += (_, _) => seen.Add(
            (engine.IsValidating, engine.GetFieldState(a).IsValidating, engine.GetFieldState(b).IsValidating));
        await OnDispatcher(dispatch, () => time.Advance(PastRefresh));
        Assert.True(await UntilAsync(dispatch, () => !engine.IsValidating));

        var began = seen.FindIndex(s => s.Validating);
        Assert.True(began >= 0);
        Assert.True(seen[began].ALit);
        Assert.All(seen, s => Assert.False(s.BLit));
        Assert.Equal((2, 1), (gates.Started("A") - aBefore, gates.Started("B") - bBefore));
    }

    // After a submit, under the default live channel, which runs the submit rules itself. A change
    // to the rendered field set empties the stored answers, so the re-check B's edit armed runs the
    // rules again and lights B. A submit cancels that re-check, A is edited while it runs, and the
    // submit is cancelled. B waits for the restarted re-check, but the live check given back for
    // A's edit runs at once and answers B first, so it lights B while B's rule runs in it. The
    // restarted re-check then finds B answered and lights A alone. A's rule runs in the first
    // submit, B's live check, the cancelled re-check, the cancelled submit and the given-back live
    // check; B's in the first submit, B's live check and the given-back live check.
    // Mutations: begin a live check that runs the submit rules without the waiting fields; B then
    // reads unlit while its rule runs in the given-back check. Keep the waiting fields through a
    // live check's landing; B then reads lit as the restarted re-check begins.
    [Fact]
    public async Task A_live_check_given_back_beside_a_recheck_lights_the_fields_it_answers_for_it()
    {
        var gates = new FieldGates();
        var model = new Pair();
        var editContext = new EditContext(model);
        var time = new FakeTimeProvider();
        var dispatch = OneAtATime();
        using var engine = new FormidableEngine<Pair>(
            model, editContext, new FluentValidationModelValidator<Pair>(new TwoAsyncRules(gates)),
            new ReflectionModelIntrospector(),
            new FormidableOptions { DisclosureOverride = _ => true },
            time,
            dispatch);
        var a = new FieldIdentifier(model, nameof(Pair.A));
        var b = new FieldIdentifier(model, nameof(Pair.B));

        gates.Open("A");
        gates.Open("B");
        Assert.True((await SubmitAsync(dispatch, engine)).CanProceed);
        await OnDispatcher(dispatch, () =>
        {
            model.B = "b";
            editContext.NotifyFieldChanged(b);
        });
        Assert.True(await UntilAsync(dispatch, () => !engine.IsValidating));
        Assert.Equal((2, 2), (gates.Started("A"), gates.Started("B")));

        gates.Close("A");
        gates.Close("B");
        await OnDispatcher(dispatch, engine.OnRenderedFieldsChanged);
        await OnDispatcher(dispatch, () => time.Advance(PastRefresh));
        Assert.True(await UntilAsync(dispatch, () => gates.Started("A") == 3 && gates.Waiting("A") == 1));
        Assert.True(engine.GetFieldState(b).IsValidating);

        using var cancel = new CancellationTokenSource();
        Task<SubmitOutcome>? submit = null;
        await OnDispatcher(dispatch, () => submit = engine.ValidateForSubmitAsync(cancel.Token));
        Assert.True(await UntilAsync(dispatch, () => gates.Started("A") == 4 && gates.Waiting("A") == 1));
        await OnDispatcher(dispatch, () =>
        {
            model.A = "a";
            editContext.NotifyFieldChanged(a);
        });
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => submit!);

        Assert.True(await UntilAsync(dispatch, () => gates.Started("A") == 5 && gates.Waiting("A") == 1));
        Assert.Equal(2, gates.Started("B"));
        Assert.True(engine.GetFieldState(a).IsValidating);
        Assert.True(engine.GetFieldState(b).IsValidating);

        gates.Open("A");
        Assert.True(await UntilAsync(dispatch, () => gates.Started("B") == 3 && gates.Waiting("B") == 1));
        Assert.True(engine.GetFieldState(b).IsValidating);

        gates.Open("B");
        Assert.True(await UntilAsync(dispatch, () => !engine.IsValidating));
        Assert.False(engine.GetFieldState(b).IsValidating);

        var seen = new List<(bool Validating, bool ALit, bool BLit)>();
        engine.StateChanged += (_, _) => seen.Add(
            (engine.IsValidating, engine.GetFieldState(a).IsValidating, engine.GetFieldState(b).IsValidating));
        await OnDispatcher(dispatch, () => time.Advance(PastRefresh));
        Assert.True(await UntilAsync(dispatch, () => !engine.IsValidating));

        var began = seen.FindIndex(s => s.Validating);
        Assert.True(began >= 0);
        Assert.True(seen[began].ALit);
        Assert.All(seen, s => Assert.False(s.BLit));
        Assert.Equal((5, 3), (gates.Started("A"), gates.Started("B")));
    }

    // Two fields with an async rule each. A's check runs both rules, and lights A alone while B's
    // rule runs in it. Once A's answer lands, B's check runs A's rule again and lights B alone: a
    // check that has landed hands its fields to no later check.
    // Mutation: hand a check the fields of the check before it even once that check has landed.
    // A then reads lit while B's check runs its rule.
    [Fact]
    public async Task Two_unrelated_async_fields_never_light_each_other()
    {
        var gates = new FieldGates();
        var model = new Pair();
        var editContext = new EditContext(model);
        var dispatch = OneAtATime();
        using var engine = new FormidableEngine<Pair>(
            model, editContext, new FluentValidationModelValidator<Pair>(new TwoAsyncRules(gates)),
            new ReflectionModelIntrospector(), new FormidableOptions(), new FakeTimeProvider(), dispatch);
        var a = new FieldIdentifier(model, nameof(Pair.A));
        var b = new FieldIdentifier(model, nameof(Pair.B));

        await OnDispatcher(dispatch, () =>
        {
            model.A = "a";
            editContext.NotifyFieldChanged(a);
        });
        Assert.True(await UntilAsync(dispatch, () => gates.Waiting("A") == 1));
        Assert.True(engine.GetFieldState(a).IsValidating);
        Assert.False(engine.GetFieldState(b).IsValidating);
        gates.Open("A");
        Assert.True(await UntilAsync(dispatch, () => gates.Waiting("B") == 1));
        Assert.True(engine.GetFieldState(a).IsValidating);
        Assert.False(engine.GetFieldState(b).IsValidating); // B's rule runs, in A's check
        gates.Open("B");
        Assert.True(await UntilAsync(dispatch, () => !engine.IsValidating));

        gates.Close("A");
        gates.Close("B");
        await OnDispatcher(dispatch, () =>
        {
            model.B = "b";
            editContext.NotifyFieldChanged(b);
        });
        Assert.True(await UntilAsync(dispatch, () => gates.Waiting("A") == 1));
        Assert.False(engine.GetFieldState(a).IsValidating); // A's rule runs, in B's check
        Assert.True(engine.GetFieldState(b).IsValidating);
        gates.Open("A");
        Assert.True(await UntilAsync(dispatch, () => gates.Waiting("B") == 1));
        Assert.False(engine.GetFieldState(a).IsValidating);
        Assert.True(engine.GetFieldState(b).IsValidating);
        gates.Open("B");
        Assert.True(await UntilAsync(dispatch, () => !engine.IsValidating));

        Assert.False(engine.GetFieldState(a).IsValidating);
        Assert.False(engine.GetFieldState(b).IsValidating);
        Assert.Equal((2, 2), (gates.Started("A"), gates.Started("B")));
    }

    // An edit made during each of a submit's three checks spends its restarts, and the third check
    // read an empty A, so its blocking answer lands. A's live check stood down for the submit, so it
    // is given back once the submit lands, and it runs B's held rule again: A is lit from the
    // submit's return until that check answers. The second row edits B while the given-back check
    // runs, and the newer check answers A in its place, so A stays lit until that one lands. Between
    // the landing and the give-back is one dispatch, which a renderer that queues its work can show
    // as an unlit moment; this test reads from the submit's return. B's rule runs once per check,
    // once in the given-back check and once more for the edit to B; the re-check the edits armed
    // finds every rule answered.
    // Mutation: the first mutation above. The second row then reads A unlit once B is edited.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_field_edited_during_a_submit_stays_checking_until_its_given_back_check_answers(bool editB)
    {
        var gates = new FieldGates();
        var model = new Pair();
        var editContext = new EditContext(model);
        var time = new FakeTimeProvider();
        var dispatch = OneAtATime();
        using var engine = new FormidableEngine<Pair>(
            model, editContext, new FluentValidationModelValidator<Pair>(new RequiredAWithHeldB(gates)),
            new ReflectionModelIntrospector(),
            new FormidableOptions { DisclosureOverride = _ => true },
            time,
            dispatch);
        var a = new FieldIdentifier(model, nameof(Pair.A));
        var b = new FieldIdentifier(model, nameof(Pair.B));

        gates.Open("B");
        await OnDispatcher(dispatch, () =>
        {
            model.A = "Bo";
            editContext.NotifyFieldChanged(a);
        });
        Assert.True(await UntilAsync(dispatch, () => !engine.IsValidating));
        gates.Close("B");
        var before = gates.Started("B");

        Task<SubmitOutcome>? submit = null;
        await OnDispatcher(dispatch, () => submit = engine.ValidateForSubmitAsync());
        string[] edits = ["Ann", string.Empty, "Cy"]; // the third check reads "" and blocks
        for (var made = 0; made < edits.Length; made++)
        {
            Assert.True(await UntilAsync(dispatch, () => gates.Started("B") == before + made + 1 && gates.Waiting("B") == 1));
            var value = edits[made];
            await OnDispatcher(dispatch, () =>
            {
                model.A = value;
                editContext.NotifyFieldChanged(a);
            });
            gates.ReleaseWaiting("B");
        }

        Assert.False((await submit!).CanProceed);
        Assert.True(await UntilAsync(dispatch, () => gates.Waiting("B") == 1));
        Assert.Equal(4, gates.Started("B") - before);
        Assert.True(engine.GetFieldState(a).IsValidating);

        var seen = new List<(bool Lit, bool Validating)>();
        engine.StateChanged += (_, _) => seen.Add((engine.GetFieldState(a).IsValidating, engine.IsValidating));
        if (editB)
        {
            await OnDispatcher(dispatch, () =>
            {
                model.B = "fine";
                editContext.NotifyFieldChanged(b);
            });
            Assert.True(await UntilAsync(dispatch, () => gates.Started("B") == before + 5 && gates.Waiting("B") == 1));
            Assert.True(engine.GetFieldState(a).IsValidating);
        }

        gates.Open("B");
        Assert.True(await UntilAsync(dispatch, () => !engine.IsValidating));
        var landed = seen.FindIndex(s => !s.Validating);
        Assert.True(landed >= 0);
        Assert.All(seen.Take(landed), s => Assert.True(s.Lit));
        Assert.False(engine.GetFieldState(a).IsValidating);
        Assert.Empty(engine.GetIssues(a));

        await OnDispatcher(dispatch, () => time.Advance(PastRefresh));
        Assert.True(await UntilAsync(dispatch, () => !engine.IsValidating));
        Assert.Equal(editB ? 5 : 4, gates.Started("B") - before);
    }

    // A submit whose answer an edit made while it ran has outdated checks again as the same pass.
    // The form-wide flag stays on from the first check to the answer of the one it restarts, read
    // at every notification, including the one the edit's first touch of B raises while the first
    // check runs. It falls once, as that answer lands. B's rule runs once per check, and the re-check the edit
    // armed finds every rule answered.
    // Mutation: end the pass and begin it again to check again. The flag then falls between the two.
    [Fact]
    public async Task The_form_stays_checking_across_a_restarted_submit()
    {
        var gates = new FieldGates();
        var model = new Pair();
        var editContext = new EditContext(model);
        var time = new FakeTimeProvider();
        var dispatch = OneAtATime();
        using var engine = new FormidableEngine<Pair>(
            model, editContext, new FluentValidationModelValidator<Pair>(new RequiredAWithHeldB(gates)),
            new ReflectionModelIntrospector(),
            new FormidableOptions { DisclosureOverride = _ => true },
            time,
            dispatch);
        var a = new FieldIdentifier(model, nameof(Pair.A));
        var b = new FieldIdentifier(model, nameof(Pair.B));

        gates.Open("B");
        await OnDispatcher(dispatch, () =>
        {
            model.A = "Bo";
            editContext.NotifyFieldChanged(a);
        });
        Assert.True(await UntilAsync(dispatch, () => !engine.IsValidating));
        gates.Close("B");
        var before = gates.Started("B");

        var seen = new List<bool>();
        engine.StateChanged += (_, _) => seen.Add(engine.IsValidating);
        Task<SubmitOutcome>? submit = null;
        await OnDispatcher(dispatch, () => submit = engine.ValidateForSubmitAsync());
        Assert.True(await UntilAsync(dispatch, () => gates.Waiting("B") == 1));
        await OnDispatcher(dispatch, () =>
        {
            model.B = "fine";
            editContext.NotifyFieldChanged(b);
        });
        gates.ReleaseWaiting("B");
        Assert.True(await UntilAsync(dispatch, () => gates.Started("B") == before + 2 && gates.Waiting("B") == 1));

        Assert.True(engine.IsValidating);
        Assert.True(engine.GetFieldState(a).IsValidating);
        Assert.True(seen.Count >= 2); // the submit's begin, and B's first touch
        Assert.All(seen, Assert.True);

        gates.Open("B");
        Assert.True((await submit!).CanProceed);
        Assert.False(engine.IsValidating);
        var fell = seen.IndexOf(false);
        Assert.True(fell >= 2);
        Assert.All(seen.Skip(fell), Assert.False);

        await OnDispatcher(dispatch, () => time.Advance(PastRefresh));
        Assert.True(await UntilAsync(dispatch, () => !engine.IsValidating));
        Assert.Equal(2, gates.Started("B") - before);
    }

    private static readonly TimeSpan PastRefresh = new FormidableOptions().RefreshDebounce + TimeSpan.FromMilliseconds(1);

    private const string ATaken = "A taken";
    private const string BTaken = "B taken";
    private const string ARequired = "A is required";

    /// <summary>Runs <paramref name="work"/> on the engine's dispatch, as the renderer runs a page's event.</summary>
    private static Task OnDispatcher(Func<Func<Task>, Task> dispatch, Action work) =>
        dispatch(() =>
        {
            work();
            return Task.CompletedTask;
        });

    /// <summary>Starts a submit on the engine's dispatch and waits for its outcome.</summary>
    private static async Task<SubmitOutcome> SubmitAsync(Func<Func<Task>, Task> dispatch, FormidableEngine<Pair> engine)
    {
        Task<SubmitOutcome>? submit = null;
        await OnDispatcher(dispatch, () => submit = engine.ValidateForSubmitAsync());
        return await submit!;
    }

    /// <summary>Yields until <paramref name="condition"/> holds, read on the engine's dispatch so it never sees a dispatch half done, for at most five seconds.</summary>
    /// <returns>Whether the condition held.</returns>
    private static async Task<bool> UntilAsync(Func<Func<Task>, Task> dispatch, Func<bool> condition)
    {
        for (var waited = 0; waited < 500; waited++)
        {
            var holds = false;
            await OnDispatcher(dispatch, () => holds = condition());
            if (holds)
            {
                return true;
            }

            await Task.Delay(10);
        }

        return false;
    }

    /// <summary>Two string fields, each the subject of the rules a test's validator gives it.</summary>
    public sealed class Pair
    {
        public string? A { get; set; } = string.Empty;

        public string? B { get; set; } = string.Empty;
    }

    /// <summary>One gate per field's async rule, with the runs each rule has started and the runs still waiting on its gate. A value starting "taken" fails.</summary>
    private sealed class FieldGates
    {
        private readonly object _sync = new();
        private readonly Dictionary<string, TaskCompletionSource> _gates = new() { ["A"] = NewGate(), ["B"] = NewGate() };
        private readonly Dictionary<string, int> _started = new() { ["A"] = 0, ["B"] = 0 };
        private readonly Dictionary<string, int> _waiting = new() { ["A"] = 0, ["B"] = 0 };

        public int Started(string rule)
        {
            lock (_sync)
            {
                return _started[rule];
            }
        }

        public int Waiting(string rule)
        {
            lock (_sync)
            {
                return _waiting[rule];
            }
        }

        /// <summary>Lets every waiting run of the rule, and every later one, answer.</summary>
        public void Open(string rule)
        {
            lock (_sync)
            {
                _gates[rule].TrySetResult();
            }
        }

        /// <summary>Holds every later run of the rule.</summary>
        public void Close(string rule)
        {
            lock (_sync)
            {
                _gates[rule] = NewGate();
            }
        }

        /// <summary>Lets the runs of the rule waiting now answer, and holds every later one.</summary>
        public void ReleaseWaiting(string rule)
        {
            TaskCompletionSource waiting;
            lock (_sync)
            {
                waiting = _gates[rule];
                _gates[rule] = NewGate();
            }

            waiting.TrySetResult();
        }

        public async Task<bool> RunAsync(string rule, string? value, CancellationToken ct)
        {
            Task gate;
            lock (_sync)
            {
                _started[rule]++;
                _waiting[rule]++;
                gate = _gates[rule].Task;
            }

            try
            {
                await gate.WaitAsync(ct);
                return value?.StartsWith("taken", StringComparison.Ordinal) != true;
            }
            finally
            {
                lock (_sync)
                {
                    _waiting[rule]--;
                }
            }
        }

        private static TaskCompletionSource NewGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>An async rule on A, held on its gate, and a synchronous rule on B, in every profile.</summary>
    private sealed class OneAsyncRule : AbstractValidator<Pair>
    {
        public OneAsyncRule(FieldGates gates)
        {
            RuleFor(m => m.A).MustAsync((value, ct) => gates.RunAsync("A", value, ct)).WithMessage(ATaken);
            RuleFor(m => m.B).MaximumLength(20);
        }
    }

    /// <summary>An async rule on each field, each held on its own gate, in every profile.</summary>
    private sealed class TwoAsyncRules : AbstractValidator<Pair>
    {
        public TwoAsyncRules(FieldGates gates)
        {
            RuleFor(m => m.A).MustAsync((value, ct) => gates.RunAsync("A", value, ct)).WithMessage(ATaken);
            RuleFor(m => m.B).MustAsync((value, ct) => gates.RunAsync("B", value, ct)).WithMessage(BTaken);
        }
    }

    /// <summary>The same two async rules in the submit bucket alone, so a live channel narrowed to the draft bucket runs neither.</summary>
    private sealed class TwoAsyncSubmitRules(FieldGates gates) : DraftSubmitValidator<Pair>
    {
        protected override void ConfigureDraftRules()
        {
        }

        protected override void ConfigureSubmitRules()
        {
            RuleFor(m => m.A).MustAsync((value, ct) => gates.RunAsync("A", value, ct)).WithMessage(ATaken);
            RuleFor(m => m.B).MustAsync((value, ct) => gates.RunAsync("B", value, ct)).WithMessage(BTaken);
        }
    }

    /// <summary>A must hold more than white space, in the draft bucket; B's async rule, held on its gate, is in the submit bucket, so a submit reads A before it waits.</summary>
    private sealed class RequiredAWithHeldB(FieldGates gates) : DraftSubmitValidator<Pair>
    {
        protected override void ConfigureDraftRules() =>
            RuleFor(m => m.A).Must(value => !string.IsNullOrWhiteSpace(value)).WithMessage(ARequired);

        protected override void ConfigureSubmitRules() =>
            RuleFor(m => m.B).MustAsync((value, ct) => gates.RunAsync("B", value, ct)).WithMessage(BTaken);
    }

    /// <summary>An async rule on <see cref="EngineOrder.Description"/> in the always-on bucket that skips an empty value, the shape of a uniqueness check, held on <see cref="Gate"/>.</summary>
    private sealed class FilledFieldGatedValidator : DraftSubmitValidator<EngineOrder>
    {
        public TaskCompletionSource Gate { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Started;

        public void Reset() => Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override void ConfigureDraftRules() =>
            RuleFor(x => x.Description)
                .MustAsync(async (_, ct) =>
                {
                    Interlocked.Increment(ref Started);
                    await Gate.Task.WaitAsync(ct);
                    return true;
                })
                .When(x => !string.IsNullOrEmpty(x.Description));

        protected override void ConfigureSubmitRules()
        {
        }
    }
}
