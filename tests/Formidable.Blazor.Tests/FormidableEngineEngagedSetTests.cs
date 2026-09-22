using FluentValidation;
using Formidable.Introspection;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Time.Testing;

namespace Formidable.Blazor.Tests;

/// <summary>
/// Pins verdict application over the engaged field set: a live pass answers every field the user
/// has committed a change to — the report's issues where it has them, the empty verdict where it
/// says nothing — so a cross-field verdict clears, or appears, on a field the triggering edit
/// never named. The fixture is a two-field cross-field rule in the DRAFT bucket, which a live
/// pass selects under any profile here — the submit bucket is empty, so nothing turns on which
/// one the live channel runs.
/// </summary>
public class FormidableEngineEngagedSetTests
{
    private const string DeadlineMessage = "The deadline must not fall after the event day";

    private sealed class CrossFieldSchedule
    {
        public int EventDay { get; set; }
        public int Deadline { get; set; }
    }

    /// <summary>
    /// One cross-field rule in the draft bucket: the deadline must not fall after the event day.
    /// Cross-field on purpose — an edit to EITHER property moves the verdict that lands on
    /// <see cref="CrossFieldSchedule.Deadline"/>, which is the shape a verdict written only for
    /// the edited field cannot answer correctly.
    /// </summary>
    private sealed class CrossFieldScheduleValidator : DraftSubmitValidator<CrossFieldSchedule>
    {
        protected override void ConfigureDraftRules() =>
            RuleFor(x => x.Deadline)
                .Must((schedule, deadline) => deadline <= schedule.EventDay)
                .WithMessage(DeadlineMessage);

        protected override void ConfigureSubmitRules()
        {
        }
    }

    /// <summary>
    /// The same cross-field rule, made asynchronous with its verdict computed BEFORE it blocks on
    /// <see cref="Gate"/> — the answer is the model's state as the pass reads it, however long the
    /// pass is then held in flight, which is what stages a report that predates a mid-pass edit.
    /// <see cref="Started"/> counts rule invocations, so a test can prove which pass answered.
    /// </summary>
    private sealed class GatedCrossFieldScheduleValidator : DraftSubmitValidator<CrossFieldSchedule>
    {
        public TaskCompletionSource Gate { get; } = new();
        public int Started;

        protected override void ConfigureDraftRules() =>
            RuleFor(x => x.Deadline)
                .MustAsync(async (schedule, deadline, ct) =>
                {
                    Started++;
                    var verdict = deadline <= schedule.EventDay;
                    await Gate.Task.WaitAsync(ct);
                    return verdict;
                })
                .WithMessage(DeadlineMessage);

        protected override void ConfigureSubmitRules()
        {
        }
    }

    [Fact]
    public void Fixing_a_cross_field_error_from_the_other_field_clears_it_live()
    {
        // Probe E7's shape: break the pair from Deadline, fix it from EventDay. Mutation that must
        // break it: scoping the verdict apply to the fields the pass was told changed — the fixing
        // pass then writes only EventDay's entry, and Deadline keeps reporting an error a direct
        // validate of the model disproves.
        var model = new CrossFieldSchedule { EventDay = 10, Deadline = 20 };
        var editContext = new EditContext(model);
        using var engine = Build(model, editContext, new CrossFieldScheduleValidator(), new FormidableOptions());

        var deadline = new FieldIdentifier(model, nameof(CrossFieldSchedule.Deadline));
        var eventDay = new FieldIdentifier(model, nameof(CrossFieldSchedule.EventDay));

        // Engage Deadline with the pair violating: the error discloses on Deadline.
        editContext.NotifyFieldChanged(deadline);

        Assert.Contains(engine.GetIssues(deadline), i => i.Message == DeadlineMessage);
        Assert.Contains(engine.GetVisibleIssues(), v => v.Issue.Message == DeadlineMessage);
        Assert.Contains(DeadlineMessage, editContext.GetValidationMessages(deadline));

        // Fix the pair from the OTHER field. The live pass validates the whole model — clean —
        // and Deadline is engaged, so its verdict is re-answered to the empty one.
        model.EventDay = 30;
        editContext.NotifyFieldChanged(eventDay);

        Assert.Empty(engine.GetIssues(deadline));
        Assert.DoesNotContain(engine.GetVisibleIssues(), v => v.Issue.Message == DeadlineMessage);
        Assert.Empty(editContext.GetValidationMessages(deadline));
    }

