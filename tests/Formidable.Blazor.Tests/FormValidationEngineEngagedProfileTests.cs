using FluentValidation;
using Formidable.Introspection;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Time.Testing;

namespace Formidable.Blazor.Tests;

/// <summary>
/// Pins the shipped shape of engaged-disclosure: a presence rule is a member of BOTH the plain
/// "Submit" ruleset and a separate "Engaged" ruleset at its own declaration (one FluentValidation
/// <c>RuleSet("Submit,Engaged", ...)</c> call, comma-named), used as <c>LiveProfile</c>, so the
/// rule answers a live pass on engagement without moving into the always-on draft bucket and
/// without turning every OTHER submit-bucket rule live. Mirrors <c>/workout</c>'s own shape exactly
/// — a <c>RuleForEach.ChildRules</c> row rule, comma-membership RuleSet, an empty
/// <c>Profile("Engaged", ...)</c> registering the name for verification, plain
/// <c>ValidationProfile.Submit</c> as <c>SubmitProfile</c> (no composite needed).
/// </summary>
public class FormValidationEngineEngagedProfileTests
{
    // The same LiveProfile /workout's options object builds, by the same name. SubmitProfile is
    // the shipped ValidationProfile.Submit itself - no composite profile exists under this shape.
    private static readonly ValidationProfile EngagedLiveProfile =
        ValidationProfile.Named("Engaged", includeDefaultRules: true, "Engaged");

    private sealed class EngagedAttendee
    {
        public string Name { get; set; } = string.Empty;
    }

    private sealed class EngagedFixtureModel
    {
        public List<EngagedAttendee> Attendees { get; set; } = [];
        public string EventName { get; set; } = string.Empty;
    }

    /// <summary>
    /// Draft/Submit/Engaged buckets. <see cref="EngagedAttendee.Name"/>'s presence rule is a
    /// member of BOTH "Submit" and "Engaged" at its own declaration — the same
    /// <c>RuleSet("Submit,Engaged", ...)</c> shape the sample's attendee name rule uses — so a
    /// test can prove one declared rule answers both a live pass and a plain submit.
    /// <see cref="EngagedFixtureModel.EventName"/>'s presence rule lives in "Submit" alone,
    /// mirroring every OTHER presence rule the sample page left untouched, so a test can prove
    /// that bucket still waits for submit. The draft bucket is empty: nothing here needs a
    /// malformed-value rule, only the bucket split.
    /// </summary>
    private sealed class EngagedFixtureValidator : DraftSubmitValidator<EngagedFixtureModel>
    {
        protected override void ConfigureDraftRules()
        {
        }

        protected override void ConfigureSubmitRules() =>
            RuleFor(x => x.EventName).NotEmpty().WithMessage("Event name is required");

        // Profile(name, rules) registers ruleset-name verification under the exact string it is
        // given, so it cannot express comma-membership itself ("Submit,Engaged" would register as
        // its own wrong name) - the shared rule uses the raw RuleSet call instead. The empty
        // Profile("Engaged", ...) alongside it exists only to register "Engaged" for that same
        // verification, so a LiveProfile naming an unregistered ruleset still throws loudly.
        protected override void ConfigureAdditionalProfiles()
        {
            Profile("Engaged", () => { });
            RuleSet("Submit,Engaged", () =>
                RuleForEach(x => x.Attendees).ChildRules(attendee =>
                    attendee.RuleFor(a => a.Name).NotEmpty().WithMessage("Name is required")));
        }
    }

    private static FormValidationEngine<EngagedFixtureModel> CreateEngine(
        EngagedFixtureModel model, EditContext editContext, TimeProvider time) =>
        new(
            model, editContext,
            new FluentValidationModelValidator<EngagedFixtureModel>(new EngagedFixtureValidator()),
            new ReflectionModelIntrospector(),
            new FormidableOptions
            {
                LiveProfile = EngagedLiveProfile,
                SubmitProfile = ValidationProfile.Submit,
                // No component ever registers a field in these bare-engine tests, so submit-time
                // visibility (render-registration) would suppress every error outright; forcing it
                // open is what lets A_submit_bucket_rule_still_waits_for_submit see the verdict a
                // real, rendered page would. The live channel is never filtered by this at all, so
                // it changes nothing for the other tests here.
                DisclosureOverride = _ => true,
            },
            time);

    [Fact]
    public void A_never_notified_field_gets_no_live_verdict_under_the_engaged_profile()
    {
        var model = new EngagedFixtureModel();
        var attendee = new EngagedAttendee(); // blank Name - the shared rule already fails
        model.Attendees.Add(attendee);
        var editContext = new EditContext(model);
        using var engine = CreateEngine(model, editContext, new FakeTimeProvider());

        var attendeesField = new FieldIdentifier(model, nameof(EngagedFixtureModel.Attendees));
        var nameField = new FieldIdentifier(attendee, nameof(EngagedAttendee.Name));

        // Mirrors "Add attendee": the collection field is what gets notified, never the row's own
        // Name field. The live pass that follows validates the whole model under the Engaged
        // profile - the row's Name rule genuinely fails as part of that report - but a verdict
        // lands only on engaged fields, and a field no notification has ever named is not one of
        // them, so the row stays silent.
        editContext.NotifyFieldChanged(attendeesField);

        Assert.Empty(engine.GetIssues(nameField));
        Assert.Empty(editContext.GetValidationMessages(nameField));
    }

