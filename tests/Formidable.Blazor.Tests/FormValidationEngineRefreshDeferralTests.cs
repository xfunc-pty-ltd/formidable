using Formidable.Blazor.Tests.Fixtures;
using Formidable.Introspection;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Time.Testing;

namespace Formidable.Blazor.Tests;

/// <summary>
/// The refresh's own arm-time computation keeps it from racing an edit's own debounced live pass
/// (see <see cref="FormValidationEngineDebounceOrderTests"/>), but two OTHER sites re-arm the
/// refresh at plain <see cref="FormidableOptions.RefreshDebounce"/> regardless of any live window
/// still open: a field-set change (<c>OnRenderedFieldsChanged</c>) and the refresh's own
/// in-flight-deferral re-arm. Landing inside an open window either way pays for the whole submit
/// profile with nothing yet to reuse, then duplicates the common rules when the live pass answers
/// moments later. These pin that the refresh's deferral covers that case too - and, just as
/// importantly, that it does not over-defer to a window nothing is left in, including a window
/// <see cref="FormidableOptions.LiveDebounce"/> being cleared out from under mid-flight leaves
/// with no timer left that will ever close it on its own.
/// </summary>
public class FormValidationEngineRefreshDeferralTests
{
    [Fact]
    public async Task A_field_set_change_rearm_defers_to_the_open_window()
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

        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));

        // Registered so the field-set change below prunes nothing from the still-open window: the
        // edit's own field is what has to stay in the accumulator for this test to mean anything.
        using var registration = engine.Registry.Register(description);

        // The submit blocks on the empty description, an error site the refresh below keeps
        // current.
        Assert.False((await engine.ValidateForSubmitAsync()).CanProceed);

        order.Description = "Quarterly refresh";
        editContext.NotifyFieldChanged(description);

        var draftBefore = validator.DraftRuleRuns;
        var submitBefore = validator.SubmitRuleRuns;

        time.Advance(TimeSpan.FromMilliseconds(50));

        // A field-set change re-arms the refresh at plain RefreshDebounce from here - due 350 ms
        // after the edit, short of the still-open live window's own 400 ms close.
        engine.OnRenderedFieldsChanged();

        time.Advance(TimeSpan.FromMilliseconds(300)); // 350 ms since the edit: the re-armed refresh comes due

        // The refresh's own re-arm landed inside the still-open live window: it must defer rather
        // than pay for the whole submit profile with nothing yet to reuse.
        Assert.Equal(draftBefore, validator.DraftRuleRuns);
        Assert.Equal(submitBefore, validator.SubmitRuleRuns);

        time.Advance(TimeSpan.FromMilliseconds(50)); // 400 ms since the edit: the live window closes and answers
        time.Advance(TimeSpan.FromMilliseconds(300)); // the deferred refresh's own re-arm comes due and reuses it

        // One edit, one execution of each rule: the live pass ran the draft bucket and the
        // deferred refresh that followed it ran only what the live profile leaves out.
        Assert.Equal(draftBefore + 1, validator.DraftRuleRuns);
        Assert.Equal(submitBefore + 1, validator.SubmitRuleRuns);
    }

    [Fact]
    public async Task A_deferral_rearm_defers_to_the_open_window()
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
                LiveDebounce = TimeSpan.FromMilliseconds(400),
                RefreshDebounce = TimeSpan.FromMilliseconds(300),
                DisclosureOverride = _ => true,
            },
            time);

        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));

        // A long submit - gated on the draft rule the live pass also runs - starts and stays in
        // flight through everything that follows, until released below.
        var submit = engine.ValidateForSubmitAsync();

        // An edit while the submit is still in flight arms both windows from itself: SubmitInFlight
        // is enough to arm the refresh even though HasSubmitted has not flipped true yet.
        order.Description = "Quarterly refresh";
        editContext.NotifyFieldChanged(description);

        time.Advance(TimeSpan.FromMilliseconds(400)); // the live window closes against the in-flight submit

        // Deferred: SubmitInFlight is still true, so the live-debounce fire re-arms itself at
        // plain LiveDebounce (due 800 ms after the edit) rather than starting a pass. The submit's
        // own form-wide indicator is still up, which is what "still in flight" means here.
        Assert.True(engine.GetFieldState(description).IsValidating);

        time.Advance(TimeSpan.FromMilliseconds(50)); // 450 ms since the edit: the refresh comes due against the same in-flight submit

        // Deferred the same way, by the existing SubmitInFlight check: re-armed at plain
        // RefreshDebounce, due 750 ms after the edit - ahead of the live window's own re-armed
        // 800. The submit's own rule reads the model as it stands once the gate releases it,
        // which by then is the edit above - so it drains clean; nothing here turns on that.
        validator.Gate.SetResult();
        await submit;

        var draftBefore = validator.DraftRuleRuns;
        var submitBefore = validator.SubmitRuleRuns;

        time.Advance(TimeSpan.FromMilliseconds(300)); // 750 ms since the edit: the refresh's own re-arm comes due first

        // The submit has drained, so neither SubmitInFlight nor LiveInFlight holds the refresh back
        // any more - but the live window it raced past is still open with the edited field still in
        // its scope, so the refresh must defer to it rather than pay for the whole profile with
        // nothing to reuse.
        Assert.Equal(draftBefore, validator.DraftRuleRuns);
        Assert.Equal(submitBefore, validator.SubmitRuleRuns);

        time.Advance(TimeSpan.FromMilliseconds(50)); // 800 ms since the edit: the live window finally closes and answers

        Assert.Equal(draftBefore + 1, validator.DraftRuleRuns);
        Assert.Equal(submitBefore, validator.SubmitRuleRuns);

        time.Advance(TimeSpan.FromMilliseconds(300)); // the deferred refresh's own re-arm comes due and reuses the live report

        // One edit, one execution of each rule despite two separate deferrals: the live pass ran
        // the draft bucket once and the refresh that finally followed it ran only what the live
        // profile leaves out.
        Assert.Equal(draftBefore + 1, validator.DraftRuleRuns);
        Assert.Equal(submitBefore + 1, validator.SubmitRuleRuns);
    }

    [Fact]
    public async Task A_null_LiveDebounce_at_fire_time_closes_the_window_instead_of_throwing()
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

        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));

        // A long submit stays in flight through everything that follows, until released below -
        // the same shape the deferral test above uses to hold the debounce fire's own deferral
        // branch open.
        var submit = engine.ValidateForSubmitAsync();

        // The edit accumulates into the live-debounce window and arms the refresh at its own
        // margin over that window; SubmitInFlight is enough to arm it even though HasSubmitted
        // has not flipped true yet.
        order.Description = "Quarterly refresh";
        editContext.NotifyFieldChanged(description);

        // Options mutate in place - the sanctioned pattern /async's own checkbox uses - so a
        // consumer can clear LiveDebounce while this window is still open and a submit is still
        // in flight, ahead of the timer that opened the window ever coming due.
        options.LiveDebounce = null;

        // The live-debounce timer fires against the still in-flight submit: the deferral branch
        // must find LiveDebounce null and close the window instead of dereferencing it.
        var fireException = Record.Exception(() => time.Advance(TimeSpan.FromMilliseconds(400)));
        Assert.Null(fireException);

        validator.Gate.SetResult();
        await submit;

        var draftBefore = validator.DraftRuleRuns;
        var submitBefore = validator.SubmitRuleRuns;

        // The refresh the edit armed comes due; the live window it would otherwise have deferred
        // to is closed for good — nothing is left that will ever re-arm it — so the refresh must
        // run rather than wait on a fire that will never come again.
        time.Advance(TimeSpan.FromMilliseconds(50)); // 450 ms since the edit

        Assert.Equal(draftBefore + 1, validator.DraftRuleRuns);
        Assert.Equal(submitBefore + 1, validator.SubmitRuleRuns);
    }

    [Fact]
    public async Task An_all_departed_window_does_not_hold_the_refresh()
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

        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));
        var registration = engine.Registry.Register(description);

        // The submit blocks on the empty description, an error site the refresh below keeps
        // current.
        Assert.False((await engine.ValidateForSubmitAsync()).CanProceed);

        order.Description = "Quarterly refresh";
        editContext.NotifyFieldChanged(description);

        var draftBefore = validator.DraftRuleRuns;
        var submitBefore = validator.SubmitRuleRuns;

        time.Advance(TimeSpan.FromMilliseconds(50));

        // The field the window opened for leaves the page, emptying the accumulator, and the
        // field-set change re-arms the refresh at plain RefreshDebounce - due 350 ms after the
        // edit, short of the still-armed live timer's own 400 ms due time.
        registration.Dispose();
        engine.OnRenderedFieldsChanged();

        time.Advance(TimeSpan.FromMilliseconds(300)); // 350 ms since the edit: the refresh comes due

        // The live timer is still armed, but its window has nothing left in scope: an empty window
        // must not hold up a refresh that is otherwise free to run, so it pays for the whole
        // profile (no live report was ever retained to reuse) rather than waiting on a window with
        // nothing coming from it.
        Assert.Equal(draftBefore + 1, validator.DraftRuleRuns);
        Assert.Equal(submitBefore + 1, validator.SubmitRuleRuns);

        time.Advance(TimeSpan.FromMilliseconds(50)); // 400 ms since the edit: the live window's own no-op fire

        // The window's own fire finds nothing left to answer for and starts no further pass.
        Assert.Equal(draftBefore + 1, validator.DraftRuleRuns);
        Assert.Equal(submitBefore + 1, validator.SubmitRuleRuns);
        Assert.False(engine.IsValidating);

        // The form settles: the refresh's full-profile run already sees the filled-in description.
        Assert.DoesNotContain(
            engine.GetVisibleIssues(), v => v.Issue.Message == RuleRunCountingValidator.SubmitMessage);
    }
}
