using Formidable.Blazor.Tests.Fixtures;
using Formidable.Introspection;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Time.Testing;
using static Formidable.Blazor.Tests.Fixtures.EngineTestSync;

namespace Formidable.Blazor.Tests;

/// <summary>
/// The submit profile includes the live profile's rules, so a post-submit edit arms two passes
/// that overlap on every rule they share. These pin that the overlap is paid once: each pass
/// executes only the rules with no fresh verdict at its own edit stamp and assembles the rest
/// from the per-rule store, so one edit costs one execution per rule whichever pass gets there
/// first — and a validator without the rule-level capability still reaches the same verdicts by
/// running whole profiles.
/// </summary>
/// <remarks>
/// The counters are what carry these tests: two runs of a rule leave exactly what one run leaves,
/// so a verdict assertion alone cannot tell them apart. Where a verdict IS asserted, it is
/// asserted against the whole-profile fallback's own verdict for the same sequence, so the two
/// have to agree about issues rather than merely about how many there are.
/// </remarks>
public class FormValidationEngineProfileSplitTests
{
    [Fact]
    public async Task A_post_submit_edit_runs_each_draft_rule_once()
    {
        var customer = new EngineCustomer { Name = "Bo" };
        var order = new EngineOrder { Customer = customer };
        var validator = new RuleRunCountingValidator();
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        using var engine = new FormValidationEngine<EngineOrder>(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(validator),
            new ReflectionModelIntrospector(),
            new FormidableOptions { DisclosureOverride = _ => true },
            time);

        // The submit blocks on the empty description, which makes it an error site the refresh
        // below keeps current.
        Assert.False((await engine.ValidateForSubmitAsync()).CanProceed);

        var draftBefore = validator.DraftRuleRuns;
        var submitBefore = validator.SubmitRuleRuns;

        order.Description = "Quarterly refresh";
        editContext.NotifyFieldChanged(new FieldIdentifier(order, nameof(EngineOrder.Description)));
        time.Advance(TimeSpan.FromMilliseconds(301));

        // One edit, one execution of each rule: the live pass ran the draft rule, and the
        // refresh that followed served its verdict from the store and executed only the submit
        // rule, which had no verdict at the edit's stamp.
        Assert.Equal(draftBefore + 1, validator.DraftRuleRuns);
        Assert.Equal(submitBefore + 1, validator.SubmitRuleRuns);
    }

    [Fact]
    public async Task The_split_verdict_matches_the_unsplit_one()
    {
        var split = await RunPostSubmitEditAsync(hideCapability: false);
        var unsplit = await RunPostSubmitEditAsync(hideCapability: true);

        // Empty would equal empty, so what the two are agreeing about is pinned first: the draft
        // rule still fails and the submit rule no longer does.
        Assert.Contains(split, x => x.Message == RuleRunCountingValidator.DraftMessage);
        Assert.DoesNotContain(split, x => x.Message == RuleRunCountingValidator.SubmitMessage);

        Assert.Equal(unsplit, split);
    }

    [Fact]
    public async Task A_debounced_edit_runs_each_common_rule_once()
    {
        var customer = new EngineCustomer { Name = "Bo" };
        var order = new EngineOrder { Customer = customer };
        var validator = new RuleRunCountingValidator();
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        using var engine = new FormValidationEngine<EngineOrder>(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(validator),
            new ReflectionModelIntrospector(),
            new FormidableOptions
            {
                LiveDebounce = TimeSpan.FromMilliseconds(400),
                RefreshDebounce = TimeSpan.FromMilliseconds(300),
                DisclosureOverride = _ => true,
            },
            time);

        Assert.False((await engine.ValidateForSubmitAsync()).CanProceed);

        order.Description = "Quarterly refresh";
        editContext.NotifyFieldChanged(new FieldIdentifier(order, nameof(EngineOrder.Description)));

        var draftBefore = validator.DraftRuleRuns;
        var submitBefore = validator.SubmitRuleRuns;

        // The refresh window is the narrower one here, so the refresh fires first and executes
        // the whole stale selection — both rules, at the edit's stamp.
        time.Advance(TimeSpan.FromMilliseconds(300));

        // The live window closes behind it and finds every rule it selects already answered at
        // its own stamp: it executes nothing and publishes from the store.
        time.Advance(TimeSpan.FromMilliseconds(100));

        // One edit, one execution of each rule — the same total as the immediate-live ordering,
        // reached in the opposite pass order. Reuse is keyed by rule and stamp, not by which
        // pass happened to run first.
        Assert.Equal(draftBefore + 1, validator.DraftRuleRuns);
        Assert.Equal(submitBefore + 1, validator.SubmitRuleRuns);
    }

