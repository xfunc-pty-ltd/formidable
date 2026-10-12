using FluentValidation;
using Formidable.Blazor.Tests.Fixtures;
using Formidable.Introspection;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Time.Testing;
using static Formidable.Blazor.Tests.Fixtures.EngineTestSync;

namespace Formidable.Blazor.Tests;

/// <summary>
/// One edit after a submit arms two passes at once — a live pass under the live profile and the
/// post-submit refresh under the submit profile. Without <see cref="FormidableOptions.LiveDebounce"/>
/// the live pass runs synchronously, ahead of the refresh's own window, every time. With it set,
/// the two windows close in whatever order their configured widths give them — and the order does
/// not matter, because rule verdicts are keyed by rule and edit stamp: whichever pass fires first
/// executes the stale rules, and the other serves their verdicts from the store. What the engine
/// still enforces is deference to a pass genuinely IN FLIGHT — a window's fire waits rather
/// than cancel a running submit or refresh, and a refresh defers to a running live pass — which
/// is supersession hygiene, not execution ordering. Every configuration still has to reach the
/// same verdicts, so each sequence here is run against one assertion set.
/// </summary>
/// <remarks>
/// The assertions name channels rather than list positions: the draft rule's message can only
/// have come from a live pass (a refresh keeps its verdict to the fields the submit made error
/// sites, and the customer's name is never one of them), and the submit rule's message can only
/// have been cleared by a refresh. Where either message sorts among the visible issues is a
/// separate question with its own tests. That attribution is what every test here narrows
/// <see cref="FormidableOptions.LiveProfile"/> to the draft bucket for: the live channel selects
/// the submit profile's own rules otherwise, and a message either pass could have produced names
/// neither.
/// </remarks>
public class FormidableEngineDebounceOrderTests
{
    private const string DraftMessage = "Customer name must be four characters or fewer";
    private const string SubmitMessage = "Description needs a named customer";

