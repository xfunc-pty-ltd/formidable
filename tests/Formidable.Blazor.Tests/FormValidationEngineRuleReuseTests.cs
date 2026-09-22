using FluentValidation;
using Formidable.Blazor.Tests.Fixtures;
using Formidable.Introspection;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Time.Testing;
using static Formidable.Blazor.Tests.Fixtures.EngineTestSync;

namespace Formidable.Blazor.Tests;

/// <summary>
/// The per-rule verdict store: after one edit, each rule the live and submit profiles select
/// executes AT MOST ONCE across the live pass and the post-submit refresh, in any pass order —
/// by construction (verdicts stamped by edit stamp, keyed by rule identity), not by scheduling.
/// The headline pin runs the shipped <c>/workout</c> Engaged shape, where profile-name
/// arithmetic could not see that a <c>RuleSet("Submit,Engaged", ...)</c> rule is one rule.
/// </summary>
/// <remarks>
/// The counters are what carry these tests: two runs of a rule leave exactly the issues one run
/// leaves, so a verdict assertion alone cannot tell reuse from re-execution. No component ever
/// registers a field in these bare-engine tests, so submit-time visibility would suppress every
/// error; <c>DisclosureOverride</c> forces it open where a submit channel is read. The live
/// channel is never filtered by registration at all.
/// </remarks>
public class FormValidationEngineRuleReuseTests
{
    // The same LiveProfile /workout's options object builds, by the same name.
    private static readonly ValidationProfile EngagedLiveProfile =
        ValidationProfile.Named("Engaged", includeDefaultRules: true, "Engaged");

    private sealed class ReuseAttendee
    {
        public string Name { get; set; } = string.Empty;
    }

    private sealed class ReuseModel
    {
        public List<ReuseAttendee> Attendees { get; } = [new ReuseAttendee()];
        public string EventName { get; set; } = string.Empty;
        public string Nickname { get; set; } = string.Empty;
    }

    /// <summary>
    /// The Engaged shape with an execution counter on every bucket: a draft-bucket rule (runs
    /// under any profile including default rules), a Submit-only rule, and a dual-membership
    /// rule declared once into BOTH "Submit" and "Engaged" via the raw comma-named RuleSet call
    /// — the same <c>RuleForEach.ChildRules</c> row shape <c>/workout</c>'s attendee rule uses.
    /// The empty <c>Profile("Engaged", ...)</c> registers the name for ruleset verification;
    /// the "Extra" profile carries a rule no other profile selects, for the profile-swap pin.
    /// </summary>
    private sealed class ReuseCountingValidator : DraftSubmitValidator<ReuseModel>
    {
        public const string DraftMessage = "Nickname is too long";
        public const string SubmitOnlyMessage = "Event name is required";
        public const string DualMessage = "Name is required";
        public const string ExtraMessage = "Nickname is reserved";

        public int DraftRuns;
        public int SubmitOnlyRuns;
        public int DualRuns;
        public int ExtraRuns;

        protected override void ConfigureDraftRules() =>
            RuleFor(x => x.Nickname)
                .Must(nickname =>
                {
                    DraftRuns++;
                    return nickname.Length <= 10;
                })
                .WithMessage(DraftMessage);

        protected override void ConfigureSubmitRules() =>
            RuleFor(x => x.EventName)
                .Must(name =>
                {
                    SubmitOnlyRuns++;
                    return name.Length > 0;
                })
                .WithMessage(SubmitOnlyMessage);

        protected override void ConfigureAdditionalProfiles()
        {
            Profile("Engaged", () => { });
            RuleSet("Submit,Engaged", () =>
                RuleForEach(x => x.Attendees).ChildRules(attendee =>
                    attendee.RuleFor(a => a.Name)
                        .Must(name =>
                        {
                            DualRuns++;
                            return name.Length > 0;
                        })
                        .WithMessage(DualMessage)));
            Profile("Extra", () =>
                RuleFor(x => x.Nickname)
                    .Must(nickname =>
                    {
                        ExtraRuns++;
                        return !nickname.StartsWith("zz", StringComparison.Ordinal);
                    })
                    .WithMessage(ExtraMessage));
        }
    }

