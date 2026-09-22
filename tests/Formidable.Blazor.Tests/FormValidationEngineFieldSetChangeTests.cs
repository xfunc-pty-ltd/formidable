using Formidable.Blazor.Tests.Fixtures;
using Formidable.Introspection;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Time.Testing;
using static Formidable.Blazor.Tests.Fixtures.EngineTestSync;

namespace Formidable.Blazor.Tests;

/// <summary>
/// A rendered field set that moves — a collection row removed, a section collapsed — announces
/// itself as nothing at all: the model may be mutated without a field change, and the registry
/// deliberately raises no event. These pin what the engine does once the host tells it, and
/// equally what it declines to do.
/// </summary>
/// <remarks>
/// The refresh assertions are carried by rule-execution counters rather than by verdicts. A
/// refresh that never ran and a refresh that ran and changed nothing leave identical state, and
/// the invalidation in particular is only visible as WHICH rules the next refresh executes — a
/// reused report and a recomputed one agree about the verdict whenever the model has not moved,
/// which is exactly the case that would make an assertion on state pass for the wrong reason.
/// The unsubmitted form is the sharpest instance: neither reveal ledger names a field there, so
/// its refresh changes nothing any surface shows and the counters are the whole of the evidence.
/// </remarks>
public class FormValidationEngineFieldSetChangeTests
{
    private const int PastRefreshWindow = 301;

    [Fact]
    public void A_field_that_left_the_page_loses_its_live_issue()
    {
        var customer = new EngineCustomer { Name = "Bo" };
        var order = new EngineOrder { Customer = customer };
        var validator = new RuleRunCountingValidator();
        var editContext = new EditContext(order);
        using var engine = Build(order, editContext, validator, new FormidableOptions());

        var name = new FieldIdentifier(customer, nameof(EngineCustomer.Name));
        var registration = engine.Registry.Register(name);

        customer.Name = "far too long";
        editContext.NotifyFieldChanged(name);

        Assert.Contains(engine.GetVisibleIssues(), v => v.Issue.Message == RuleRunCountingValidator.DraftMessage);

        // The customer is still on the model — only its field's registration is gone, which is
        // the whole difference between a row that left the page and one that is merely not being
        // looked at.
        registration.Dispose();
        engine.OnRenderedFieldsChanged();

        Assert.DoesNotContain(engine.GetVisibleIssues(), v => v.Issue.Message == RuleRunCountingValidator.DraftMessage);
    }

    [Fact]
    public void A_field_that_left_the_page_loses_its_store_message()
    {
        var customer = new EngineCustomer { Name = "Bo" };
        var order = new EngineOrder { Customer = customer };
        var validator = new RuleRunCountingValidator();
        var editContext = new EditContext(order);
        using var engine = Build(order, editContext, validator, new FormidableOptions());

        var name = new FieldIdentifier(customer, nameof(EngineCustomer.Name));
        var registration = engine.Registry.Register(name);

        customer.Name = "far too long";
        editContext.NotifyFieldChanged(name);

        // The message store is the second place the verdict lives, and the one the platform's own
        // ValidationMessage and ValidationSummary render from. An engine read that has dropped the
        // issue while this still offers it is two answers to one question.
        Assert.Contains(RuleRunCountingValidator.DraftMessage, editContext.GetValidationMessages());

        registration.Dispose();
        engine.OnRenderedFieldsChanged();

        Assert.DoesNotContain(RuleRunCountingValidator.DraftMessage, editContext.GetValidationMessages());
    }

    [Fact]
    public void A_field_set_change_that_prunes_nothing_publishes_nothing()
    {
        var customer = new EngineCustomer { Name = "Bo" };
        var order = new EngineOrder { Customer = customer };
        var validator = new RuleRunCountingValidator();
        var editContext = new EditContext(order);
        using var engine = Build(order, editContext, validator, new FormidableOptions());

        var name = new FieldIdentifier(customer, nameof(EngineCustomer.Name));
        using var registration = engine.Registry.Register(name);

        customer.Name = "far too long";
        editContext.NotifyFieldChanged(name);

        var notifications = 0;
        var validationStateChanges = 0;
        engine.StateChanged += (_, _) => notifications++;
        editContext.OnValidationStateChanged += (_, _) => validationStateChanges++;

        // The field is still registered, so nothing leaves and there is nothing to republish. This
        // is the case a page whose rows churn as it scrolls spends all its time in, and a render
        // round per registration change is what it must not cost. The refresh the change arms is
        // a debounced matter of its own: it collapses a whole burst of these into one pass, and
        // no time is advanced here for it to fire in.
        engine.OnRenderedFieldsChanged();

        Assert.Equal(0, notifications);
        Assert.Equal(0, validationStateChanges);
    }