    [Fact]
    public async Task An_edit_during_an_in_flight_live_pass_is_re_checked_before_the_refresh_reuses_it()
    {
        var customer = new EngineCustomer { Name = "Bo" };
        var order = new EngineOrder { Customer = customer };
        var validator = new GatedRuleRunCountingValidator();
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        using var engine = new FormValidationEngine<EngineOrder>(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(validator),
            new ReflectionModelIntrospector(),
            new FormidableOptions
            {
                LiveDebounce = TimeSpan.FromMilliseconds(100),
                RefreshDebounce = TimeSpan.FromMilliseconds(300),
                DisclosureOverride = _ => true,
            },
            time);

        // Submit on an open gate, populating the store at the pre-edit stamp.
        var submit = engine.ValidateForSubmitAsync();
        validator.Gate.SetResult();
        await submit;
        validator.Reset();

        // A first edit opens the live window; its pass starts on the window's close and blocks
        // on the gate.
        order.Description = "Quarterly refresh";
        editContext.NotifyFieldChanged(new FieldIdentifier(order, nameof(EngineOrder.Description)));
        time.Advance(TimeSpan.FromMilliseconds(100));

        // A second edit lands while that pass is still in flight. It moves the model on and
        // re-arms both windows from itself; a debounced edit never cancels a pass already
        // running.
        customer.Name = "far too long";
        editContext.NotifyFieldChanged(new FieldIdentifier(customer, nameof(EngineCustomer.Name)));

        // The in-flight pass finishes as the current pass and writes its verdicts — stamped
        // with the stamp it READ AS IT BEGAN, one edit behind the model as it now stands.
        var settled = Quiescence(engine);
        validator.Gate.SetResult();
        await settled;
        validator.Reset();

        var draftBefore = validator.DraftRuleRuns;
        var submitBefore = validator.SubmitRuleRuns;

        // The second edit's own live window closes: the stored draft verdict carries the FIRST
        // edit's stamp, so it cannot be served here — a fresh execution answers for the second
        // edit's model. Stamping at apply time instead of begin time is the mutation this
        // catches: the verdict would then claim an edit its validation never saw, and this pass
        // would have nothing to run.
        var secondLive = Quiescence(engine);
        time.Advance(TimeSpan.FromMilliseconds(100));
        validator.Gate.SetResult();
        await secondLive;

        Assert.Equal(draftBefore + 1, validator.DraftRuleRuns);
        Assert.Equal(submitBefore, validator.SubmitRuleRuns);
        Assert.False(engine.IsValidating);

        // The refresh follows and serves that now-current verdict from the store rather than
        // executing the draft rule a second time; only the submit rule, still carrying the
        // submit-time stamp, executes.
        time.Advance(TimeSpan.FromMilliseconds(200));
        Assert.Equal(draftBefore + 1, validator.DraftRuleRuns);
        Assert.Equal(submitBefore + 1, validator.SubmitRuleRuns);
    }

    [Fact]
    public async Task A_capability_less_validator_runs_the_full_profile()
    {
        var customer = new EngineCustomer { Name = "far too long" };
        var order = new EngineOrder { Customer = customer };
        var validator = new RuleRunCountingValidator();
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        using var engine = new FormValidationEngine<EngineOrder>(
            order, editContext,
            // The wrapper hides the rule-level capability, so every pass validates its whole
            // profile in one call — no store, no per-rule accounting.
            new CapabilityHidingModelValidator<EngineOrder>(
                new FluentValidationModelValidator<EngineOrder>(validator)),
            new ReflectionModelIntrospector(),
            new FormidableOptions { DisclosureOverride = _ => true },
            time);

        // Both buckets fail, so both fields are error sites the refresh keeps current.
        Assert.False((await engine.ValidateForSubmitAsync()).CanProceed);

        // An edit that leaves both rules failing, so the refresh's verdict has to carry both.
        // Its own immediate live pass runs the whole live profile first.
        customer.Name = "still far too long";
        editContext.NotifyFieldChanged(new FieldIdentifier(customer, nameof(EngineCustomer.Name)));

        var draftBefore = validator.DraftRuleRuns;
        var submitBefore = validator.SubmitRuleRuns;

        time.Advance(TimeSpan.FromMilliseconds(301));

        // The refresh pays the whole submit profile: the draft rule runs again even though the
        // live pass just answered it — the honest cost of a validator the engine cannot ask
        // rule by rule.
        Assert.Equal(draftBefore + 1, validator.DraftRuleRuns);
        Assert.Equal(submitBefore + 1, validator.SubmitRuleRuns);

        var visible = engine.GetVisibleIssues();
        Assert.Contains(visible, v => v.Issue.Message == RuleRunCountingValidator.SubmitMessage);
        Assert.Contains(visible, v => v.Issue.Message == RuleRunCountingValidator.DraftMessage);
    }

