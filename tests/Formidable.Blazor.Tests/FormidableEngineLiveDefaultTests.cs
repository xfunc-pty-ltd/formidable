using FluentValidation;
using Formidable.Introspection;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Time.Testing;

namespace Formidable.Blazor.Tests;

/// <summary>
/// The live channel's default rule selection: whatever the submit profile selects. A field the
/// user has engaged discloses anything that would block a submit — a required field among it —
/// while the engaged set alone decides which FIELDS may speak, so a form still says nothing about
/// what nobody has touched. <see cref="FormidableOptions.LiveProfile"/> stays the explicit
/// narrowing for a submit rule too expensive to run per keystroke, and it tracks
/// <see cref="FormidableOptions.SubmitProfile"/> by reference rather than by name, which is what
/// keeps the verdict store's freshness check and the coverage cache serving.
/// </summary>
public class FormidableEngineLiveDefaultTests
{
    private sealed class LiveDefaultAttendee
    {
        public string Name { get; set; } = string.Empty;
    }

    private sealed class LiveDefaultModel
    {
        public string Title { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;
        public List<LiveDefaultAttendee> Attendees { get; set; } = [];
    }

    /// <summary>
    /// A plain two-bucket validator with nothing engagement-shaped about it: the draft bucket
    /// carries a length rule, and the submit bucket carries the presence rules a submit enforces.
    /// <see cref="LiveDefaultModel.Title"/> is the field a test engages,
    /// <see cref="LiveDefaultModel.Summary"/> the one it never touches, and the attendee row rule
    /// is there for the collection case — where the notification names the collection and never
    /// the row.
    /// </summary>
    private sealed class LiveDefaultValidator : DraftSubmitValidator<LiveDefaultModel>
    {
        protected override void ConfigureDraftRules() =>
            RuleFor(x => x.Title).MaximumLength(10).WithMessage("Title is too long");

        protected override void ConfigureSubmitRules()
        {
            RuleFor(x => x.Title).NotEmpty().WithMessage("Title is required");
            RuleFor(x => x.Summary).NotEmpty().WithMessage("Summary is required");
            RuleForEach(x => x.Attendees).ChildRules(attendee =>
                attendee.RuleFor(a => a.Name).NotEmpty().WithMessage("Name is required"));
        }
    }

    private static FormidableEngine<LiveDefaultModel> CreateEngine(
        LiveDefaultModel model, EditContext editContext, FormidableOptions options, TimeProvider time) =>
        new(
            model,
            editContext,
            new FluentValidationModelValidator<LiveDefaultModel>(new LiveDefaultValidator()),
            new ReflectionModelIntrospector(),
            options,
            time);

    private static FieldIdentifier Title(LiveDefaultModel model) =>
        new(model, nameof(LiveDefaultModel.Title));

    private static FieldIdentifier Summary(LiveDefaultModel model) =>
        new(model, nameof(LiveDefaultModel.Summary));

    // Mutation this breaks: restoring ValidationProfile.Draft as LiveProfile's default. The
    // engaged field's required rule then never runs live, and a visitor who fills the field in
    // and empties it again hears nothing until a submit is blocked.
    [Fact]
    public void An_engaged_then_emptied_required_field_discloses_with_no_submit()
    {
        var model = new LiveDefaultModel();
        var editContext = new EditContext(model);
        using var engine = CreateEngine(model, editContext, new FormidableOptions(), new FakeTimeProvider());
        var title = Title(model);

        model.Title = "Sprint";
        editContext.NotifyFieldChanged(title);
        Assert.Empty(engine.GetIssues(title)); // engaged, and passing

        model.Title = string.Empty;
        editContext.NotifyFieldChanged(title);

        Assert.Contains(engine.GetIssues(title), issue => issue.Message == "Title is required");
        Assert.Contains(engine.GetVisibleIssues(), visible => visible.Issue.Message == "Title is required");
        Assert.Contains("Title is required", editContext.GetValidationMessages(title));
        Assert.False(engine.HasSubmitted);
    }