    [Fact]
    public void A_kept_registered_field_keeps_its_live_issue()
    {
        var customer = new EngineCustomer { Name = "Bo" };
        var order = new EngineOrder { Customer = customer };
        var validator = new RuleRunCountingValidator();
        var editContext = new EditContext(order);
        using var engine = Build(order, editContext, validator, new FormidableOptions());

        var name = new FieldIdentifier(customer, nameof(EngineCustomer.Name));
        var registration = engine.Registry.Register(name, keepRegistered: true);

        customer.Name = "far too long";
        editContext.NotifyFieldChanged(name);

        Assert.Contains(engine.GetVisibleIssues(), v => v.Issue.Message == RuleRunCountingValidator.DraftMessage);

        // What a virtualized row's disposal means: the element is gone from the DOM, the field has
        // not left the form. Its messages have to survive being scrolled past.
        registration.Dispose();
        engine.OnRenderedFieldsChanged();

        Assert.Contains(engine.GetVisibleIssues(), v => v.Issue.Message == RuleRunCountingValidator.DraftMessage);
    }

    [Fact]
    public async Task A_live_pass_in_flight_does_not_answer_for_a_field_that_left_the_page()
    {
        // A collapsed section rather than a removed row: the customer is still on the model, so
        // the draft rule the field fails goes on failing, and the verdict the pass in flight is
        // about to write is a real issue rather than an empty entry.
        var customer = new EngineCustomer { Name = "far too long" };
        var order = new EngineOrder { Customer = customer };
        var validator = new GatedRuleRunCountingValidator();
        var editContext = new EditContext(order);
        using var engine = new FormValidationEngine<EngineOrder>(
            order,
            editContext,
            new FluentValidationModelValidator<EngineOrder>(validator),
            new ReflectionModelIntrospector(),
            new FormidableOptions(),
            new FakeTimeProvider());

        var name = new FieldIdentifier(customer, nameof(EngineCustomer.Name));
        var registration = engine.Registry.Register(name);

        // A first edit, settled, so the field holds an issue the prune has to find — the state the
        // pass below would be putting back rather than creating for the first time.
        editContext.NotifyFieldChanged(name);
        var first = Quiescence(engine);
        validator.Gate.SetResult();
        await first;

        Assert.Contains(engine.GetVisibleIssues(), v => v.Issue.Message == RuleRunCountingValidator.DraftMessage);

        // A second edit, held open on the gate: this is the pass that is still in flight when the
        // section closes.
        validator.Reset();
        editContext.NotifyFieldChanged(name);

        registration.Dispose();
        engine.OnRenderedFieldsChanged();

        Assert.DoesNotContain(engine.GetVisibleIssues(), v => v.Issue.Message == RuleRunCountingValidator.DraftMessage);

        var settled = Quiescence(engine);
        validator.Gate.SetResult();
        await settled;

        // The verdict arrives for a field the prune already disengaged: the pass intersects its
        // pass-begin engaged snapshot with the set as it stands at apply time, so no entry lands.
        // The live view reads through the engaged set, so a written entry could not surface
        // anyway - the intersect is what keeps the source from accumulating verdicts nothing
        // can read.
        Assert.DoesNotContain(engine.GetVisibleIssues(), v => v.Issue.Message == RuleRunCountingValidator.DraftMessage);
    }