    private static FormValidationEngine<ReuseModel> CreateEngagedEngine(
        ReuseModel model,
        ReuseCountingValidator validator,
        EditContext editContext,
        TimeProvider time,
        TimeSpan? liveDebounce = null,
        bool hideCapability = false)
    {
        IModelValidator<ReuseModel> modelValidator = new FluentValidationModelValidator<ReuseModel>(validator);
        if (hideCapability)
        {
            modelValidator = new CapabilityHidingModelValidator<ReuseModel>(modelValidator);
        }

        return new(
            model, editContext,
            modelValidator,
            new ReflectionModelIntrospector(),
            new FormidableOptions
            {
                LiveProfile = EngagedLiveProfile,
                SubmitProfile = ValidationProfile.Submit,
                LiveDebounce = liveDebounce,
                RefreshDebounce = TimeSpan.FromMilliseconds(300),
                DisclosureOverride = _ => true,
            },
            time);
    }

    /// <summary>
    /// R1 at the shipped shape. One submit runs every counted rule once. One post-submit edit
    /// then triggers a debounced live pass under "Engaged" (the draft and dual rules) and the
    /// refresh that follows under "Submit" (all three) — and each rule the two profiles share
    /// must execute exactly once across the pair, because a verdict stamped with the edit the
    /// pass began under answers any later pass at the same stamp, whichever profile that pass
    /// runs. Name arithmetic over the profile pair cannot see this: "Engaged" is a name the
    /// submit profile's own list never carries, while the dual rule is one declared rule.
    /// </summary>
    [Fact]
    public async Task One_post_submit_edit_runs_each_selected_rule_at_most_once_across_both_passes()
    {
        var model = new ReuseModel();
        var validator = new ReuseCountingValidator();
        var editContext = new EditContext(model);
        var time = new FakeTimeProvider();
        using var engine = CreateEngagedEngine(
            model, validator, editContext, time, liveDebounce: TimeSpan.FromMilliseconds(100));

        // The submit blocks (the event name and the attendee name are both empty) and runs the
        // whole Submit profile: every counter moves to exactly one.
        Assert.False((await engine.ValidateForSubmitAsync()).CanProceed);
        Assert.Equal(1, validator.DraftRuns);
        Assert.Equal(1, validator.SubmitOnlyRuns);
        Assert.Equal(1, validator.DualRuns);

        // One post-submit edit arms both windows.
        model.Nickname = "edited";
        editContext.NotifyFieldChanged(new FieldIdentifier(model, nameof(ReuseModel.Nickname)));

        time.Advance(TimeSpan.FromMilliseconds(100)); // the live window closes: the Engaged pass answers
        time.Advance(TimeSpan.FromMilliseconds(200)); // the refresh window closes: the refresh follows

        // One edit, at most one execution per rule across both passes: the live pass ran the
        // draft and dual rules, and the refresh ran only the Submit-only rule, reusing the
        // other two verdicts from the store. Asserted as one triple so a failure reports the
        // whole execution signature — a whole-profile refresh reads (3, 2, 3).
        Assert.Equal(
            (Draft: 2, SubmitOnly: 2, Dual: 2),
            (Draft: validator.DraftRuns, SubmitOnly: validator.SubmitOnlyRuns, Dual: validator.DualRuns));
    }

    /// <summary>
    /// Reuse is bidirectional because freshness is keyed on the edit stamp, not on which pass
    /// ran first: with the live window wider than the refresh window, the refresh lands first
    /// and executes the whole stale selection — and the live window's own pass then finds every
    /// rule it selects already answered at its stamp, executes nothing, and still runs the full
    /// lifecycle and publishes. The publish is observable on its own: the draft failure is not
    /// a submit-time error site, so only a live pass's verdict apply can put it on the field.
    /// </summary>
    [Fact]
    public async Task A_refresh_that_lands_first_leaves_the_live_pass_nothing_to_run()
    {
        var model = new ReuseModel();
        var validator = new ReuseCountingValidator();
        var editContext = new EditContext(model);
        var time = new FakeTimeProvider();
        using var engine = CreateEngagedEngine(
            model, validator, editContext, time, liveDebounce: TimeSpan.FromMilliseconds(400));

        var nicknameField = new FieldIdentifier(model, nameof(ReuseModel.Nickname));

        Assert.False((await engine.ValidateForSubmitAsync()).CanProceed);

        // The edit breaks the draft rule; its window is still open when the refresh comes due.
        model.Nickname = "far too long for the rule";
        editContext.NotifyFieldChanged(nicknameField);

        time.Advance(TimeSpan.FromMilliseconds(300)); // the refresh lands first

        // The refresh executed the whole stale selection. Its own channel discloses none of the
        // draft failure — the nickname passed at submit time, so it is not a submit error site —
        // which is what makes the live pass's later publish observable.
        Assert.Equal(
            (Draft: 2, SubmitOnly: 2, Dual: 2),
            (Draft: validator.DraftRuns, SubmitOnly: validator.SubmitOnlyRuns, Dual: validator.DualRuns));
        Assert.Empty(engine.GetIssues(nicknameField));

        time.Advance(TimeSpan.FromMilliseconds(100)); // the live window closes behind it

        // Zero executions — every rule the Engaged profile selects is fresh at this stamp — yet
        // the pass published: the engaged field carries the draft verdict the refresh computed.
        Assert.Equal(
            (Draft: 2, SubmitOnly: 2, Dual: 2),
            (Draft: validator.DraftRuns, SubmitOnly: validator.SubmitOnlyRuns, Dual: validator.DualRuns));
        Assert.Contains(engine.GetIssues(nicknameField), i => i.Message == ReuseCountingValidator.DraftMessage);
    }