    [Fact]
    public void MarkTouched_does_not_engage_the_field_for_a_later_live_pass()
    {
        // This test's mirror image: MarkTouched marks a field touched — CSS disclosure a
        // component may grant on a bare blur — without adding it to the set the live channel
        // answers. Mutation that must break it: engaging inside MarkTouched — the pass below,
        // started from EventDay alone, would then land Deadline's still-violating verdict on a
        // field nothing ever notified a change for.
        var model = new CrossFieldSchedule { EventDay = 10, Deadline = 20 };
        var editContext = new EditContext(model);
        using var engine = Build(
            model, editContext, new CrossFieldScheduleValidator(), new FormidableOptions());

        var deadline = new FieldIdentifier(model, nameof(CrossFieldSchedule.Deadline));
        var eventDay = new FieldIdentifier(model, nameof(CrossFieldSchedule.EventDay));

        engine.MarkTouched(deadline);
        Assert.True(engine.GetFieldState(deadline).IsTouched);

        // A live pass from the OTHER field validates the whole model — the pair still
        // violates — but Deadline was only touched a moment ago, never engaged.
        editContext.NotifyFieldChanged(eventDay);

        Assert.Empty(engine.GetIssues(deadline));
        Assert.DoesNotContain(engine.GetVisibleIssues(), v => v.Issue.Message == DeadlineMessage);
        Assert.Empty(editContext.GetValidationMessages(deadline));
    }

    [Fact]
    public async Task A_post_submit_edit_re_answers_every_engaged_field()
    {
        // The E7 sequence with HasSubmitted true, plus the appear-on-break half. Mutation that
        // must break it: clearing the engaged set at submit — no post-submit edit ever names
        // Deadline, so a cleared set leaves the breaking pass at the end answering EventDay
        // alone and Deadline's live verdict never appears.
        var model = new CrossFieldSchedule { EventDay = 10, Deadline = 20 };
        var time = new FakeTimeProvider();
        var editContext = new EditContext(model);
        using var engine = Build(
            model, editContext, new CrossFieldScheduleValidator(),
            // No component registers a field in a bare-engine test, so submit-time visibility
            // (render-registration) would suppress the blocked submit's error behind the
            // defensive gate; forcing disclosure open lets the submit channel carry the verdict
            // a rendered page would show. The live channel is never filtered by this at all.
            new FormidableOptions { DisclosureOverride = _ => true },
            time);

        var deadline = new FieldIdentifier(model, nameof(CrossFieldSchedule.Deadline));
        var eventDay = new FieldIdentifier(model, nameof(CrossFieldSchedule.EventDay));

        // Engage both fields, then submit with the pair violating: the submit blocks and the
        // submit channel takes the Deadline error over from the live channel.
        editContext.NotifyFieldChanged(deadline);
        editContext.NotifyFieldChanged(eventDay);
        Assert.False((await engine.ValidateForSubmitAsync()).CanProceed);
        Assert.True(engine.HasSubmitted);
        Assert.Contains(DeadlineMessage, editContext.GetValidationMessages(deadline));

        // Fix the pair from the other field: the live pass that edit starts re-answers every
        // engaged field and, running the default submit-profile LiveProfile, rebuilds the submit
        // channel's own source too — so both channels end clean for Deadline at that pass. The
        // refresh armed below lands after and finds the same clean answer already standing.
        model.EventDay = 30;
        editContext.NotifyFieldChanged(eventDay);
        time.Advance(TimeSpan.FromMilliseconds(301));

        Assert.Empty(engine.GetIssues(deadline));
        Assert.DoesNotContain(engine.GetVisibleIssues(), v => v.Issue.Message == DeadlineMessage);
        Assert.Empty(editContext.GetValidationMessages(deadline));

        // Break the pair from the other field again: the error must land on still-engaged
        // Deadline through the LIVE channel, before any refresh window closes — the read that
        // tells an engagement that survived the submit apart from one the submit discarded.
        model.EventDay = 1;
        editContext.NotifyFieldChanged(eventDay);

        Assert.Contains(engine.GetIssues(deadline), i => i.Message == DeadlineMessage);
        Assert.Contains(engine.GetVisibleIssues(), v => v.Issue.Message == DeadlineMessage);
        Assert.Contains(DeadlineMessage, editContext.GetValidationMessages(deadline));
    }