    // Mutation this breaks: writing the whole live report to the fields it names rather than to
    // the engaged set alone. Summary's own NotEmpty fails from the first read onwards and the
    // live pass genuinely reports it — engagement is the only thing keeping it off the screen.
    [Fact]
    public void A_never_engaged_required_field_stays_silent()
    {
        var model = new LiveDefaultModel();
        var editContext = new EditContext(model);
        using var engine = CreateEngine(model, editContext, new FormidableOptions(), new FakeTimeProvider());
        var title = Title(model);
        var summary = Summary(model);

        Assert.Empty(engine.GetIssues(summary));

        model.Title = "Sprint";
        editContext.NotifyFieldChanged(title);
        Assert.Empty(engine.GetIssues(summary));

        model.Title = string.Empty;
        editContext.NotifyFieldChanged(title);

        // The neighbouring field's disclosure is what makes the silence meaningful: the pass ran,
        // it answered for the whole model, and Summary still says nothing on any surface.
        Assert.Contains(engine.GetIssues(title), issue => issue.Message == "Title is required");
        Assert.Empty(engine.GetIssues(summary));
        Assert.Empty(editContext.GetValidationMessages(summary));
        Assert.DoesNotContain(engine.GetVisibleIssues(), visible => visible.Issue.Message == "Summary is required");
    }

    // The collection shape of the same rule, with a row of each kind so the silence is not
    // vacuous. Mirrors "Add attendee": the notification names the collection field, never the new
    // row's own Name field, so that row's failing presence rule — part of the very report the
    // engaged row's message comes out of — lands on nothing.
    [Fact]
    public void A_never_notified_row_field_gets_no_live_verdict()
    {
        var model = new LiveDefaultModel();
        var engaged = new LiveDefaultAttendee();
        var added = new LiveDefaultAttendee(); // blank Name - the row rule already fails
        model.Attendees.Add(engaged);
        model.Attendees.Add(added);
        var editContext = new EditContext(model);
        using var engine = CreateEngine(model, editContext, new FormidableOptions(), new FakeTimeProvider());
        var engagedName = new FieldIdentifier(engaged, nameof(LiveDefaultAttendee.Name));
        var addedName = new FieldIdentifier(added, nameof(LiveDefaultAttendee.Name));

        engaged.Name = "Ada";
        editContext.NotifyFieldChanged(engagedName);
        engaged.Name = string.Empty;
        editContext.NotifyFieldChanged(engagedName);

        editContext.NotifyFieldChanged(new FieldIdentifier(model, nameof(LiveDefaultModel.Attendees)));

        // The engaged row is the positive control: the pass ran, the row rule executed, and the
        // report covered every row. Only engagement decides which of them speaks.
        Assert.Contains(engine.GetIssues(engagedName), issue => issue.Message == "Name is required");
        Assert.Empty(engine.GetIssues(addedName));
        Assert.Empty(editContext.GetValidationMessages(addedName));
    }

    // Mutation this breaks: letting the live channel's selection reach the Draft profile itself.
    // Draft answers "is this value malformed", and a half-finished model is exactly what it has
    // to accept — a save-progress button asks the profile directly, and what the live channel
    // discloses beside it changes nothing about that answer.
    [Fact]
    public async Task A_direct_draft_validate_stays_clean_while_live_discloses_the_required_rule()
    {
        var validator = new LiveDefaultValidator();
        var model = new LiveDefaultModel();
        model.Attendees.Add(new LiveDefaultAttendee());
        var editContext = new EditContext(model);
        using var engine = new FormidableEngine<LiveDefaultModel>(
            model,
            editContext,
            new FluentValidationModelValidator<LiveDefaultModel>(validator),
            new ReflectionModelIntrospector(),
            new FormidableOptions(),
            new FakeTimeProvider());
        var title = Title(model);

        model.Title = "Sprint";
        editContext.NotifyFieldChanged(title);
        model.Title = string.Empty;
        editContext.NotifyFieldChanged(title);
        Assert.Contains(engine.GetIssues(title), issue => issue.Message == "Title is required");

        // Every required field is empty and the draft profile is satisfied regardless.
        var draft = await validator.ValidateAsync(model, ValidationProfile.Draft);

        Assert.True(draft.IsValid);
    }