    /// <summary>
    /// A rendered-field-set change clears the store AND bumps its generation, and a verdict
    /// apply writes the store only while the generation it captured at begin is unchanged. The
    /// two halves are pinned together: a pass held in flight ACROSS the change completes as the
    /// current pass — nothing supersedes it — but must not repopulate the store, or the next
    /// refresh at the same stamp would reuse verdicts computed against a page, and possibly a
    /// model, that no longer exists.
    /// </summary>
    [Fact]
    public async Task A_field_set_change_invalidates_stored_verdicts()
    {
        var customer = new EngineCustomer { Name = "far too long" };
        var order = new EngineOrder { Customer = customer };
        var validator = new GatedRuleRunCountingValidator();
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        using var engine = new FormValidationEngine<EngineOrder>(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(validator),
            new ReflectionModelIntrospector(),
            new FormidableOptions { DisclosureOverride = _ => true },
            time);

        var submit = engine.ValidateForSubmitAsync();
        validator.Gate.SetResult();
        Assert.False((await submit).CanProceed);
        validator.Reset();

        // The edit's immediate live pass executes the stale draft rule and blocks on the gate.
        customer.Name = "still far too long";
        editContext.NotifyFieldChanged(new FieldIdentifier(customer, nameof(EngineCustomer.Name)));
        Assert.Equal(2, validator.DraftRuleRuns); // the submit's run plus the in-flight pass's

        // The rendered field set moves while that pass is still in flight.
        engine.OnRenderedFieldsChanged();

        var settled = Quiescence(engine);
        validator.Gate.SetResult();
        await settled;

        var draftBefore = validator.DraftRuleRuns;
        var submitBefore = validator.SubmitRuleRuns;

        time.Advance(TimeSpan.FromMilliseconds(301));

        // No edit moved the stamp since the live pass began, so only the cleared store — and
        // the generation gate keeping the in-flight pass from refilling it — can explain the
        // refresh re-executing the draft rule here.
        Assert.Equal(draftBefore + 1, validator.DraftRuleRuns);
        Assert.Equal(submitBefore + 1, validator.SubmitRuleRuns);
    }