    [Fact]
    public async Task A_refresh_with_nothing_stale_executes_nothing()
    {
        var customer = new EngineCustomer { Name = "far too long" };
        var order = new EngineOrder { Customer = customer };
        var validator = new RuleRunCountingValidator();
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        using var engine = new FormValidationEngine<EngineOrder>(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(validator),
            new ReflectionModelIntrospector(),
            new FormidableOptions
            {
                // Live and submit select the same rules, so the edit's own live pass answers
                // everything the refresh will need at the same stamp.
                LiveProfile = ValidationProfile.Submit,
                DisclosureOverride = _ => true,
            },
            time);

        Assert.False((await engine.ValidateForSubmitAsync()).CanProceed);

        order.Description = "Quarterly refresh";
        editContext.NotifyFieldChanged(new FieldIdentifier(order, nameof(EngineOrder.Description)));

        // Counted after the edit's own live pass has run, so what follows is the refresh alone.
        var draftBefore = validator.DraftRuleRuns;
        var submitBefore = validator.SubmitRuleRuns;

        time.Advance(TimeSpan.FromMilliseconds(301));

        Assert.Equal(draftBefore, validator.DraftRuleRuns);
        Assert.Equal(submitBefore, validator.SubmitRuleRuns);

        // The verdict still answers for the model: the description is fixed, the name is not.
        var visible = engine.GetVisibleIssues();
        Assert.DoesNotContain(visible, v => v.Issue.Message == RuleRunCountingValidator.SubmitMessage);
        Assert.Contains(visible, v => v.Issue.Message == RuleRunCountingValidator.DraftMessage);
    }

    [Fact]
    public async Task A_live_profile_swap_leaves_the_verdict_whole()
    {
        var customer = new EngineCustomer { Name = "far too long" };
        var order = new EngineOrder { Customer = customer };
        var validator = new RuleRunCountingValidator();
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        var options = new FormidableOptions { DisclosureOverride = _ => true };
        using var engine = new FormValidationEngine<EngineOrder>(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(validator),
            new ReflectionModelIntrospector(),
            options,
            time);

        // Both buckets fail, so both fields are error sites the refresh keeps current.
        Assert.False((await engine.ValidateForSubmitAsync()).CanProceed);

        // An edit that leaves both rules failing. Its live pass runs under the profile
        // configured right now and stores what it executed, verdicts keyed by rule.
        customer.Name = "still far too long";
        editContext.NotifyFieldChanged(new FieldIdentifier(customer, nameof(EngineCustomer.Name)));

        // The live profile is swapped before the refresh comes due, on the same options instance
        // the engine holds — the pattern the sample teaches for changing engine behaviour without
        // a model swap. A rule verdict is a fact about the rule at a model state, not about the
        // profile that happened to select it, so the swap strands nothing and invents nothing.
        options.LiveProfile = ValidationProfile.Submit;

        time.Advance(TimeSpan.FromMilliseconds(301));

        // The refresh serves the draft rule's verdict from the store and executes the submit
        // rule it still owes: both buckets' issues survive, whole.
        var visible = engine.GetVisibleIssues();
        Assert.Contains(visible, v => v.Issue.Message == RuleRunCountingValidator.SubmitMessage);
        Assert.Contains(visible, v => v.Issue.Message == RuleRunCountingValidator.DraftMessage);
    }