    [Fact]
    public async Task A_field_engaged_during_an_open_window_is_not_answered_from_the_older_pass()
    {
        // Mutation that must break it: applying the verdict over the engaged set as it stands at
        // apply time instead of intersecting it with the pass-begin snapshot — the held pass then
        // writes Deadline the stale violation its report computed before the fixing edit landed.
        var model = new CrossFieldSchedule { EventDay = 10, Deadline = 20 };
        var validator = new GatedCrossFieldScheduleValidator();
        var time = new FakeTimeProvider();
        var editContext = new EditContext(model);
        using var engine = Build(
            model, editContext, validator,
            new FormidableOptions { LiveDebounce = TimeSpan.FromMilliseconds(400) },
            time);

        var deadline = new FieldIdentifier(model, nameof(CrossFieldSchedule.Deadline));
        var eventDay = new FieldIdentifier(model, nameof(CrossFieldSchedule.EventDay));

        // Engage EventDay; its window closes into a pass held in flight on the gate, the verdict
        // for the pair as it stands — violating — already computed.
        editContext.NotifyFieldChanged(eventDay);
        time.Advance(TimeSpan.FromMilliseconds(400));
        Assert.True(engine.IsValidating);
        Assert.Equal(1, validator.Started);

        // Mid-flight: fix the pair AND engage Deadline for the first time. The edit opens a new
        // debounce window; the pass in flight predates the edit and must not answer for it.
        model.Deadline = 5;
        editContext.NotifyFieldChanged(deadline);

        var settled = Quiescence(engine);
        validator.Gate.SetResult();
        await settled;

        // The held pass's report says the pair is violating — the state it read — but Deadline
        // was not engaged when that pass began, so no entry lands on it from that report.
        Assert.Empty(engine.GetIssues(deadline));
        Assert.DoesNotContain(engine.GetVisibleIssues(), v => v.Issue.Message == DeadlineMessage);
        Assert.Empty(editContext.GetValidationMessages(deadline));
        Assert.Equal(1, validator.Started);

        // Deadline's own window fire answers it: a second pass runs against the fixed pair, and
        // the verdict stays clean everywhere.
        var secondSettled = Quiescence(engine);
        time.Advance(TimeSpan.FromMilliseconds(400));
        await secondSettled;

        Assert.Equal(2, validator.Started);
        Assert.False(engine.IsValidating);
        Assert.Empty(engine.GetIssues(deadline));
        Assert.Empty(engine.GetIssues(eventDay));
    }

    /// <summary>
    /// Completes the next time the engine reports it is no longer validating. Call it only once
    /// the pass under test is confirmed in flight — StateChanged also fires before a pass flips
    /// IsValidating true (MarkTouched does), which would resolve quiescence prematurely.
    /// </summary>
    private static Task Quiescence(FormidableEngine<CrossFieldSchedule> engine)
    {
        var quiescent = new TaskCompletionSource();
        engine.StateChanged += (_, _) =>
        {
            if (!engine.IsValidating)
            {
                quiescent.TrySetResult();
            }
        };
        return quiescent.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static FormidableEngine<CrossFieldSchedule> Build(
        CrossFieldSchedule model,
        EditContext editContext,
        IValidator<CrossFieldSchedule> validator,
        FormidableOptions options,
        TimeProvider? time = null) =>
        new(
            model,
            editContext,
            new FluentValidationModelValidator<CrossFieldSchedule>(validator),
            new ReflectionModelIntrospector(),
            options,
            time ?? new FakeTimeProvider());
}