    /// <summary>
    /// Rule verdicts are facts about rules, not about profiles: swapping the live profile
    /// mid-sequence re-selects, and the next live pass runs exactly the selected rules that
    /// have no verdict at the current stamp — a rule both profiles select is not re-run, and a
    /// rule only the swapped-in profile selects is never served from thin air.
    /// </summary>
    [Fact]
    public async Task A_live_profile_swap_never_serves_an_unrun_rule()
    {
        var model = new ReuseModel();
        var validator = new ReuseCountingValidator();
        var editContext = new EditContext(model);
        var time = new FakeTimeProvider();
        var options = new FormidableOptions
        {
            // The default live profile (Draft) to start with; swapped below on the same options
            // instance the engine holds — the sanctioned mutate-in-place pattern.
            LiveDebounce = TimeSpan.FromMilliseconds(400),
            RefreshDebounce = TimeSpan.FromMilliseconds(300),
            DisclosureOverride = _ => true,
        };
        using var engine = new FormValidationEngine<ReuseModel>(
            model, editContext,
            new FluentValidationModelValidator<ReuseModel>(validator),
            new ReflectionModelIntrospector(),
            options,
            time);

        var nicknameField = new FieldIdentifier(model, nameof(ReuseModel.Nickname));

        Assert.False((await engine.ValidateForSubmitAsync()).CanProceed);

        // The edit fails the draft rule AND the Extra rule — the latter has still never run.
        model.Nickname = "zz-reserved and far too long";
        editContext.NotifyFieldChanged(nicknameField);

        time.Advance(TimeSpan.FromMilliseconds(300)); // the refresh executes the stale Submit selection

        Assert.Equal(2, validator.DraftRuns);
        Assert.Equal(0, validator.ExtraRuns);

        // Swap before the live window closes: the window's pass runs under the profile in
        // force at its own begin.
        options.LiveProfile = ValidationProfile.Named("DraftPlusExtra", includeDefaultRules: true, "Extra");

        time.Advance(TimeSpan.FromMilliseconds(100)); // the live window closes under the swapped profile

        // The draft rule both profiles select is served from the store; the Extra rule, which
        // no pass has ever executed, runs — and both verdicts reach the live channel.
        Assert.Equal(2, validator.DraftRuns);
        Assert.Equal(1, validator.ExtraRuns);
        Assert.Contains(engine.GetIssues(nicknameField), i => i.Message == ReuseCountingValidator.DraftMessage);
        Assert.Contains(engine.GetIssues(nicknameField), i => i.Message == ReuseCountingValidator.ExtraMessage);
    }

    /// <summary>
    /// The refresh's assembled verdict — fresh-from-store and just-executed rule reports
    /// concatenated in declaration order — is issue-for-issue what a fresh whole-profile
    /// validation of the same model state produces. Every counted rule fails at submit and
    /// keeps failing after the edit, so the comparison is over a non-empty issue set.
    /// </summary>
    [Fact]
    public async Task The_assembled_refresh_verdict_equals_a_fresh_full_validation()
    {
        var model = new ReuseModel { Nickname = "far too long for the rule" };
        var validator = new ReuseCountingValidator();
        var editContext = new EditContext(model);
        var time = new FakeTimeProvider();
        using var engine = CreateEngagedEngine(model, validator, editContext, time);

        var nicknameField = new FieldIdentifier(model, nameof(ReuseModel.Nickname));

        // All three counted rules fail, so every field is a submit-time error site the refresh
        // keeps current.
        Assert.False((await engine.ValidateForSubmitAsync()).CanProceed);

        // The edit leaves the draft rule failing; its immediate live pass and the refresh that
        // follows split the work between them.
        model.Nickname = "even longer and still far too long";
        editContext.NotifyFieldChanged(nicknameField);
        time.Advance(TimeSpan.FromMilliseconds(301));

        var direct = await new FluentValidationModelValidator<ReuseModel>(new ReuseCountingValidator())
            .ValidateAsync(model, ValidationProfile.Submit);

        // Empty would equal empty, so the agreement is pinned non-vacuous first.
        Assert.Equal(3, direct.Issues.Count);

        Assert.Equal(
            direct.Issues
                .Select(i => (i.Path, i.Message))
                .OrderBy(x => x.Path, StringComparer.Ordinal).ThenBy(x => x.Message, StringComparer.Ordinal)
                .ToList(),
            engine.GetVisibleIssues()
                .Select(v => (v.Issue.Path, v.Issue.Message))
                .OrderBy(x => x.Path, StringComparer.Ordinal).ThenBy(x => x.Message, StringComparer.Ordinal)
                .ToList());
    }

    /// <summary>
    /// A validator without the rule-level capability takes the whole-profile fallback on every
    /// pass: correct, unoptimised. The counters show full-profile execution — the shape the
    /// capability path's headline pin forbids — while the visible verdict is identical to the
    /// capability path's for the same sequence.
    /// </summary>
    [Fact]
    public async Task A_capability_less_validator_stays_correct_and_unoptimised()
    {
        var (capabilityIssues, capabilityCounters) = await RunEngagedPostSubmitEditAsync(hideCapability: false);
        var (fallbackIssues, fallbackCounters) = await RunEngagedPostSubmitEditAsync(hideCapability: true);

        // The fallback pays the whole submit profile on the refresh — the draft and dual rules
        // run once in the live pass and again in the refresh — where the capability path runs
        // each rule at most once per stamp.
        Assert.Equal((2, 2, 2), capabilityCounters);
        Assert.Equal((3, 2, 3), fallbackCounters);

        // Both paths reach the same verdict, pinned non-vacuous first.
        Assert.NotEmpty(capabilityIssues);
        Assert.Equal(capabilityIssues, fallbackIssues);
    }