    [Fact]
    public void Engaging_then_clearing_discloses_with_no_submit()
    {
        var model = new EngagedFixtureModel();
        var attendee = new EngagedAttendee();
        model.Attendees.Add(attendee);
        var editContext = new EditContext(model);
        using var engine = CreateEngine(model, editContext, new FakeTimeProvider());

        var nameField = new FieldIdentifier(attendee, nameof(EngagedAttendee.Name));

        attendee.Name = "Ada";
        editContext.NotifyFieldChanged(nameField);
        Assert.Empty(editContext.GetValidationMessages(nameField)); // engaged, but passing

        attendee.Name = string.Empty;
        editContext.NotifyFieldChanged(nameField);

        Assert.Contains("Name is required", editContext.GetValidationMessages(nameField));
        Assert.False(engine.HasSubmitted);
    }

    [Fact]
    public async Task A_submit_bucket_rule_still_waits_for_submit()
    {
        var model = new EngagedFixtureModel();
        var editContext = new EditContext(model);
        using var engine = CreateEngine(model, editContext, new FakeTimeProvider());

        var eventNameField = new FieldIdentifier(model, nameof(EngagedFixtureModel.EventName));

        // Notified changed while still empty - a live pass runs, but under the Engaged profile,
        // which does not select the plain "Submit" ruleset EventName's rule lives in alone.
        editContext.NotifyFieldChanged(eventNameField);
        Assert.Empty(editContext.GetValidationMessages(eventNameField));

        var outcome = await engine.ValidateForSubmitAsync();

        Assert.False(outcome.CanProceed);
        Assert.Contains("Event name is required", editContext.GetValidationMessages(eventNameField));
    }

    /// <summary>
    /// Comma-membership cuts both ways: the shared rule that answers a live pass under "Engaged"
    /// is a member of "Submit" too, at the same declaration, so a plain submit - naming only
    /// "Submit", nothing composite, nothing mentioning "Engaged" - still reaches it. Wiring a rule
    /// for engagement-only disclosure is additive; it never narrows what a submit enforces.
    /// </summary>
    [Fact]
    public async Task A_plain_submit_still_enforces_the_engaged_rule()
    {
        var model = new EngagedFixtureModel();
        var attendee = new EngagedAttendee(); // blank Name - the shared rule fails
        model.Attendees.Add(attendee);
        var editContext = new EditContext(model);
        using var engine = CreateEngine(model, editContext, new FakeTimeProvider());

        var nameField = new FieldIdentifier(attendee, nameof(EngagedAttendee.Name));

        var outcome = await engine.ValidateForSubmitAsync();

        Assert.False(outcome.CanProceed);
        Assert.Contains("Name is required", editContext.GetValidationMessages(nameField));
    }

    [Fact]
    public async Task A_direct_draft_validate_stays_clean()
    {
        var validator = new EngagedFixtureValidator();
        var model = new EngagedFixtureModel();
        model.Attendees.Add(new EngagedAttendee()); // blank Name - the shared rule fails

        var result = await validator.ValidateAsync(model, ValidationProfile.Draft);

        Assert.True(result.IsValid);
    }

    /// <summary>
    /// The real, verified cost of comma-membership over a dedicated composite SubmitProfile:
    /// <see cref="ProfileDelta"/> is validator-agnostic algebra over two <see cref="ValidationProfile"/>
    /// objects' own declared ruleset-NAME lists. <see cref="ValidationProfile.Submit"/>'s list is
    /// just <c>["Submit"]</c> - it has no way to know that, for this one validator, "Submit" rules
    /// also happen to include whatever a <c>RuleSet("Submit,Engaged", ...)</c> call tagged onto
    /// "Engaged" too. So "Engaged" reads as a ruleset the live profile selects that the submit
    /// profile's own list never names, and the pair is NOT subtractable - the post-submit refresh
    /// on <c>/workout</c> does not reuse the live pass's retained report and instead runs the
    /// whole Submit profile (still correct -
    /// FormValidationEngineProfileSplitTests.A_non_subtractable_profile_pair_runs_the_full_profile
    /// pins that the fallback itself stays safe - just unoptimised).
    /// </summary>
    [Fact]
    public void The_submit_and_engaged_profile_pair_is_not_subtractable()
    {
        var result = ProfileDelta.Compute(ValidationProfile.Submit, EngagedLiveProfile);

        Assert.Equal(ProfileDeltaKind.NotSubtractable, result.Kind);
        Assert.Null(result.Profile);
    }
}
