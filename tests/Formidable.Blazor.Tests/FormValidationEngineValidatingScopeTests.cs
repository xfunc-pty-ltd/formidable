using Formidable.Blazor.Tests.Fixtures;
using Formidable.Introspection;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Time.Testing;

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
        // Two independent async draft rules, as the sibling-field live-pass test above uses.
        // The refresh pass revalidates the whole model under SubmitProfile, which (via
        // ValidationProfile.Submit's IncludeDefaultRules) still runs both of these unnamed rules.
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

        // Notify a change for field B only, then advance the fake clock past the 300ms debounce
        // so the refresh pass starts and (via the gate) sits in flight.
        editContext.NotifyFieldChanged(fieldB);
        time.Advance(TimeSpan.FromMilliseconds(301));

        Assert.True(engine.GetFieldState(fieldB).IsValidating);  // edited in the window: scoped flag set
        Assert.False(engine.GetFieldState(fieldA).IsValidating); // untouched: scope excludes it
        Assert.True(engine.IsValidating);                        // engine-level flag stays form-wide

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

        // Notify changes for BOTH fields before the debounce elapses.
        editContext.NotifyFieldChanged(fieldA);
        editContext.NotifyFieldChanged(fieldB);
        time.Advance(TimeSpan.FromMilliseconds(301));

        Assert.True(engine.GetFieldState(fieldA).IsValidating); // both edited in the window
        Assert.True(engine.GetFieldState(fieldB).IsValidating);
        Assert.True(engine.IsValidating);

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

        // Edit B; let its refresh (#1) start and sit in flight, scoped to {B}.
        editContext.NotifyFieldChanged(fieldB);
        time.Advance(TimeSpan.FromMilliseconds(301));
        Assert.True(engine.GetFieldState(fieldB).IsValidating); // refresh #1 in flight, scoped to B

        // Edit A mid-flight: supersedes refresh #1 (live passes never defer to refreshes),
        // takes the pending indicator with it, and re-arms the debounce for a second refresh.
        editContext.NotifyFieldChanged(fieldA);
        time.Advance(TimeSpan.FromMilliseconds(301));

        Assert.True(engine.GetFieldState(fieldA).IsValidating);  // refresh #2: scoped to the new edit
        Assert.False(engine.GetFieldState(fieldB).IsValidating); // B's scope was dropped by the supersession
        Assert.True(engine.IsValidating);                        // engine-level flag stays form-wide

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
}