    [Fact]
    public async Task A_refresh_with_nothing_to_execute_still_runs_the_pass_lifecycle()
    {
        var customer = new EngineCustomer { Name = "far too long" };
        var order = new EngineOrder { Customer = customer };
        var validator = new RuleRunCountingValidator();
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        using var engine = new FormValidationEngine<EngineOrder>(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(validator),
            new ReflectionModelIntrospector(),
            new FormidableOptions
            {
                // Live and submit select the same rules, so every refresh below executes nothing.
                LiveProfile = ValidationProfile.Submit,
                DisclosureOverride = _ => true,
            },
            time);

        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));
        var customerName = new FieldIdentifier(customer, nameof(EngineCustomer.Name));

        Assert.False((await engine.ValidateForSubmitAsync()).CanProceed);

        // Each edit's own live pass has already run and notified by the time the recorder below is
        // attached, so what it records is the refresh alone.
        order.Description = "Quarterly refresh";
        editContext.NotifyFieldChanged(description);

        var first = RecordScopeWhileValidating(engine, description, customerName, () =>
            time.Advance(TimeSpan.FromMilliseconds(301)));

        // A pass that executes nothing is still a pass: it lights the pending indicator, and it
        // lights it scoped to the field edited in the window rather than form-wide.
        Assert.NotEmpty(first);
        Assert.All(first, scope => Assert.True(scope.First));
        Assert.All(first, scope => Assert.False(scope.Second));

        customer.Name = "still far too long";
        editContext.NotifyFieldChanged(customerName);

        var second = RecordScopeWhileValidating(engine, description, customerName, () =>
            time.Advance(TimeSpan.FromMilliseconds(301)));

        // Only the second edit's field, which is what a snapshot-and-clear of the accumulator
        // means: a refresh that never cleared it would still be carrying the first edit's field.
        Assert.NotEmpty(second);
        Assert.All(second, scope => Assert.True(scope.Second));
        Assert.All(second, scope => Assert.False(scope.First));
    }

    /// <summary>
    /// Runs <paramref name="act"/> and reports how each of two fields read while a pass was in
    /// flight, one entry per notification the engine raised with its validating flag set. The
    /// subscription lasts only as long as the call, so a caller can bracket one pass at a time.
    /// </summary>
    private static List<(bool First, bool Second)> RecordScopeWhileValidating(
        FormValidationEngine<EngineOrder> engine,
        FieldIdentifier first,
        FieldIdentifier second,
        Action act)
    {
        var recorded = new List<(bool First, bool Second)>();
        void Record()
        {
            if (engine.IsValidating)
            {
                recorded.Add((
                    engine.GetFieldState(first).IsValidating,
                    engine.GetFieldState(second).IsValidating));
            }
        }

        engine.StateChanged += Record;
        try
        {
            act();
        }
        finally
        {
            engine.StateChanged -= Record;
        }

        return recorded;
    }

    /// <summary>
    /// Submits a form failing both rule buckets, fixes the description, and returns what the
    /// refresh leaves visible — sorted, so the comparison is of the (field, message) pairs
    /// themselves rather than of dictionary iteration order. <paramref name="hideCapability"/>
    /// false takes the per-rule store path; true wraps the validator so the engine falls back to
    /// whole-profile validation, which is the unsplit control the store-assembled verdict must
    /// match. Fields are named rather than identified because the two runs are two model
    /// instances, and a <see cref="FieldIdentifier"/> is equal only to one over the same object.
    /// </summary>
    private static async Task<List<(string Field, string Message)>> RunPostSubmitEditAsync(
        bool hideCapability)
    {
        var customer = new EngineCustomer { Name = "far too long" };
        var order = new EngineOrder { Customer = customer };
        var validator = new RuleRunCountingValidator();
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        IModelValidator<EngineOrder> modelValidator = new FluentValidationModelValidator<EngineOrder>(validator);
        if (hideCapability)
        {
            modelValidator = new CapabilityHidingModelValidator<EngineOrder>(modelValidator);
        }

        using var engine = new FormValidationEngine<EngineOrder>(
            order, editContext,
            modelValidator,
            new ReflectionModelIntrospector(),
            new FormidableOptions { DisclosureOverride = _ => true },
            time);

        // Both buckets fail, so both fields are error sites the refresh keeps current.
        Assert.False((await engine.ValidateForSubmitAsync()).CanProceed);

        // One edit clears the submit rule and leaves the draft rule failing, so the refresh has to
        // drop one issue and keep the other.
        order.Description = "Quarterly refresh";
        editContext.NotifyFieldChanged(new FieldIdentifier(order, nameof(EngineOrder.Description)));
        time.Advance(TimeSpan.FromMilliseconds(301));

        return
        [
            .. engine.GetVisibleIssues()
                .Select(v => (Field: $"{v.Field.Model.GetType().Name}.{v.Field.FieldName}", v.Issue.Message))
                .OrderBy(x => x.Field, StringComparer.Ordinal)
                .ThenBy(x => x.Message, StringComparer.Ordinal)
        ];
    }
}