    /// <summary>
    /// A live-debounce fire that finds <see cref="FormidableOptions.LiveDebounce"/> cleared out
    /// from under its own open window — options mutate in place — neither throws nor strands
    /// what the window accumulated: the accumulator empties with the window, the pass in flight
    /// is left untouched, and the refresh the same edit armed still runs.
    /// </summary>
    [Fact]
    public async Task A_null_LiveDebounce_fire_neither_throws_nor_strands()
    {
        var customer = new EngineCustomer { Name = "Bo" };
        var order = new EngineOrder { Customer = customer };
        var validator = new GatedRuleRunCountingValidator();
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        var options = new FormidableOptions
        {
            LiveDebounce = TimeSpan.FromMilliseconds(400),
            RefreshDebounce = TimeSpan.FromMilliseconds(300),
            DisclosureOverride = _ => true,
        };
        using var engine = new FormValidationEngine<EngineOrder>(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(validator),
            new ReflectionModelIntrospector(),
            options,
            time);

        // A long submit stays in flight through the fire below, until released.
        var submit = engine.ValidateForSubmitAsync();

        // The edit accumulates into the open window and arms the refresh; SubmitInFlight is
        // enough to arm it even though HasSubmitted has not flipped true yet.
        order.Description = "Quarterly refresh";
        editContext.NotifyFieldChanged(new FieldIdentifier(order, nameof(EngineOrder.Description)));

        // The consumer clears the option while the window is still open — ahead of the timer
        // that opened it ever coming due.
        options.LiveDebounce = null;

        time.Advance(TimeSpan.FromMilliseconds(300)); // the refresh defers to the in-flight submit

        // The window's own fire lands against the in-flight submit, finds no duration left to
        // re-arm with, and must close the window rather than throw or start a pass that would
        // cancel the submit.
        var fireException = Record.Exception(() => time.Advance(TimeSpan.FromMilliseconds(100)));
        Assert.Null(fireException);

        validator.Gate.SetResult();
        await submit;

        var draftBefore = validator.DraftRuleRuns;
        var submitBefore = validator.SubmitRuleRuns;

        // The deferred refresh comes due with nothing in its way and runs; no live pass ever
        // fires for the emptied accumulator.
        time.Advance(TimeSpan.FromMilliseconds(200));

        Assert.Equal(draftBefore + 1, validator.DraftRuleRuns);
        Assert.Equal(submitBefore + 1, validator.SubmitRuleRuns);
        Assert.False(engine.IsValidating);
    }

    private const string ScopedChildMessage = "Details code is required";
    private const string ScopedSiblingMessage = "Name is required";

    private sealed class ScopedDetails
    {
        public string Code { get; set; } = string.Empty;
    }

    private sealed class ScopedModel
    {
        public ScopedDetails Details { get; } = new();
        public List<ReuseAttendee> Attendees { get; } = [new ReuseAttendee()];
    }

    private sealed class ScopedDetailsValidator : AbstractValidator<ScopedDetails>
    {
        public ScopedDetailsValidator(Action countRun) =>
            RuleSet("Submit", () =>
                RuleFor(d => d.Code)
                    .Must(code =>
                    {
                        countRun();
                        return code.Length > 0;
                    })
                    .WithMessage(ScopedChildMessage));
    }

    /// <summary>
    /// A dual-membership rule whose <c>SetValidator</c> child carries its OWN ruleset tag — the
    /// shape whose execution consults a child-scope decision that differs across profiles, so
    /// its verdicts come back profile-scoped. The sibling row rule's children carry only their
    /// parent's propagated tags, so its verdicts stay profile-independent.
    /// </summary>
    private sealed class ProfileScopedValidator : DraftSubmitValidator<ScopedModel>
    {
        public int ChildRuns;
        public int SiblingRuns;

        protected override void ConfigureDraftRules()
        {
        }

        protected override void ConfigureSubmitRules()
        {
        }

        protected override void ConfigureAdditionalProfiles()
        {
            Profile("Engaged", () => { });
            RuleSet("Submit,Engaged", () =>
            {
                RuleFor(x => x.Details).SetValidator(new ScopedDetailsValidator(() => ChildRuns++));
                RuleForEach(x => x.Attendees).ChildRules(attendee =>
                    attendee.RuleFor(a => a.Name)
                        .Must(name =>
                        {
                            SiblingRuns++;
                            return name.Length > 0;
                        })
                        .WithMessage(ScopedSiblingMessage));
            });
        }
    }