    // Mutation this breaks: ignoring an explicit narrowing — resolving the submit profile
    // whatever the option holds. Setting LiveProfile is how a form keeps an expensive submit rule
    // off the per-keystroke path, and it has to hold on every surface the live channel reaches.
    [Fact]
    public async Task An_explicit_live_profile_narrows_live_evaluation_on_every_surface()
    {
        var model = new LiveDefaultModel();
        var editContext = new EditContext(model);
        using var engine = CreateEngine(
            model,
            editContext,
            new FormidableOptions
            {
                LiveProfile = ValidationProfile.Draft,
                // Nothing registers a field in a bare-engine test, so submit-time visibility
                // would suppress the error the second half looks for. The live channel is not
                // filtered by this at all, so the first half reads what a rendered page reads.
                DisclosureOverride = _ => true,
            },
            new FakeTimeProvider());
        var title = Title(model);

        model.Title = "Sprint";
        editContext.NotifyFieldChanged(title);
        model.Title = string.Empty;
        editContext.NotifyFieldChanged(title);

        Assert.Empty(engine.GetIssues(title));
        Assert.Empty(engine.GetVisibleIssues());
        Assert.Empty(editContext.GetValidationMessages(title));

        // The rule is still enforced where it was declared.
        Assert.False((await engine.ValidateForSubmitAsync()).CanProceed);
        Assert.Contains("Title is required", editContext.GetValidationMessages(title));
    }

    // ---------------------------------------------------------------------------------------
    // Tracking a custom submit profile, by reference
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Selects the <c>"Review"</c> ruleset and nothing else — no default rules, and not the
    /// <c>"Submit"</c> ruleset the shipped <see cref="ValidationProfile.Submit"/> names. A live
    /// pass under it therefore produces a message no other profile could have produced, which is
    /// what separates "followed the configured instance" from "ran the Submit static".
    /// </summary>
    private static readonly ValidationProfile ReviewProfile =
        ValidationProfile.Named("Review", includeDefaultRules: false, "Review");

    /// <summary>
    /// One rule per bucket, each with a message of its own, plus a counted rule in the
    /// <c>"Review"</c> ruleset. The counter is what tells a store-served verdict from a second
    /// execution: running a rule twice leaves exactly the issues running it once leaves.
    /// </summary>
    private sealed class ReviewValidator : DraftSubmitValidator<LiveDefaultModel>
    {
        public int ReviewRuleRuns;

        protected override void ConfigureDraftRules() =>
            RuleFor(x => x.Summary).MaximumLength(4).WithMessage("Summary is too long");

        protected override void ConfigureSubmitRules() =>
            RuleFor(x => x.Title).NotEmpty().WithMessage("Title is required");

        protected override void ConfigureAdditionalProfiles() =>
            Profile("Review", () =>
                RuleFor(x => x.Title)
                    .Must(title =>
                    {
                        ReviewRuleRuns++;
                        return title.Contains("v2", StringComparison.Ordinal);
                    })
                    .WithMessage("Title needs a version"));
    }

    /// <summary>
    /// Forwards an inner validator whole, recording the profile INSTANCE each rule selection was
    /// handed. Reference identity is the property under test, so a wrapper that compared names
    /// would pin nothing — and the rule-level capability has to survive the wrapping, or the
    /// engine would take the whole-profile fallback and the reuse half would prove nothing
    /// either.
    /// </summary>
    private sealed class ProfileRecordingValidator<TModel>(IRuleLevelValidator<TModel> inner)
        : IModelValidator<TModel>, IRuleLevelValidator<TModel>
        where TModel : class
    {
        public List<ValidationProfile> Selections { get; } = [];

        public bool CanValidateByRule => inner.CanValidateByRule;

        public IReadOnlyList<RuleIdentity> SelectRules(ValidationProfile profile)
        {
            Selections.Add(profile);
            return inner.SelectRules(profile);
        }

        public Task<RuleLevelResult> ValidateRulesAsync(
            TModel model, ValidationProfile profile, IReadOnlyList<RuleIdentity> rules,
            CancellationToken cancellationToken = default) =>
            inner.ValidateRulesAsync(model, profile, rules, cancellationToken);

        public IReadOnlyList<IReadOnlyList<RuleIdentity>> GroupBySelectionClass(IReadOnlyList<RuleIdentity> rules) =>
            inner.GroupBySelectionClass(rules);

        public Task<ValidationReport> ValidateAsync(
            TModel model, ValidationProfile profile, CancellationToken cancellationToken = default) =>
            ((IModelValidator<TModel>)inner).ValidateAsync(model, profile, cancellationToken);

        public ValidationReport Validate(TModel model, ValidationProfile profile) =>
            ((IModelValidator<TModel>)inner).Validate(model, profile);
    }

