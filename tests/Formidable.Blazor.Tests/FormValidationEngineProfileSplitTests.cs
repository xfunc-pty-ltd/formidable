using Formidable.Blazor.Tests.Fixtures;
using Formidable.Introspection;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Time.Testing;
using static Formidable.Blazor.Tests.Fixtures.EngineTestSync;

namespace Formidable.Blazor.Tests;

/// <summary>
/// The submit profile includes the live profile's rules, so a post-submit edit arms two passes
/// that overlap on every rule they share. These pin that the refresh runs only the difference and
/// puts the live pass's own report back in its place — and, just as importantly, that every way
/// of failing to establish the difference falls back to the whole submit profile rather than to a
/// partial verdict.
/// </summary>
/// <remarks>
/// The counters are what carry these tests: two runs of a rule leave exactly what one run leaves,
/// so a verdict assertion alone cannot tell them apart. Where a verdict IS asserted, it is
/// asserted against the fallback's own verdict for the same sequence, so the two have to agree
/// about issues rather than merely about how many there are.
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

        // One edit, one execution of each rule: the live pass ran the draft bucket and the refresh
        // that followed it ran only what the live profile leaves out.
        Assert.Equal(draftBefore + 1, validator.DraftRuleRuns);
        Assert.Equal(submitBefore + 1, validator.SubmitRuleRuns);
    }

    [Fact]
    public async Task The_split_verdict_matches_the_unsplit_one()
    {
        var split = await RunPostSubmitEditAsync(liveProfile: null);
        var unsplit = await RunPostSubmitEditAsync(
            liveProfile: ValidationProfile.Named(
                "DraftPlusExtra", includeDefaultRules: true, RuleRunCountingValidator.ExtraRuleSetName));

        // Empty would equal empty, so what the two are agreeing about is pinned first: the draft
        // rule still fails and the submit rule no longer does.
        Assert.Contains(split, x => x.Message == RuleRunCountingValidator.DraftMessage);
        Assert.DoesNotContain(split, x => x.Message == RuleRunCountingValidator.SubmitMessage);

        Assert.Equal(unsplit, split);
    }

    [Fact]
    public async Task A_stale_retained_report_forces_the_full_profile()
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
                // The refresh window is the shorter one, so the refresh staged below comes due
                // before the live window the same edit re-armed.
                LiveDebounce = TimeSpan.FromMilliseconds(100),
                RefreshDebounce = TimeSpan.FromMilliseconds(50),
                DisclosureOverride = _ => true,
            },
            time);

        // This edit lands before the form has ever been submitted, so it opens the live window
        // without arming a refresh.
        order.Description = "Quarterly refresh";
        editContext.NotifyFieldChanged(new FieldIdentifier(order, nameof(EngineOrder.Description)));

        // Submit on an open gate, which leaves HasSubmitted set and the live window still open.
        var submit = engine.ValidateForSubmitAsync();
        validator.Gate.SetResult();
        await submit;
        validator.Reset();

        // The live pass that window was holding starts, and blocks.
        time.Advance(TimeSpan.FromMilliseconds(100));

        // A second edit lands while that pass is still in flight. It moves the model on, and
        // because a live window only re-arms it starts no pass of its own.
        customer.Name = "far too long";
        editContext.NotifyFieldChanged(new FieldIdentifier(customer, nameof(EngineCustomer.Name)));

        // The in-flight pass now finishes as the current pass, so it retains its report — a report
        // taken for the model as it stood one edit ago, even though the edit standing when the
        // verdict lands is the newer one.
        var settled = Quiescence(engine);
        validator.Gate.SetResult();
        await settled;

        var draftBefore = validator.DraftRuleRuns;

        // The refresh comes due (at 150 ms) before the re-armed live window (at 200 ms), so
        // nothing has re-validated the model since. The retained report is one edit behind, and a
        // refresh that reused it would answer for the wrong model.
        time.Advance(TimeSpan.FromMilliseconds(50));

        Assert.Equal(draftBefore + 1, validator.DraftRuleRuns);
    }

    [Fact]
    public async Task A_non_subtractable_profile_pair_runs_the_full_profile()
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
                // Selects exactly what Draft selects, since Extra carries no rules, while naming a
                // ruleset the submit profile does not — which is what makes the pair unsubtractable.
                LiveProfile = ValidationProfile.Named(
                    "DraftPlusExtra", includeDefaultRules: true, RuleRunCountingValidator.ExtraRuleSetName),
                DisclosureOverride = _ => true,
            },
            time);

        // Both buckets fail, so both fields are error sites the refresh keeps current.
        Assert.False((await engine.ValidateForSubmitAsync()).CanProceed);

        // An edit that leaves both rules failing, so the refresh's verdict has to carry both.
        customer.Name = "still far too long";
        editContext.NotifyFieldChanged(new FieldIdentifier(customer, nameof(EngineCustomer.Name)));
        time.Advance(TimeSpan.FromMilliseconds(301));

        var visible = engine.GetVisibleIssues();
        Assert.Contains(visible, v => v.Issue.Message == RuleRunCountingValidator.SubmitMessage);
        Assert.Contains(visible, v => v.Issue.Message == RuleRunCountingValidator.DraftMessage);
    }

    [Fact]
    public async Task An_empty_delta_runs_no_pass()
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
                // Live and submit select the same rules, so a refresh has nothing left of its own.
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
    public async Task A_live_profile_swap_forces_the_full_profile()
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

        // An edit that leaves both rules failing. Its live pass runs under the profile configured
        // right now, and retains the report that pass produced.
        customer.Name = "still far too long";
        editContext.NotifyFieldChanged(new FieldIdentifier(customer, nameof(EngineCustomer.Name)));

        // The live profile is swapped before the refresh comes due, on the same options instance
        // the engine holds — the pattern the sample teaches for changing engine behaviour without
        // a model swap. Subtracting this profile would make the difference empty and publish the
        // retained report, which answers for the profile that is no longer configured.
        options.LiveProfile = ValidationProfile.Submit;

        time.Advance(TimeSpan.FromMilliseconds(301));

        // The retained report cannot be reconciled with a profile it never ran under, so the
        // refresh validates everything: both buckets' issues survive.
        var visible = engine.GetVisibleIssues();
        Assert.Contains(visible, v => v.Issue.Message == RuleRunCountingValidator.SubmitMessage);
        Assert.Contains(visible, v => v.Issue.Message == RuleRunCountingValidator.DraftMessage);
    }

    [Fact]
    public async Task An_empty_delta_still_runs_the_pass_lifecycle()
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
                // Live and submit select the same rules, so every refresh below runs no validation.
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

        // A pass that validates nothing is still a pass: it lights the pending indicator, and it
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
    /// themselves rather than of dictionary iteration order. A null <paramref name="liveProfile"/>
    /// takes the default, subtractable pair; naming the validator's rule-less Extra ruleset
    /// instead selects the same rules while making the pair unsubtractable, which forces the
    /// full-profile fallback. Fields are named rather than identified because the two runs are
    /// two model instances, and a <see cref="FieldIdentifier"/> is equal only to one over the
    /// same object.
    /// </summary>
    private static async Task<List<(string Field, string Message)>> RunPostSubmitEditAsync(
        ValidationProfile? liveProfile)
    {
        var customer = new EngineCustomer { Name = "far too long" };
        var order = new EngineOrder { Customer = customer };
        var validator = new RuleRunCountingValidator();
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        var options = new FormidableOptions { DisclosureOverride = _ => true };
        if (liveProfile is not null)
        {
            options.LiveProfile = liveProfile;
        }

        using var engine = new FormValidationEngine<EngineOrder>(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(validator),
            new ReflectionModelIntrospector(),
            options,
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