    /// <summary>
    /// A profile-scoped verdict is honest about its limits: the live pass under "Engaged" runs
    /// the SetValidator rule but the profile filters its child out, so that verdict answers only
    /// for "Engaged" — the refresh under "Submit" must re-run it (its child now admitted) rather
    /// than reuse a verdict that would silently drop the child's failure. The sibling rule's
    /// profile-independent verdict is reused across the same pair, and both channels stay
    /// whole-profile-correct throughout.
    /// </summary>
    [Fact]
    public async Task A_profile_scoped_verdict_is_not_reused_across_profiles()
    {
        var model = new ScopedModel();
        var validator = new ProfileScopedValidator();
        var editContext = new EditContext(model);
        var time = new FakeTimeProvider();
        using var engine = new FormValidationEngine<ScopedModel>(
            model, editContext,
            new FluentValidationModelValidator<ScopedModel>(validator),
            new ReflectionModelIntrospector(),
            new FormidableOptions
            {
                LiveProfile = EngagedLiveProfile,
                SubmitProfile = ValidationProfile.Submit,
                DisclosureOverride = _ => true,
            },
            time);

        var nameField = new FieldIdentifier(model.Attendees[0], nameof(ReuseAttendee.Name));

        // The submit runs both rules under "Submit": the child is admitted and fails.
        Assert.False((await engine.ValidateForSubmitAsync()).CanProceed);
        Assert.Equal(1, validator.ChildRuns);
        Assert.Equal(1, validator.SiblingRuns);

        // The edit's immediate live pass runs under "Engaged": the SetValidator rule executes,
        // but the profile filters its "Submit"-tagged child out — the counter stands still, and
        // the live channel honestly shows nothing for a child the profile does not select.
        model.Attendees[0].Name = "Ada";
        editContext.NotifyFieldChanged(nameField);
        Assert.Equal(1, validator.ChildRuns);
        Assert.Equal(2, validator.SiblingRuns);

        time.Advance(TimeSpan.FromMilliseconds(301));

        // The refresh runs under "Submit" at the same stamp: the sibling's verdict is reused,
        // while the SetValidator rule re-runs — its stored verdict is scoped to "Engaged", and
        // serving it here would erase the child's still-failing issue from the submit channel.
        Assert.Equal(2, validator.ChildRuns);
        Assert.Equal(2, validator.SiblingRuns);

        var visible = engine.GetVisibleIssues();
        Assert.Contains(visible, v => v.Issue.Message == ScopedChildMessage);
        Assert.DoesNotContain(visible, v => v.Issue.Message == ScopedSiblingMessage);
    }

    /// <summary>
    /// One submit, one post-submit edit that leaves every counted rule failing, both windows
    /// drained — returns the visible issues (sorted so the comparison is of the pairs, not of
    /// dictionary iteration order) and the three execution counters. The capability path and
    /// the hidden-capability fallback run the identical sequence.
    /// </summary>
    private static async Task<(List<(string Path, string Message)> Issues, (int Draft, int SubmitOnly, int Dual) Counters)>
        RunEngagedPostSubmitEditAsync(bool hideCapability)
    {
        var model = new ReuseModel { Nickname = "far too long for the rule" };
        var validator = new ReuseCountingValidator();
        var editContext = new EditContext(model);
        var time = new FakeTimeProvider();
        using var engine = CreateEngagedEngine(
            model, validator, editContext, time,
            liveDebounce: TimeSpan.FromMilliseconds(100), hideCapability: hideCapability);

        Assert.False((await engine.ValidateForSubmitAsync()).CanProceed);

        model.Nickname = "even longer and still far too long";
        editContext.NotifyFieldChanged(new FieldIdentifier(model, nameof(ReuseModel.Nickname)));

        time.Advance(TimeSpan.FromMilliseconds(100)); // the live window closes
        time.Advance(TimeSpan.FromMilliseconds(200)); // the refresh follows

        var issues = engine.GetVisibleIssues()
            .Select(v => (v.Issue.Path, v.Issue.Message))
            .OrderBy(x => x.Path, StringComparer.Ordinal).ThenBy(x => x.Message, StringComparer.Ordinal)
            .ToList();

        return (issues, (validator.DraftRuns, validator.SubmitOnlyRuns, validator.DualRuns));
    }
}