    // Mutation this breaks: resolving the live profile by composing a fresh instance, or by
    // naming ValidationProfile.Submit. The first is caught by reference identity — and it is not
    // a cosmetic difference, since the verdict store's freshness check and the coverage cache are
    // both keyed on the instance. The second is caught by the message: only the configured
    // profile selects the "Review" ruleset.
    [Fact]
    public void The_live_channel_follows_the_configured_submit_profile_instance()
    {
        var model = new LiveDefaultModel { Summary = "brief" };
        var validator = new ReviewValidator();
        var editContext = new EditContext(model);
        var recording = new ProfileRecordingValidator<LiveDefaultModel>(
            new FluentValidationModelValidator<LiveDefaultModel>(validator));
        using var engine = new FormidableEngine<LiveDefaultModel>(
            model,
            editContext,
            recording,
            new ReflectionModelIntrospector(),
            new FormidableOptions { SubmitProfile = ReviewProfile, TrackFormValidity = true },
            new FakeTimeProvider());
        var title = Title(model);

        model.Title = "Sprint";
        editContext.NotifyFieldChanged(title);

        // Every selection the engine asked for named the very object the options hold — not an
        // equal one, and not the shipped static.
        Assert.NotEmpty(recording.Selections);
        Assert.All(recording.Selections, profile => Assert.Same(ReviewProfile, profile));

        // The "Review" ruleset's rule answered the live pass; neither the "Submit" ruleset's
        // rule nor the default-rule bucket did, so the selection is the configured one throughout.
        Assert.Contains(engine.GetIssues(title), issue => issue.Message == "Title needs a version");
        Assert.DoesNotContain(engine.GetIssues(title), issue => issue.Message == "Title is required");
        Assert.DoesNotContain(
            engine.GetVisibleIssues(), visible => visible.Issue.Message == "Summary is too long");

        // The reuse half: the edit's validity probe and its live pass run the same profile at the
        // same edit stamp, so between them the rule executes once. This rule's verdict is not
        // profile-scoped, so its freshness turns on rule identity and stamp alone and the count
        // survives a resolution handing out equal-but-distinct instances: what catches that here
        // is the identity assertion above, and
        // A_profile_scoped_verdict_is_shared_by_the_probe_and_the_live_pass_at_one_stamp below
        // carries the execution cost it would bring.
        Assert.Equal(2, validator.ReviewRuleRuns); // one at construction, one for this edit
    }

    // ---------------------------------------------------------------------------------------
    // Reference-keyed reuse, where it is observable
    // ---------------------------------------------------------------------------------------

    private sealed class ScopedVerdictSlot
    {
        public string Name { get; set; } = string.Empty;
        public string Special { get; set; } = string.Empty;
    }

    private sealed class ScopedVerdictModel
    {
        public string Title { get; set; } = string.Empty;
        public List<ScopedVerdictSlot> Slots { get; set; } = [];
    }