    [Fact]
    public async Task A_field_set_change_after_a_submit_schedules_a_refresh()
    {
        var customer = new EngineCustomer { Name = "far too long" };
        var order = new EngineOrder { Customer = customer };
        var validator = new RuleRunCountingValidator();
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        using var engine = Build(
            order, editContext, validator, new FormidableOptions { DisclosureOverride = _ => true }, time);

        // Both buckets fail, so the submit blocks and leaves error sites a refresh keeps current.
        Assert.False((await engine.ValidateForSubmitAsync()).CanProceed);

        var draftBefore = validator.DraftRuleRuns;
        var submitBefore = validator.SubmitRuleRuns;

        engine.OnRenderedFieldsChanged();
        time.Advance(TimeSpan.FromMilliseconds(PastRefreshWindow));

        // No edit armed anything here — the field-set change is the only thing that could have.
        Assert.Equal(draftBefore + 1, validator.DraftRuleRuns);
        Assert.Equal(submitBefore + 1, validator.SubmitRuleRuns);
    }

    [Fact]
    public async Task A_field_set_change_with_no_live_pass_rebuilds_the_submit_projection_on_its_own()
    {
        // Nothing here ever calls NotifyFieldChanged, so HandleFieldChanged never runs and no
        // live pass ever answers for the fix below — the field-set change is the only thing
        // that arms anything, which is what isolates the refresh's own rebuild from the live
        // channel's.
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
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));
        using var descReg = engine.Registry.Register(description);

        Assert.False((await engine.ValidateForSubmitAsync()).CanProceed);
        Assert.NotEmpty(editContext.GetValidationMessages(description));

        // Fixed directly on the model, with no committed change behind it.
        order.Description = "ok";
        engine.OnRenderedFieldsChanged();
        time.Advance(TimeSpan.FromMilliseconds(PastRefreshWindow));

        Assert.Empty(editContext.GetValidationMessages(description));
    }

    [Fact]
    public async Task A_reconciliation_armed_refresh_lights_no_pending_indicator()
    {
        // A field-set change is the one arm site that can start a refresh with NOTHING in
        // _pendingRefreshFields - no edit ever put a field there. Held open on a gated draft
        // rule (the refresh re-executes the whole submit selection, since the field-set change
        // cleared the verdict store), this pins that an empty accumulator reads as an empty
        // scope: no field validating, ever, across the pass's whole lifetime.
        var customer = new EngineCustomer { Name = "far too long" };
        var order = new EngineOrder { Customer = customer };
        var validator = new GatedRuleRunCountingValidator();
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        using var engine = new FormValidationEngine<EngineOrder>(
            order,
            editContext,
            new FluentValidationModelValidator<EngineOrder>(validator),
            new ReflectionModelIntrospector(),
            new FormidableOptions(),
            time);

        var name = new FieldIdentifier(customer, nameof(EngineCustomer.Name));
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));

        // Submit once, released, so the form has disclosed a verdict for the refresh below to
        // reconcile - the case in which that refresh has something to say, and therefore the one
        // in which lighting the wrong fields pending would be seen.
        var submit = engine.ValidateForSubmitAsync();
        validator.Gate.SetResult();
        await submit;
        validator.Reset();

        // Subscribed before the refresh fires, so a transient flip mid-window cannot be missed by
        // only sampling before and after.
        var everValidating = false;
        engine.StateChanged += (_, _) =>
            everValidating |= engine.GetFieldState(name).IsValidating
                || engine.GetFieldState(description).IsValidating;

        // No field was ever edited - only the rendered field set moved.
        engine.OnRenderedFieldsChanged();
        time.Advance(TimeSpan.FromMilliseconds(PastRefreshWindow)); // fires the refresh; held open on the gate

        Assert.True(engine.IsValidating);                           // the refresh pass, in flight
        Assert.False(engine.GetFieldState(name).IsValidating);       // no edit armed this refresh: empty scope
        Assert.False(engine.GetFieldState(description).IsValidating);

        var settled = Quiescence(engine);
        validator.Gate.SetResult();
        await settled;

        Assert.False(everValidating); // no field ever lit, across the whole window
        Assert.False(engine.GetFieldState(name).IsValidating);
        Assert.False(engine.GetFieldState(description).IsValidating);
    }

    [Fact]
    public void A_field_set_change_before_any_submit_re_answers_in_silence()
    {
        // Both rules fail, and neither field is ever edited: what the refresh finds is exactly
        // what nothing on the page is entitled to show.
        var customer = new EngineCustomer { Name = "far too long" };
        var order = new EngineOrder { Customer = customer };
        var validator = new RuleRunCountingValidator();
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        using var engine = Build(order, editContext, validator, new FormidableOptions(), time);

        var name = new FieldIdentifier(customer, nameof(EngineCustomer.Name));
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));
        using var nameRegistration = engine.Registry.Register(name);
        using var descriptionRegistration = engine.Registry.Register(description);

        Assert.Equal(0, validator.DraftRuleRuns);
        Assert.Equal(0, validator.SubmitRuleRuns);

        // Subscribed before the refresh fires: a pending class that lit only while the pass was
        // in flight would be gone again by the time the advance returns.
        var everPending = false;
        engine.StateChanged += (_, _) =>
            everPending |= editContext.FieldCssClass(name).Contains("formidable-pending")
                || editContext.FieldCssClass(description).Contains("formidable-pending");

        engine.OnRenderedFieldsChanged();
        time.Advance(TimeSpan.FromMilliseconds(PastRefreshWindow));

        // The rules ran, which is the only way to tell a refresh that happened from one that did
        // not: an unsubmitted form's state reads the same either way.
        Assert.Equal(1, validator.DraftRuleRuns);
        Assert.Equal(1, validator.SubmitRuleRuns);

        // And it said nothing. Nothing is engaged and neither reveal ledger names a field, so the
        // two failures reach no surface at all; the empty edited set the field-set change armed it
        // with scopes the pending indicator to nothing, so no input flashes on the way past.
        Assert.Empty(engine.GetVisibleIssues());
        Assert.Empty(engine.GetIssues(name));
        Assert.Empty(engine.GetIssues(description));
        Assert.Empty(editContext.GetValidationMessages());
        Assert.False(everPending);
        Assert.Equal(string.Empty, editContext.FieldCssClass(name));
        Assert.Equal(string.Empty, editContext.FieldCssClass(description));
    }

    [Fact]
    public async Task A_field_set_change_clears_the_verdict_store()
    {
        var customer = new EngineCustomer { Name = "far too long" };
        var order = new EngineOrder { Customer = customer };
        var validator = new RuleRunCountingValidator();
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        using var engine = Build(
            order, editContext, validator, new FormidableOptions { DisclosureOverride = _ => true }, time);

        Assert.False((await engine.ValidateForSubmitAsync()).CanProceed);

        // This edit's live pass stores the draft rule's verdict at the current edit count; the
        // refresh the edit also armed would otherwise serve it from the store in place of
        // running the rule again.
        customer.Name = "longer still";
        editContext.NotifyFieldChanged(new FieldIdentifier(customer, nameof(EngineCustomer.Name)));

        var draftBefore = validator.DraftRuleRuns;

        // A field-set change moves no edit counter, so the stored verdict still reads as fresh.
        // Only the store being cleared outright can stop the refresh below reusing a verdict
        // that was computed against the page — and the model — as they stood before the move.
        engine.OnRenderedFieldsChanged();
        time.Advance(TimeSpan.FromMilliseconds(PastRefreshWindow));

        Assert.Equal(draftBefore + 1, validator.DraftRuleRuns);
    }

    [Fact]
    public void A_field_departing_during_the_window_gets_no_live_issue()
    {
        var customer = new EngineCustomer { Name = "far too long" };
        var order = new EngineOrder { Customer = customer };
        var validator = new RuleRunCountingValidator();
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        using var engine = Build(
            order, editContext, validator, new FormidableOptions { LiveDebounce = TimeSpan.FromMilliseconds(400) }, time);

        var name = new FieldIdentifier(customer, nameof(EngineCustomer.Name));
        var registration = engine.Registry.Register(name);

        // Accumulates into the open debounce window rather than running anything yet.
        editContext.NotifyFieldChanged(name);

        // The field the window opened for leaves the page before the window closes.
        registration.Dispose();
        engine.OnRenderedFieldsChanged();

        time.Advance(TimeSpan.FromMilliseconds(400)); // window closes

        Assert.DoesNotContain(engine.GetVisibleIssues(), v => v.Issue.Message == RuleRunCountingValidator.DraftMessage);
    }

    [Fact]
    public void An_all_departed_window_runs_no_pass()
    {
        var customer = new EngineCustomer { Name = "far too long" };
        var order = new EngineOrder { Customer = customer };
        var validator = new RuleRunCountingValidator();
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();

        // The field-set change below arms a refresh of its own. Held well past the live window,
        // so the advance that closes that window fires it and nothing else: the pass this is
        // about is the one the window would have started.
        using var engine = Build(
            order,
            editContext,
            validator,
            new FormidableOptions
            {
                LiveDebounce = TimeSpan.FromMilliseconds(400),
                RefreshDebounce = TimeSpan.FromSeconds(30),
            },
            time);

        var name = new FieldIdentifier(customer, nameof(EngineCustomer.Name));
        var registration = engine.Registry.Register(name);

        editContext.NotifyFieldChanged(name);
        Assert.Equal(0, validator.DraftRuleRuns);

        // The only field the window accumulated leaves before it closes, so the snapshot the
        // timer takes is empty.
        registration.Dispose();
        engine.OnRenderedFieldsChanged();

        // A pass with an empty scope still flips IsValidating for an instant before it ends, even
        // though no field ever reads as validating under it — the transient flip is what a
        // pass-that-should-never-have-started leaves behind, and a post-hoc read after the
        // (synchronous) fire would already have missed it.
        var everValidating = false;
        engine.StateChanged += (_, _) => everValidating |= engine.IsValidating || engine.GetFieldState(name).IsValidating;

        time.Advance(TimeSpan.FromMilliseconds(400)); // window closes into an empty snapshot

        Assert.Equal(0, validator.DraftRuleRuns);
        Assert.False(everValidating);
        Assert.False(engine.IsValidating);
    }

    [Fact]
    public async Task Survivors_keep_the_window()
    {
        var customer = new EngineCustomer { Name = "far too long" };
        var order = new EngineOrder { Customer = customer, Items = [new EngineItem { Sku = "far too long" }] };
        var validator = new SlowLiveRuleValidator();
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        using var engine = new FormValidationEngine<EngineOrder>(
            order,
            editContext,
            new FluentValidationModelValidator<EngineOrder>(validator),
            new ReflectionModelIntrospector(),
            // The departure below arms a refresh as well, and a refresh in flight is one the
            // window's own fire stands down for and re-arms behind. Held past the window, so what
            // answers for the survivor here is the window's pass rather than a pass that would
            // have answered for every field alike.
            new FormidableOptions
            {
                LiveDebounce = TimeSpan.FromMilliseconds(400),
                RefreshDebounce = TimeSpan.FromSeconds(30),
            },
            time);

        var customerNameField = new FieldIdentifier(customer, nameof(EngineCustomer.Name));
        var skuField = new FieldIdentifier(order.Items[0], nameof(EngineItem.Sku));

        var customerRegistration = engine.Registry.Register(customerNameField);
        using var skuRegistration = engine.Registry.Register(skuField);

        editContext.NotifyFieldChanged(customerNameField); // opens the window
        editContext.NotifyFieldChanged(skuField); // accumulates into the same window

        // The customer field departs before the window closes; the SKU field stays registered.
        customerRegistration.Dispose();
        engine.OnRenderedFieldsChanged();

        time.Advance(TimeSpan.FromMilliseconds(400)); // window closes; held open on Description's gate

        // The survivor is what the pass answers for: the departed field was pruned from the
        // window's accumulator and the engaged set alike, so it is neither in the pending scope
        // nor in the verdict the pass applies.
        Assert.True(engine.GetFieldState(skuField).IsValidating);
        Assert.False(engine.GetFieldState(customerNameField).IsValidating);

        var quiescent = Quiescence(engine);
        validator.Gate.SetResult();
        await quiescent;

        Assert.DoesNotContain(engine.GetVisibleIssues(), v => v.Issue.Message == "Customer name is too long");
        Assert.Contains(engine.GetVisibleIssues(), v => v.Issue.Message == "SKU is too long");
    }

    private static FormValidationEngine<EngineOrder> Build(
        EngineOrder order,
        EditContext editContext,
        RuleRunCountingValidator validator,
        FormidableOptions options,
        TimeProvider? time = null) =>
        new(
            order,
            editContext,
            new FluentValidationModelValidator<EngineOrder>(validator),
            new ReflectionModelIntrospector(),
            options,
            time ?? new FakeTimeProvider());
}