    [Fact]
    public async Task Live_before_refresh_leaves_both_channels_current()
    {
        // LiveDebounce null (immediate) against the 300 ms RefreshDebounce default: the live pass
        // runs on the edit, the refresh lands after it.
        var customer = new EngineCustomer();
        var order = new EngineOrder { Description = "Quarterly refresh", Customer = customer };
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        using var engine = new FormidableEngine<EngineOrder>(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(new ChannelSeparatingValidator()),
            new ReflectionModelIntrospector(),
            new FormidableOptions { LiveProfile = ValidationProfile.Draft, DisclosureOverride = _ => true },
            time);

        var customerName = new FieldIdentifier(customer, nameof(EngineCustomer.Name));
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));

        // The submit blocks on the description, which makes it the one error site a refresh keeps
        // current; the customer's name passes its draft rule at this point.
        Assert.False((await engine.ValidateForSubmitAsync()).CanProceed);
        Assert.Contains(engine.GetIssues(description), i => i.Message == SubmitMessage);

        // One edit breaks the draft rule and clears the submit rule at the same time.
        customer.Name = "far too long";
        editContext.NotifyFieldChanged(customerName);

        // No live debounce, so the live pass has already run: its verdict is on the field while
        // the refresh the same edit armed is still waiting out its window.
        Assert.Contains(engine.GetIssues(customerName), i => i.Message == DraftMessage);
        Assert.Contains(engine.GetIssues(description), i => i.Message == SubmitMessage);

        time.Advance(TimeSpan.FromMilliseconds(301)); // the refresh lands second

        AssertBothChannelsCurrent(engine, customerName, description);
    }

    [Fact]
    public async Task A_wide_live_debounce_lets_the_refresh_land_first_and_the_live_pass_reuse_it()
    {
        // LiveDebounce 400 ms against the 300 ms RefreshDebounce default: the refresh window is
        // the narrower one, so the refresh fires first, executes the whole stale selection, and
        // the live window's own pass then has nothing left to run — it publishes the draft
        // rule's verdict from the store. The reversed pass order changes which pass pays for
        // which rule and nothing else.
        var customer = new EngineCustomer();
        var order = new EngineOrder { Description = "Quarterly refresh", Customer = customer };
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        using var engine = new FormidableEngine<EngineOrder>(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(new ChannelSeparatingValidator()),
            new ReflectionModelIntrospector(),
            new FormidableOptions
            {
                LiveDebounce = TimeSpan.FromMilliseconds(400),
                LiveProfile = ValidationProfile.Draft,
                DisclosureOverride = _ => true,
            },
            time);

        var customerName = new FieldIdentifier(customer, nameof(EngineCustomer.Name));
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));

        Assert.False((await engine.ValidateForSubmitAsync()).CanProceed);
        Assert.Contains(engine.GetIssues(description), i => i.Message == SubmitMessage);

        customer.Name = "far too long";
        editContext.NotifyFieldChanged(customerName);

        // Both windows are open, so neither channel has moved yet.
        Assert.Empty(engine.GetIssues(customerName));
        Assert.Contains(engine.GetIssues(description), i => i.Message == SubmitMessage);

        time.Advance(TimeSpan.FromMilliseconds(300)); // the refresh fires first

        // The submit channel is already current — the refresh executed both rules — while the
        // draft failure it computed waits for the live channel: it is not a submit-time error
        // site, so only a live pass's verdict apply can disclose it.
        Assert.DoesNotContain(engine.GetIssues(description), i => i.Message == SubmitMessage);
        Assert.Empty(engine.GetIssues(customerName));

        time.Advance(TimeSpan.FromMilliseconds(100)); // the live window closes behind it

        AssertBothChannelsCurrent(engine, customerName, description);
    }

    [Fact]
    public async Task Live_before_refresh_settles_an_async_rule_in_both_channels()
    {
        // The same ordering with the shared draft rule asynchronous, and the mirror of the
        // inverted test below: here the LIVE pass is the one in flight when the other's window
        // closes, so the refresh is the one that has to stand down and wait rather than cancel
        // it. An async live rule outlasting the refresh window is the whole shape of that branch.
        var customer = new EngineCustomer();
        var order = new EngineOrder { Description = "Quarterly refresh", Customer = customer };
        var validator = new GatedChannelSeparatingValidator();
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        using var engine = new FormidableEngine<EngineOrder>(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(validator),
            new ReflectionModelIntrospector(),
            new FormidableOptions { LiveProfile = ValidationProfile.Draft, DisclosureOverride = _ => true },
            time);

        var customerName = new FieldIdentifier(customer, nameof(EngineCustomer.Name));
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));

        var submit = engine.ValidateForSubmitAsync();
        validator.Gate.SetResult();
        Assert.False((await submit).CanProceed);
        Assert.Contains(engine.GetIssues(description), i => i.Message == SubmitMessage);
        validator.Reset();

        customer.Name = "far too long";
        editContext.NotifyFieldChanged(customerName);

        // The live pass is in flight on the async rule, so the field it covers reads as pending.
        Assert.True(engine.GetFieldState(customerName).IsValidating);

        time.Advance(TimeSpan.FromMilliseconds(301)); // the refresh window closes against the gated live pass

        // Deferred, not started: the live pass is still the one in flight, and the submit channel
        // still carries the verdict only a refresh can clear.
        Assert.True(engine.GetFieldState(customerName).IsValidating);
        Assert.Contains(engine.GetIssues(description), i => i.Message == SubmitMessage);

        var liveSettled = Quiescence(engine);
        validator.Gate.SetResult();
        await liveSettled;
        validator.Reset();

        // The live pass landed; the refresh it held up still owes the submit channel its answer.
        Assert.Contains(engine.GetIssues(customerName), i => i.Message == DraftMessage);
        Assert.Contains(engine.GetIssues(description), i => i.Message == SubmitMessage);

        var refreshSettled = Quiescence(engine);
        time.Advance(TimeSpan.FromMilliseconds(301)); // the re-armed refresh window closes
        Assert.True(engine.GetFieldState(customerName).IsValidating);
        validator.Gate.SetResult();
        await refreshSettled;

        AssertBothChannelsCurrent(engine, customerName, description);
    }

    [Fact]
    public async Task A_wide_live_debounce_defers_to_the_in_flight_refresh_and_lands_from_the_store()
    {
        // LiveDebounce 400 ms against the 300 ms RefreshDebounce default, with both rules
        // asynchronous: the refresh fires first and is still in flight on its gate when the live
        // window closes. The window must wait rather than start a pass that would cancel the
        // refresh (see RefreshInFlight's remarks) — and once the refresh lands, the re-armed
        // window's own pass finds every rule answered at its stamp and publishes the draft
        // verdict without touching the gate at all.
        var customer = new EngineCustomer();
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
                LiveDebounce = TimeSpan.FromMilliseconds(400),
                LiveProfile = ValidationProfile.Draft,
                DisclosureOverride = _ => true,
            },
            time);

        var customerName = new FieldIdentifier(customer, nameof(EngineCustomer.Name));
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));

        var submit = engine.ValidateForSubmitAsync();
        validator.Gate.SetResult();
        Assert.False((await submit).CanProceed);
        Assert.Contains(engine.GetIssues(description), i => i.Message == SubmitMessage);
        validator.Reset();

        customer.Name = "far too long";
        editContext.NotifyFieldChanged(customerName);

        time.Advance(TimeSpan.FromMilliseconds(300)); // the refresh fires first and blocks on its rule
        Assert.True(engine.GetFieldState(customerName).IsValidating);

        time.Advance(TimeSpan.FromMilliseconds(100)); // the live window closes against the in-flight refresh

        // Deferred, not started: the refresh is still the one in flight, and the submit channel
        // still carries the verdict only it can clear.
        Assert.True(engine.GetFieldState(customerName).IsValidating);
        Assert.Contains(engine.GetIssues(description), i => i.Message == SubmitMessage);

        var refreshSettled = Quiescence(engine);
        validator.Gate.SetResult();
        await refreshSettled;

        // The refresh landed both rules: the submit channel is current, and the draft failure it
        // computed waits for the live channel to disclose it.
        Assert.DoesNotContain(engine.GetIssues(description), i => i.Message == SubmitMessage);
        Assert.Empty(engine.GetIssues(customerName));

        time.Advance(TimeSpan.FromMilliseconds(400)); // the re-armed window closes; nothing left to execute

        AssertBothChannelsCurrent(engine, customerName, description);
    }

    // A refresh that comes due at a RefreshDebounce of zero while an edit's live check is out
    // fires once and waits; the live check's end arms it once more, and the refresh then answers
    // the submit rules once. The live channel is narrowed to the draft bucket, so only the refresh
    // runs the submit rule. Mutation: re-arm the refresh at its own wait from the fire that finds
    // the live check, and the fire repeats until the counting clock's cap stops it.
    [Fact]
    public async Task A_refresh_deferred_behind_a_live_pass_fires_once_and_runs_once_it_ends()
    {
        var customer = new EngineCustomer { Name = "Bo" };
        var order = new EngineOrder { Description = "ok", Customer = customer };
        var validator = new GatedRuleRunCountingValidator();
        validator.Gate.SetResult();
        var editContext = new EditContext(order);
        var clock = new CountingTimeProvider(new FakeTimeProvider());
        using var engine = new FormidableEngine<EngineOrder>(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(validator),
            new ReflectionModelIntrospector(),
            new FormidableOptions { LiveProfile = ValidationProfile.Draft, RefreshDebounce = TimeSpan.Zero },
            clock);
        Assert.True((await engine.ValidateForSubmitAsync()).CanProceed);
        validator.Reset();
        var submitRuns = validator.SubmitRuleRuns;
        var draftRuns = validator.DraftRuleRuns;

        customer.Name = "far too long";
        editContext.NotifyFieldChanged(new FieldIdentifier(customer, nameof(EngineCustomer.Name)));

        // The live check is out on the draft rule; the refresh has come due once and waited.
        Assert.Equal(draftRuns + 1, validator.DraftRuleRuns);
        Assert.Equal(submitRuns, validator.SubmitRuleRuns);
        Assert.Equal(1, clock.Callbacks);
        Assert.False(clock.AnyCapped);

        validator.Gate.SetResult();
        await UntilAsync(() => validator.SubmitRuleRuns > submitRuns && !engine.IsValidating);

        Assert.Equal(submitRuns + 1, validator.SubmitRuleRuns);
        Assert.Equal(draftRuns + 1, validator.DraftRuleRuns);
        Assert.Equal(2, clock.Callbacks);
        Assert.False(clock.AnyCapped);
    }

    // A live window of zero that closes while a load is out fires once and waits; the load's end
    // arms it once more, and the window's check then runs once, for the value edited during the
    // load. Mutation: re-arm the window at its own width from the fire that finds the load, and the
    // fire repeats until the counting clock's cap stops it.
    [Fact]
    public async Task A_live_window_deferred_behind_a_load_fires_once_and_runs_once_it_ends()
    {
        var customer = new EngineCustomer { Name = "Bo" };
        var order = new EngineOrder { Description = "ok", Customer = customer };
        var validator = new GatedRuleRunCountingValidator();
        var editContext = new EditContext(order);
        var clock = new CountingTimeProvider(new FakeTimeProvider());
        using var engine = new FormidableEngine<EngineOrder>(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(validator),
            new ReflectionModelIntrospector(),
            new FormidableOptions { LiveDebounce = TimeSpan.Zero },
            clock);
        var name = new FieldIdentifier(customer, nameof(EngineCustomer.Name));

        var load = engine.DiscloseLoadedValuesAsync();
        customer.Name = "far too long";
        editContext.NotifyFieldChanged(name);

        // The load is out on the draft rule; the window has closed once and waited.
        Assert.Equal(1, validator.DraftRuleRuns);
        Assert.Equal(1, clock.Callbacks);
        Assert.False(clock.AnyCapped);

        validator.Gate.SetResult();
        await load;
        await UntilAsync(() => engine.GetIssues(name).Count > 0 && !engine.IsValidating);

        // One more run of each rule, for the edited value, and the window's message shows.
        Assert.Equal(2, validator.DraftRuleRuns);
        Assert.Equal(2, validator.SubmitRuleRuns);
        Assert.Contains(engine.GetIssues(name), issue => issue.Message == RuleRunCountingValidator.DraftMessage);
        Assert.Equal(2, clock.Callbacks);
        Assert.False(clock.AnyCapped);
    }

    // A pin, which a fire that re-armed itself behind the live check would pass too. The refresh
    // that came due during an edit's live check runs once that check has ended, even when it ended
    // in a fault, and its landing clears the fault's form-level message. Mutation: arm the waiting
    // fires only from a pass that landed, and after the fault nothing runs the refresh, so the
    // message stays.
    [Fact]
    public async Task A_check_deferred_behind_a_pass_that_faults_still_runs()
    {
        var order = new EngineOrder { Description = "ok", Customer = new EngineCustomer { Name = "Bo" } };
        var validator = new TwoFieldGatedValidator();
        validator.Gate.SetResult();
        var editContext = new EditContext(order);
        var clock = new CountingTimeProvider(new FakeTimeProvider());
        var options = new FormidableOptions();
        using var engine = new FormidableEngine<EngineOrder>(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(validator),
            new ReflectionModelIntrospector(),
            options,
            clock,
            EngineTestSync.OneAtATime());
        Assert.True((await engine.ValidateForSubmitAsync()).CanProceed);
        validator.Reset();

        order.Description = "still ok";
        editContext.NotifyFieldChanged(new FieldIdentifier(order, nameof(EngineOrder.Description)));
        clock.Advance(options.RefreshDebounce); // the refresh comes due behind the live check

        var faulted = 0;
        engine.ValidationFaulted += (_, _) => faulted++;
        var armsBefore = clock.Changes;
        validator.ThrowOnRelease = true;
        validator.Gate.SetResult();
        await UntilAsync(() => faulted > 0 && !engine.IsValidating);
        Assert.Contains(engine.GetVisibleIssues(), issue => issue.Issue.Message == options.ValidationFaultMessage);

        // The live check ends on a worker thread, and its end arms the waiting refresh in a
        // dispatch of its own after the one that cleared IsValidating. The clock moves only once
        // that arm has landed, or the refresh would come due on a clock nothing advances again.
        await UntilAsync(() => clock.Changes > armsBefore);
        validator.ThrowOnRelease = false;
        clock.Advance(options.RefreshDebounce);
        await UntilAsync(() => !engine.IsValidating);

        Assert.DoesNotContain(engine.GetVisibleIssues(), issue => issue.Issue.Message == options.ValidationFaultMessage);
        Assert.Equal(1, faulted);
    }

    // The same fault, with a StateChanged handler that throws once as the faulted live check ends.
    // The throw reaches nobody who could retry, so the refresh that waited for that check must be
    // armed whatever the handler does. Mutation: arm the waiting fires only after the end's
    // notification returns, and the refresh never runs, so the fault's message stays.
    [Fact]
    public async Task A_check_deferred_behind_a_pass_whose_end_handler_throws_still_runs()
    {
        var order = new EngineOrder { Description = "ok", Customer = new EngineCustomer { Name = "Bo" } };
        var validator = new TwoFieldGatedValidator();
        validator.Gate.SetResult();
        var editContext = new EditContext(order);
        var clock = new CountingTimeProvider(new FakeTimeProvider());
        var options = new FormidableOptions();
        using var engine = new FormidableEngine<EngineOrder>(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(validator),
            new ReflectionModelIntrospector(),
            options,
            clock,
            EngineTestSync.OneAtATime());
        Assert.True((await engine.ValidateForSubmitAsync()).CanProceed);
        validator.Reset();

        order.Description = "still ok";
        editContext.NotifyFieldChanged(new FieldIdentifier(order, nameof(EngineOrder.Description)));
        clock.Advance(options.RefreshDebounce); // the refresh comes due behind the live check

        var armsBefore = clock.Changes;
        var throwAtEnd = false;
        var thrown = 0;
        engine.ValidationFaulted += (_, _) => throwAtEnd = true;
        engine.StateChanged += (_, _) =>
        {
            if (throwAtEnd && !engine.IsValidating)
            {
                throwAtEnd = false;
                thrown++;
                throw new InvalidOperationException("handler blew up");
            }
        };
        validator.ThrowOnRelease = true;
        validator.Gate.SetResult();
        await UntilAsync(() => thrown > 0 && !engine.IsValidating);
        Assert.Equal(1, thrown);
        Assert.Contains(engine.GetVisibleIssues(), issue => issue.Issue.Message == options.ValidationFaultMessage);

        // As above, the clock moves only once the end's own dispatch has armed the refresh.
        await UntilAsync(() => clock.Changes > armsBefore);
        validator.ThrowOnRelease = false;
        clock.Advance(options.RefreshDebounce);
        await UntilAsync(() => !engine.IsValidating
            && !engine.GetVisibleIssues().Any(issue => issue.Issue.Message == options.ValidationFaultMessage));

        Assert.DoesNotContain(engine.GetVisibleIssues(), issue => issue.Issue.Message == options.ValidationFaultMessage);
    }

    // TrackFormValidity on, a LiveDebounce that never closes, RefreshDebounce 300 ms. An edit to A
    // before any reply arms the validity check, a server reply follows, and a submit or load held
    // on C's rule is out when the check comes due, so the check waits for it. Each recipe then
    // ends that submit or load and, at the same instant, empties B, which B's rule rejects. That
    // edit arms the whole-form re-check, and a double run needs a validity check still armed
    // beside it, due at the same instant:
    // - "cancelled-load" and "cancelled-submit": the pass ends unanswered and its end arms the
    //   waiting check once more, at the instant the edit comes;
    // - "landed-load": the load lands with its answer adopted, which drops the waiting check, so
    //   no check is left for the edit to meet;
    // - "edit-during-load": no check is ever armed, because the edit to A comes after the reply,
    //   during the load, and arms the re-check instead.
    // B's async rule is held from the edit on, so a run that starts is still unanswered when any
    // other timer due at that instant fires. The clock runs timers due together in the order they
    // were armed ("fifo"), in reverse ("lifo", the re-check first), or in FakeTimeProvider's own
    // order ("fake", which here is the order they were armed). Every row runs B's rule once for the
    // one edit, and IsFormValid ends false, the answer for the emptied B.
    // Mutation: drop the stand-down HandleFieldChanged makes when the edit arms the re-check. The
    // two unanswered recipes then run the rule twice when the re-check fires first (lifo).
    [Theory]
    [InlineData("lifo", "cancelled-load")]
    [InlineData("fifo", "cancelled-load")]
    [InlineData("fake", "cancelled-load")]
    [InlineData("lifo", "cancelled-submit")]
    [InlineData("fifo", "cancelled-submit")]
    [InlineData("fake", "cancelled-submit")]
    [InlineData("lifo", "landed-load")]
    [InlineData("fifo", "landed-load")]
    [InlineData("lifo", "edit-during-load")]
    [InlineData("fifo", "edit-during-load")]
    public async Task An_edit_beside_a_waiting_validity_check_runs_the_rule_once_in_either_timer_order(string clockKind, string recipe)
    {
        var (runs, isFormValid) = await RunTieAsync(clockKind, gapMs: 0, recipe);

        Assert.False(isFormValid);
        Assert.Equal(1, runs);
    }

    // The control: the edit to B comes 1 ms after the end of the submit or load, so a validity
    // check still armed beside the re-check would come due 1 ms before it, first in every clock
    // order, while that re-check is still armed for it to stand down for.
    [Theory]
    [InlineData("lifo", "cancelled-load")]
    [InlineData("fifo", "cancelled-load")]
    [InlineData("fake", "cancelled-load")]
    [InlineData("lifo", "cancelled-submit")]
    [InlineData("fifo", "cancelled-submit")]
    [InlineData("fake", "cancelled-submit")]
    [InlineData("lifo", "landed-load")]
    public async Task A_one_millisecond_gap_runs_the_rule_once(string clockKind, string recipe)
    {
        var (runs, isFormValid) = await RunTieAsync(clockKind, gapMs: 1, recipe);

        Assert.False(isFormValid);
        Assert.Equal(1, runs);
    }

    // The mirror: the re-check is the one waiting, and the edit is the one that would arm the
    // validity check. A first submit held on C's rule is cancelled after an edit to A during it,
    // so HasSubmitted stays false while the re-check that edit armed stays armed. The edit to B,
    // at the same instant or later, then takes the never-closing window's branch. The re-check
    // begins after that edit and answers it, so B's rule runs once in every order.
    // Mutation: let that branch arm the validity check whatever the re-check says. The check then
    // comes due with the re-check or after it, fires once the re-check has begun, and runs the
    // rule a second time (fifo and fake at the same instant, every clock 100 ms later).
    [Theory]
    [InlineData("lifo", 0)]
    [InlineData("fifo", 0)]
    [InlineData("fake", 0)]
    [InlineData("lifo", 100)]
    [InlineData("fake", 100)]
    public async Task An_edit_beside_a_waiting_recheck_runs_the_rule_once_in_either_timer_order(string clockKind, int gapMs)
    {
        var (runs, isFormValid) = await RunTieAsync(clockKind, gapMs, "edit-during-first-submit");

        Assert.False(isFormValid);
        Assert.Equal(1, runs);
    }

    /// <summary>Runs one recipe and returns how many times B's async rule started for the edit to B, and <see cref="FormidableEngine{TModel}.IsFormValid"/> once every check has settled.</summary>
    private static async Task<(int Runs, bool IsFormValid)> RunTieAsync(string clockKind, int gapMs, string recipe)
    {
        var fake = new FakeTimeProvider();
        var tie = clockKind == "fake" ? null : new TieOrderClock(fake) { LaterArmedFirst = clockKind == "lifo" };
        var dispatch = EngineTestSync.OneAtATime();
        var validator = new TripValidator();
        var trip = new Trip();
        var editContext = new EditContext(trip);
        using var engine = new FormidableEngine<Trip>(
            trip, editContext,
            new FluentValidationModelValidator<Trip>(validator),
            new ReflectionModelIntrospector(),
            new FormidableOptions
            {
                TrackFormValidity = true,
                LiveDebounce = Timeout.InfiniteTimeSpan,
                RefreshDebounce = TimeSpan.FromMilliseconds(300),
            },
            tie is null ? fake : tie,
            dispatch);
        var a = new FieldIdentifier(trip, nameof(Trip.A));
        var b = new FieldIdentifier(trip, nameof(Trip.B));

        // Every step the test takes runs through the engine's dispatch, as a page's would.
        Task OnDispatcher(Action action) => dispatch(() =>
        {
            action();
            return Task.CompletedTask;
        });
        Task Advance(TimeSpan by) => OnDispatcher(() =>
        {
            if (tie is null)
            {
                fake.Advance(by);
            }
            else
            {
                tie.Advance(by);
            }
        });
        Task Edit(FieldIdentifier field, Action write) => OnDispatcher(() =>
        {
            write();
            editContext.NotifyFieldChanged(field);
        });
        Task Reply() => OnDispatcher(() => engine.ApplyServerIssues([new ValidationIssue(nameof(Trip.B), "Server says no")]));

        await UntilAsync(() => !engine.IsValidating);

        using var cancel = new CancellationTokenSource();
        validator.HoldC = true;
        Task held;
        switch (recipe)
        {
            case "edit-during-load":
                await Reply();
                held = dispatch(() => engine.DiscloseLoadedValuesAsync());
                await UntilAsync(() => engine.IsValidating);
                await Edit(a, () => trip.A = "x"); // arms the re-check, not the validity check
                break;
            case "edit-during-first-submit":
                held = dispatch(() => engine.ValidateForSubmitAsync(cancel.Token));
                await UntilAsync(() => engine.IsValidating);
                await Edit(a, () => trip.A = "x"); // arms the re-check, which outlives the submit
                break;
            default:
                await Edit(a, () => trip.A = "x"); // arms the validity check
                await Reply();
                held = recipe switch
                {
                    "cancelled-submit" => dispatch(() => engine.ValidateForSubmitAsync(cancel.Token)),
                    "cancelled-load" => dispatch(() => engine.DiscloseLoadedValuesAsync(cancel.Token)),
                    _ => dispatch(() => engine.DiscloseLoadedValuesAsync()),
                };
                await UntilAsync(() => engine.IsValidating);
                await Advance(TimeSpan.FromMilliseconds(300)); // the check comes due and waits
                break;
        }

        validator.HoldC = false;
        if (recipe is "cancelled-load" or "cancelled-submit" or "edit-during-first-submit")
        {
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => held);
        }
        else
        {
            validator.GateC.TrySetResult();
            await held;
        }

        await UntilAsync(() => !engine.IsValidating);
        Assert.Equal(recipe != "edit-during-first-submit", engine.HasSubmitted);
        if (gapMs > 0)
        {
            await Advance(TimeSpan.FromMilliseconds(gapMs));
        }

        validator.HoldB = true;
        var startsBefore = validator.BStarts;
        Assert.True(engine.IsFormValid);
        await Edit(b, () => trip.B = string.Empty); // the one edit, which fails B's rule

        await Advance(TimeSpan.FromMilliseconds(300)); // the re-check comes due, and so would a check beside it
        await Task.Delay(50);

        validator.HoldB = false;
        validator.GateB.TrySetResult();
        await UntilAsync(() => !engine.IsValidating && validator.BEnds == validator.BStarts);
        await Advance(TimeSpan.FromSeconds(2));
        await UntilAsync(() => !engine.IsValidating && validator.BEnds == validator.BStarts);
        await Task.Delay(50);

        return (validator.BStarts - startsBefore, engine.IsFormValid);
    }

    /// <summary>Yields until <paramref name="condition"/> holds, for at most five seconds; the caller asserts afterwards.</summary>
    private static async Task UntilAsync(Func<bool> condition)
    {
        for (var waited = 0; waited < 500 && !condition(); waited++)
        {
            await Task.Delay(10);
        }
    }

    /// <summary>
    /// The one assertion set every ordering has to reach. Each message names exactly one channel:
    /// only a live pass can put the draft rule's verdict on the customer's name, and only a
    /// refresh can take the submit rule's verdict off the description.
    /// </summary>
    private static void AssertBothChannelsCurrent(
        FormidableEngine<EngineOrder> engine,
        FieldIdentifier customerName,
        FieldIdentifier description)
    {
        // One read settles the pending indicator for every field: IsFieldValidating is this
        // conjoined with the pass's scope, so a field-level read here could not fail on its own.
        // The reads that CAN fail are the mid-flight ones, asserted where the pass is still open.
        Assert.False(engine.IsValidating);

        // The live channel carries the edit's own verdict.
        Assert.Contains(engine.GetIssues(customerName), i => i.Message == DraftMessage);
        Assert.Contains(engine.EditContext.GetValidationMessages(customerName), m => m == DraftMessage);

        // The submit channel carries the refreshed one: the rule that blocked the submit passes
        // now, so nothing is left holding the form.
        Assert.DoesNotContain(engine.GetIssues(description), i => i.Message == SubmitMessage);
        Assert.Empty(engine.EditContext.GetValidationMessages(description));
    }

    /// <summary>Three fields, each with a value every rule accepts.</summary>
    private sealed class Trip
    {
        public string? A { get; set; } = "a";

        public string? B { get; set; } = "b";

        public string? C { get; set; } = "c";
    }

    /// <summary>
    /// A synchronous rule on A, an asynchronous rule on B that counts each run as it starts and
    /// as it ends and rejects an empty B, and an asynchronous rule on C that can hold a check out.
    /// All three are default rules, so every check runs them together. B and C wait on their gates
    /// only while held.
    /// </summary>
    private sealed class TripValidator : AbstractValidator<Trip>
    {
        private int _bStarts;
        private int _bEnds;
        private bool _holdB;
        private bool _holdC;

        public TripValidator()
        {
            RuleFor(m => m.A).NotEmpty();
            RuleFor(m => m.B).MustAsync(async (value, ct) =>
            {
                Interlocked.Increment(ref _bStarts);
                try
                {
                    if (HoldB)
                    {
                        await GateB.Task.WaitAsync(ct);
                    }

                    return !string.IsNullOrEmpty(value);
                }
                finally
                {
                    Interlocked.Increment(ref _bEnds);
                }
            });
            RuleFor(m => m.C).MustAsync(async (_, ct) =>
            {
                if (HoldC)
                {
                    await GateC.Task.WaitAsync(ct);
                }

                return true;
            });
        }

        public bool HoldB
        {
            get => Volatile.Read(ref _holdB);
            set => Volatile.Write(ref _holdB, value);
        }

        public bool HoldC
        {
            get => Volatile.Read(ref _holdC);
            set => Volatile.Write(ref _holdC, value);
        }

        public TaskCompletionSource GateB { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource GateC { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int BStarts => Volatile.Read(ref _bStarts);

        public int BEnds => Volatile.Read(ref _bEnds);
    }

    /// <summary>
    /// A clock over a <see cref="FakeTimeProvider"/> that runs the timers one <see cref="Advance"/>
    /// brings due in due-time order, and timers due at the same instant in the order
    /// <see cref="LaterArmedFirst"/> picks. A real timer gives no order for two timers due
    /// together, so either order is a run the engine has to handle.
    /// </summary>
    /// <remarks>
    /// The fires an advance collects run after the fake clock has moved, so a timer one of them
    /// arms is due from the end of the advance. A zero due time still fires inside the arm, as the
    /// fake clock's own does.
    /// </remarks>
    private sealed class TieOrderClock(FakeTimeProvider clock) : TimeProvider
    {
        private List<(DateTimeOffset Due, long Seq, TieTimer Timer)>? _collected;
        private long _seq;

        public bool LaterArmedFirst { get; init; }

        public override long GetTimestamp() => clock.GetTimestamp();

        public override long TimestampFrequency => clock.TimestampFrequency;

        public override DateTimeOffset GetUtcNow() => clock.GetUtcNow();

        public override TimeZoneInfo LocalTimeZone => clock.LocalTimeZone;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new TieTimer(this, callback, state);
            timer.Inner = clock.CreateTimer(_ => OnFire(timer), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            if (dueTime != Timeout.InfiniteTimeSpan)
            {
                timer.Change(dueTime, period);
            }

            return timer;
        }

        public void Advance(TimeSpan by)
        {
            _collected = [];
            clock.Advance(by);
            var due = _collected;
            _collected = null;
            var ordered = LaterArmedFirst
                ? due.OrderBy(x => x.Due).ThenByDescending(x => x.Seq)
                : due.OrderBy(x => x.Due).ThenBy(x => x.Seq);
            foreach (var (_, _, timer) in ordered.ToList())
            {
                timer.Run();
            }
        }

        private void OnFire(TieTimer timer)
        {
            if (_collected is not null)
            {
                _collected.Add((timer.Due, timer.Seq, timer));
            }
            else
            {
                timer.Run();
            }
        }

        private sealed class TieTimer(TieOrderClock owner, TimerCallback callback, object? state) : ITimer
        {
            public ITimer Inner { get; set; } = default!;

            public DateTimeOffset Due { get; private set; }

            public long Seq { get; private set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (dueTime != Timeout.InfiniteTimeSpan)
                {
                    Due = owner.GetUtcNow() + dueTime;
                    Seq = ++owner._seq;
                }

                return Inner.Change(dueTime, period);
            }

            public void Run() => callback(state);

            public void Dispose() => Inner.Dispose();

            public ValueTask DisposeAsync() => Inner.DisposeAsync();
        }
    }
}