    /// <summary>
    /// A child validator carrying its OWN ruleset axis: the untagged name rule rides the default
    /// bucket and the <c>"Special"</c> rule runs only under a profile naming it. Attached with
    /// <c>SetValidator</c>, its rules are filtered by the consuming profile's name list — which
    /// is what makes the parent rule's verdict profile-SCOPED, the one shape whose reuse turns
    /// on the profile reference rather than on rule identity and edit stamp alone.
    /// </summary>
    private sealed class ScopedSlotValidator : AbstractValidator<ScopedVerdictSlot>
    {
        public ScopedSlotValidator(Action onNameRule)
        {
            RuleFor(s => s.Name)
                .Must(name =>
                {
                    onNameRule();
                    return name.Length > 0;
                })
                .WithMessage("Slot name is required");
            RuleSet("Special", () =>
                RuleFor(s => s.Special).NotEmpty().WithMessage("Slot special is required"));
        }
    }

    private sealed class ScopedVerdictValidator : DraftSubmitValidator<ScopedVerdictModel>
    {
        public int SlotNameRuleRuns;

        protected override void ConfigureDraftRules()
        {
        }

        protected override void ConfigureSubmitRules()
        {
            RuleFor(x => x.Title).NotEmpty().WithMessage("Title is required");
            RuleForEach(x => x.Slots).SetValidator(new ScopedSlotValidator(() => SlotNameRuleRuns++));
        }
    }

    // The reference contract's one observable consequence, and the reason resolution must hand
    // back the stored instance rather than an equal copy. A profile-scoped verdict is served only
    // to a pass running the very profile object it was filed under, so the two passes one edit
    // arms — its live pass and its validity probe, at one edit stamp — share a single execution
    // exactly while the live channel resolves to the submit profile BY REFERENCE.
    // Mutation this breaks: resolving to a composed profile of the same name and selection. Rule
    // identity and edit stamp still match, so every other pin here survives it; this one does not,
    // because the freshness check falls through to the reference comparison and the second pass
    // re-executes the rule.
    [Fact]
    public void A_profile_scoped_verdict_is_shared_by_the_probe_and_the_live_pass_at_one_stamp()
    {
        var model = new ScopedVerdictModel { Title = "Sprint" };
        model.Slots.Add(new ScopedVerdictSlot { Name = "Morning" });
        var validator = new ScopedVerdictValidator();
        var editContext = new EditContext(model);
        using var engine = new FormidableEngine<ScopedVerdictModel>(
            model,
            editContext,
            new FluentValidationModelValidator<ScopedVerdictModel>(validator),
            new ReflectionModelIntrospector(),
            new FormidableOptions { TrackFormValidity = true },
            new FakeTimeProvider());

        // The construction probe answers a pristine store, which is also the control that the
        // rule is selected and does execute.
        Assert.Equal(1, validator.SlotNameRuleRuns);

        model.Title = "Sprint plan";
        editContext.NotifyFieldChanged(new FieldIdentifier(model, nameof(ScopedVerdictModel.Title)));

        // One more execution for the edit, not two: whichever of the pair gets there first files
        // the verdict, and the other is handed it.
        Assert.Equal(2, validator.SlotNameRuleRuns);
    }

    // ---------------------------------------------------------------------------------------
    // Honest valid, reached live
    // ---------------------------------------------------------------------------------------

    // Mutation this breaks: keeping submit coverage keyed to submit and probe passes only. The
    // Valid class asks whether the field's submit-selected rules have answered, cleanly, for the
    // model as it stands, and a live pass answers exactly that — so green is reachable on a page
    // that neither tracks validity nor has ever been submitted.
    [Fact]
    public void An_engaged_error_free_field_earns_green_from_a_live_pass_alone()
    {
        var model = new LiveDefaultModel { Summary = "Ready" };
        model.Attendees.Add(new LiveDefaultAttendee { Name = "Ada" });
        var editContext = new EditContext(model);
        using var engine = CreateEngine(model, editContext, new FormidableOptions(), new FakeTimeProvider());
        var title = Title(model);
        using var registration = engine.Registry.Register(title);

        model.Title = "Sprint";
        editContext.NotifyFieldChanged(title);

        Assert.False(engine.HasSubmitted);
        Assert.False(engine.Options.TrackFormValidity);
        Assert.Equal("formidable-valid", editContext.FieldCssClass(title));
        Assert.Equal(
            "formidable-valid",
            FormidableCss.Compute(engine.GetFieldState(title), engine.Options.CssClasses));
    }
}
