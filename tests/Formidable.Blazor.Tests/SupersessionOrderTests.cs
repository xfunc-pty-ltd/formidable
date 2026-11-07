using FluentValidation;
using Formidable.Introspection;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Time.Testing;

namespace Formidable.Blazor.Tests;

/// <summary>
/// A check that begins records itself as the current check before it cancels the one it
/// overtakes. On Blazor WebAssembly there is no synchronization context and the dispatcher runs
/// work at once, so cancelling the older check's token can run that check's end inside the newer
/// call. That end must find itself overtaken and publish nothing, so a
/// <see cref="IFormidableEngine.StateChanged"/> handler sees each flag rise once and fall once.
/// Each row builds that host: no context around the engine calls, and a dispatch that runs its
/// work inline. The rule waits with <c>WaitAsync(ct)</c>, whose wait completes inside the cancel.
/// Recording first must not strand the new check when the cancel throws, which a callback a rule
/// registered on its token can make it do, a disposal whose cancel throws still finishes, and a
/// disposed engine starts no check at all.
/// </summary>
public class SupersessionOrderTests
{
    public sealed class Account
    {
        public string? Other { get; set; }

        public string? Name { get; set; }

        public string? Third { get; set; }
    }

    /// <summary>A counting async rule on Other, in the submit bucket, that waits on a gate while <see cref="Hold"/> is set and honours its token.</summary>
    private sealed class HeldValidator : DraftSubmitValidator<Account>
    {
        private volatile bool _hold;
        private volatile bool _fails;
        private volatile bool _throwsOnCancel;
        private volatile bool _throws;
        private volatile Action? _onCancel;
        private TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _runs;
        private int _held;

        /// <summary>How many times the async rule has started.</summary>
        public int Runs => Volatile.Read(ref _runs);

        /// <summary>How many runs have reached the gate, each with its callbacks already registered.</summary>
        public int Held => Volatile.Read(ref _held);

        /// <summary>Whether a run that starts waits on the gate.</summary>
        public bool Hold
        {
            get => _hold;
            set => _hold = value;
        }

        /// <summary>Whether a run that answers rejects Other.</summary>
        public bool Fails
        {
            get => _fails;
            set => _fails = value;
        }

        /// <summary>Whether a run that starts registers a callback on its token that throws when the token is cancelled.</summary>
        public bool ThrowsOnCancel
        {
            get => _throwsOnCancel;
            set => _throwsOnCancel = value;
        }

        /// <summary>Whether a run that answers throws instead.</summary>
        public bool Throws
        {
            get => _throws;
            set => _throws = value;
        }

        /// <summary>A callback a run that starts registers on its token, which runs when the token is cancelled.</summary>
        public Action? OnCancel
        {
            get => _onCancel;
            set => _onCancel = value;
        }

        /// <summary>Lets every held run, and every later one, answer.</summary>
        public void Release()
        {
            Hold = false;
            Volatile.Read(ref _gate).TrySetResult();
        }

        /// <summary>Lets the runs held now answer, and holds the later ones while <see cref="Hold"/> is set.</summary>
        public void ReleaseHeld() =>
            Interlocked.Exchange(ref _gate, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
                .TrySetResult();

        protected override void ConfigureDraftRules()
        {
        }

        protected override void ConfigureSubmitRules() =>
            RuleFor(m => m.Other).MustAsync(async (_, ct) =>
            {
                Interlocked.Increment(ref _runs);

                // Registered before the wait, so each runs after the wait's own callback.
                if (OnCancel is { } onCancel)
                {
                    ct.Register(onCancel);
                }

                if (ThrowsOnCancel)
                {
                    ct.Register(() => throw new InvalidOperationException("The rule's cancellation callback threw."));
                }

                if (Hold)
                {
                    var gate = Volatile.Read(ref _gate).Task;
                    Interlocked.Increment(ref _held);
                    await gate.WaitAsync(ct);
                }

                if (Throws)
                {
                    throw new InvalidOperationException("The rule failed.");
                }

                return !Fails;
            });
    }

    /// <summary>The engine's two flags, as each <see cref="IFormidableEngine.StateChanged"/> handler read them, on whichever thread raised it.</summary>
    private sealed class FlagRecord
    {
        private readonly object _sync = new();
        private readonly List<(bool Submitting, bool Validating)> _seen = [];

        public FlagRecord(IFormidableEngine engine) =>
            engine.StateChanged += (_, _) =>
            {
                lock (_sync)
                {
                    _seen.Add((engine.IsSubmitting, engine.IsValidating));
                }
            };

        public List<(bool Submitting, bool Validating)> Seen
        {
            get
            {
                lock (_sync)
                {
                    return [.. _seen];
                }
            }
        }

        /// <summary>One flag's values in the order the handlers read them, each run of equal values collapsed to one.</summary>
        public string Shape(Func<(bool Submitting, bool Validating), bool> flag)
        {
            var shape = new List<bool>();
            foreach (var seen in Seen)
            {
                var value = flag(seen);
                if (shape.Count == 0 || shape[^1] != value)
                {
                    shape.Add(value);
                }
            }

            return string.Join(",", shape.Select(up => up ? "up" : "down"));
        }
    }

    /// <summary>A check a caller starts and awaits.</summary>
    public enum CheckCall
    {
        /// <summary>A submit.</summary>
        Submit,

        /// <summary>A load of values; Other holds nothing, so the load adopts nothing and no check follows it.</summary>
        Load,
    }

    private static FormidableEngine<Account> BuildEngine(
        Account model, HeldValidator validator, FakeTimeProvider time, FormidableOptions? options = null) =>
        new(
            model, new EditContext(model), new FluentValidationModelValidator<Account>(validator),
            new ReflectionModelIntrospector(), options ?? new FormidableOptions(), time, work => work());

    private static Task Start(FormidableEngine<Account> engine, CheckCall call) =>
        call == CheckCall.Submit ? engine.ValidateForSubmitAsync() : engine.DiscloseLoadedValuesAsync();

    /// <summary>Makes <paramref name="call"/> with no synchronization context, as a WebAssembly host does, and puts the caller's context back before anything awaits.</summary>
    private static T WithoutContext<T>(Func<T> call)
    {
        var ambient = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            return call();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(ambient);
        }
    }

    /// <summary>Commits <paramref name="value"/> to the field <paramref name="member"/> names, with no synchronization context, as a WebAssembly host does.</summary>
    private static void Edit(EditContext editContext, Account model, string member, string value) =>
        WithoutContext(() =>
        {
            switch (member)
            {
                case nameof(Account.Name):
                    model.Name = value;
                    break;
                case nameof(Account.Third):
                    model.Third = value;
                    break;
                default:
                    model.Other = value;
                    break;
            }

            editContext.NotifyFieldChanged(new FieldIdentifier(model, member));
            return true;
        });

    /// <summary>Yields until <paramref name="condition"/> holds, for at most five seconds.</summary>
    /// <returns>Whether the condition holds at the end.</returns>
    private static async Task<bool> UntilAsync(Func<bool> condition)
    {
        for (var waited = 0; waited < 500 && !condition(); waited++)
        {
            await Task.Delay(10);
        }

        return condition();
    }

    // The overtaken submit's end runs inside the overtaking call, as its token is cancelled. The
    // one notification raised inside that call is the overtaking check's begin, both flags rise
    // once and fall once, and the overtaking check runs and answers as it would with no submit
    // before it.
    // Mutation: cancel the older check's token before recording the new check, and both rows fail:
    // the older check's end raises a notification inside the call that reads both flags false.
    [Theory]
    [InlineData(CheckCall.Submit)]
    [InlineData(CheckCall.Load)]
    public async Task An_overtaken_submit_ended_inside_the_overtaking_call_publishes_nothing(CheckCall overtaker)
    {
        var model = new Account();
        var time = new FakeTimeProvider();
        var validator = new HeldValidator();
        using var engine = BuildEngine(model, validator, time);
        var record = new FlagRecord(engine);
        validator.Hold = true;

        var submit = WithoutContext(() => engine.ValidateForSubmitAsync());
        Assert.True(await UntilAsync(() => validator.Runs == 1));
        Assert.True(engine.IsSubmitting);

        var seenBefore = record.Seen.Count;
        var overtaking = WithoutContext(() => Start(engine, overtaker));

        // The cancellation ran the overtaken submit to its end before the call returned. The
        // notification inside the call is the overtaking check's begin: checking, and submitting
        // only when the overtaking check is a submit.
        Assert.True(submit.IsCompleted);
        Assert.False((await submit).CanProceed);
        Assert.Equal([(overtaker == CheckCall.Submit, true)], record.Seen.Skip(seenBefore));
        Assert.True(engine.IsValidating);
        Assert.Equal(overtaker == CheckCall.Submit, engine.IsSubmitting);
        Assert.True(await UntilAsync(() => validator.Runs == 2));

        validator.Release();
        if (overtaking is Task<SubmitOutcome> later)
        {
            Assert.True((await later).CanProceed);
        }
        else
        {
            await overtaking;
        }

        WithoutContext(() =>
        {
            time.Advance(TimeSpan.FromHours(1));
            return true;
        });
        Assert.True(await UntilAsync(() => !engine.IsValidating));

        Assert.Equal("up,down", record.Shape(seen => seen.Submitting));
        Assert.Equal("up,down", record.Shape(seen => seen.Validating));
        Assert.Equal(2, validator.Runs);
    }

    // A rule registered a callback on its token that throws. The next check cancels that token
    // as it begins, either while the older check still waits or after it has answered. Every
    // callback still runs, so a waiting check's end runs inside the call, as it does when no
    // callback throws, and then the call throws. The check the call recorded never runs a rule, and it is ended: no flag stays
    // up, and an edit starts its own check rather than standing down for a check that never runs.
    // Mutation: cancel the older token in BeginPass, before RunPassAsync's try, and every row
    // leaves the recorded check current: the edit's check stands down, and in the rows whose
    // older check still waits IsValidating also stays true.
    // Mutation: end only a check that has published its begin (test IsValidating alone in
    // RunPassAsync's finally), and the rows whose older check had answered leave the recorded
    // check current, so the edit's check stands down.
    [Theory]
    [InlineData(CheckCall.Submit, true)]
    [InlineData(CheckCall.Load, true)]
    [InlineData(CheckCall.Submit, false)]
    [InlineData(CheckCall.Load, false)]
    public async Task A_cancellation_callback_that_throws_leaves_no_check_in_flight(CheckCall next, bool olderStillWaits)
    {
        var model = new Account();
        var editContext = new EditContext(model);
        var time = new FakeTimeProvider();
        var validator = new HeldValidator { ThrowsOnCancel = true, Hold = olderStillWaits };
        using var engine = new FormidableEngine<Account>(
            model, editContext, new FluentValidationModelValidator<Account>(validator),
            new ReflectionModelIntrospector(), new FormidableOptions(), time, work => work());
        var record = new FlagRecord(engine);

        var submit = WithoutContext(() => engine.ValidateForSubmitAsync());
        Assert.True(await UntilAsync(() => validator.Runs == 1));
        if (!olderStillWaits)
        {
            Assert.True((await submit).CanProceed);
            Assert.False(engine.IsValidating);
        }

        var call = WithoutContext(() => Start(engine, next));
        var thrown = await Assert.ThrowsAsync<AggregateException>(() => call);
        Assert.IsType<InvalidOperationException>(Assert.Single(thrown.InnerExceptions));
        validator.ThrowsOnCancel = false;
        if (olderStillWaits)
        {
            Assert.True(submit.IsCompleted);
            Assert.False((await submit).CanProceed);
        }

        Assert.False(engine.IsValidating);
        Assert.False(engine.IsSubmitting);
        Assert.Equal(1, validator.Runs);

        validator.Release();
        WithoutContext(() =>
        {
            model.Other = "y";
            editContext.NotifyFieldChanged(new FieldIdentifier(model, nameof(Account.Other)));
            return true;
        });
        Assert.True(await UntilAsync(() => validator.Runs == 2));
        Assert.True(await UntilAsync(() => !engine.IsValidating));
        Assert.Equal("up,down", record.Shape(seen => seen.Submitting));
    }

    /// <summary>What ends an edit's check before it runs a rule.</summary>
    public enum DeadCheck
    {
        /// <summary>The callback a rule left on the token of the check it cancels throws.</summary>
        CancellationCallbackThrows,

        /// <summary>A validation-state handler throws as the check's begin is published.</summary>
        BeginNotificationThrows,
    }

    // A live check that runs the submit profile counts, from its begin, as on its way to answering
    // IsFormValid for the edit it began at, so a validity check due for that edit stands down for
    // it. A load whose answer an edit outdated owes that validity check as it ends, and the live
    // check that follows the load is held. The next edit's check then ends before it runs a rule,
    // so it answers nothing and must not count: the validity check runs and answers the edit.
    // Mutation: keep the record where RunPassAsync ends a live check that did not get through its
    // begin, and both rows fail: the validity check stands down, and the rule never runs again.
    // Mutation: count the check as through its begin once its cancel returns, before the begin is
    // published, and the BeginNotificationThrows row fails the same way.
    [Theory]
    [InlineData(DeadCheck.CancellationCallbackThrows)]
    [InlineData(DeadCheck.BeginNotificationThrows)]
    public async Task An_edits_check_that_ends_before_it_runs_a_rule_leaves_IsFormValid_to_the_validity_check(DeadCheck dead)
    {
        var model = new Account();
        var editContext = new EditContext(model);
        var time = new FakeTimeProvider();
        var validator = new HeldValidator();
        using var engine = new FormidableEngine<Account>(
            model, editContext, new FluentValidationModelValidator<Account>(validator),
            new ReflectionModelIntrospector(), new FormidableOptions { TrackFormValidity = true }, time, work => work());
        Assert.True(await UntilAsync(() => engine.IsFormValid)); // the check at build
        var load = await HoldTheCheckAfterAnOutdatedLoadAsync(
            engine, editContext, model, validator, throwsOnCancel: dead == DeadCheck.CancellationCallbackThrows);

        var throwOnce = dead == DeadCheck.BeginNotificationThrows;
        editContext.OnValidationStateChanged += (_, _) =>
        {
            if (throwOnce)
            {
                throwOnce = false;
                throw new InvalidOperationException("The validation-state handler threw.");
            }
        };

        Edit(editContext, model, nameof(Account.Other), "y");
        validator.ThrowsOnCancel = false;
        Assert.False(engine.IsValidating);
        Assert.Equal(3, validator.Runs);
        await load;

        validator.Fails = true;
        validator.Release();
        WithoutContext(() =>
        {
            time.Advance(new FormidableOptions().RefreshDebounce);
            return true;
        });

        Assert.True(await UntilAsync(() => validator.Runs == 4));
        Assert.False(engine.IsFormValid);
    }

    // A check that runs its rule and meets a failure has answered with the fault, and a validity
    // check for the same edit would run the failing rule again, so the check still counts as that
    // edit's: the validity check the load owed stands down.
    // Mutation: drop the test of whether the check got through its begin where RunPassAsync ends a
    // live check that did not land, and the validity check runs the failing rule a fifth time.
    [Fact]
    public async Task A_rule_that_failed_is_not_run_again_by_the_validity_check_for_the_same_edit()
    {
        var model = new Account();
        var editContext = new EditContext(model);
        var time = new FakeTimeProvider();
        var validator = new HeldValidator();
        using var engine = new FormidableEngine<Account>(
            model, editContext, new FluentValidationModelValidator<Account>(validator),
            new ReflectionModelIntrospector(), new FormidableOptions { TrackFormValidity = true }, time, work => work());
        Assert.True(await UntilAsync(() => engine.IsFormValid)); // the check at build
        var load = await HoldTheCheckAfterAnOutdatedLoadAsync(engine, editContext, model, validator, throwsOnCancel: false);

        validator.Hold = false; // the next run answers, and the held one stays held
        validator.Throws = true;
        Edit(editContext, model, nameof(Account.Other), "y");
        validator.Throws = false;
        Assert.False(engine.IsValidating);
        Assert.Equal(4, validator.Runs);
        await load;

        WithoutContext(() =>
        {
            time.Advance(new FormidableOptions().RefreshDebounce);
            return true;
        });

        Assert.Equal(4, validator.Runs);
        Assert.True(engine.IsFormValid);
    }

    /// <summary>Runs a load that an edit outdates, which owes IsFormValid the validity check as it ends, and holds the live check that follows the load, its callbacks registered.</summary>
    /// <returns>The load, which ends with that live check.</returns>
    private static async Task<Task> HoldTheCheckAfterAnOutdatedLoadAsync(
        FormidableEngine<Account> engine, EditContext editContext, Account model, HeldValidator validator, bool throwsOnCancel)
    {
        validator.Hold = true;
        var load = WithoutContext(() => engine.DiscloseLoadedValuesAsync());
        Assert.True(await UntilAsync(() => validator.Held == 1));
        Assert.Equal(2, validator.Runs);
        Edit(editContext, model, nameof(Account.Other), "x");
        validator.ThrowsOnCancel = throwsOnCancel;
        validator.ReleaseHeld();
        Assert.True(await UntilAsync(() => validator.Held == 2));
        Assert.Equal(3, validator.Runs);
        Assert.True(engine.IsValidating);
        return load;
    }

    // A check that begins cancels the token of the check before it, and a callback a rule left on
    // that token can start a check of its own from inside the cancel: here it edits Name, and the
    // edit's check supersedes the beginning check before that check has recorded its fields. The
    // beginning check then records none, so the fields the check in flight covers stay its own,
    // and the next edit's check, which answers them in its place, lights Name.
    // Mutation: produce and record the scope in RunPassAsync whether or not the pass is still
    // current, and Name stays dark: the next check carries the superseded check's fields instead.
    [Fact]
    public async Task A_check_started_inside_a_cancel_keeps_its_fields_for_the_check_that_supersedes_it()
    {
        var model = new Account();
        var editContext = new EditContext(model);
        var validator = new HeldValidator();
        using var engine = new FormidableEngine<Account>(
            model, editContext, new FluentValidationModelValidator<Account>(validator),
            new ReflectionModelIntrospector(), new FormidableOptions(), new FakeTimeProvider(), work => work());
        var other = new FieldIdentifier(model, nameof(Account.Other));
        var name = new FieldIdentifier(model, nameof(Account.Name));

        validator.OnCancel = () => Edit(editContext, model, nameof(Account.Name), "n");
        Edit(editContext, model, nameof(Account.Other), "x"); // its check runs the rule and answers
        Assert.False(engine.IsValidating);
        validator.OnCancel = null;
        validator.Hold = true;

        Edit(editContext, model, nameof(Account.Other), "y");
        Assert.True(engine.IsValidating);
        Assert.True(engine.GetFieldState(name).IsValidating); // the check on Name, held

        Edit(editContext, model, nameof(Account.Other), "z");
        Assert.True(engine.GetFieldState(other).IsValidating);
        Assert.True(engine.GetFieldState(name).IsValidating);

        validator.Release();
        Assert.True(await UntilAsync(() => !engine.IsValidating));
    }

    // The same, with the beginning check a refresh. Its fields are the ones edited within its
    // window, and producing them takes them, so a refresh superseded inside its cancel must take
    // none: the refresh the callback's edit arms lights them all, that edit's field included.
    // Mutation: as above, and the next refresh lights neither field: the superseded one took both.
    [Fact]
    public async Task A_refresh_superseded_inside_its_cancel_leaves_its_fields_to_the_next_refresh()
    {
        var model = new Account();
        var editContext = new EditContext(model);
        var time = new FakeTimeProvider();
        var validator = new HeldValidator();
        using var engine = new FormidableEngine<Account>(
            model, editContext, new FluentValidationModelValidator<Account>(validator),
            new ReflectionModelIntrospector(), new FormidableOptions(), time, work => work());
        var other = new FieldIdentifier(model, nameof(Account.Other));
        var name = new FieldIdentifier(model, nameof(Account.Name));
        var debounce = new FormidableOptions().RefreshDebounce;
        Assert.True((await WithoutContext(() => engine.ValidateForSubmitAsync())).CanProceed);

        validator.OnCancel = () => Edit(editContext, model, nameof(Account.Name), "n");
        Edit(editContext, model, nameof(Account.Other), "x"); // its check answers; the refresh is armed
        Assert.False(engine.IsValidating);
        validator.OnCancel = null;

        // The refresh's begin cancels that token, and the callback's edit starts the check on Name,
        // which answers at once and arms the refresh again.
        WithoutContext(() =>
        {
            time.Advance(debounce);
            return true;
        });
        Assert.False(engine.IsValidating);

        var lit = new List<(bool Other, bool Name)>();
        engine.StateChanged += (_, _) =>
        {
            if (engine.IsValidating)
            {
                lit.Add((engine.GetFieldState(other).IsValidating, engine.GetFieldState(name).IsValidating));
            }
        };

        WithoutContext(() =>
        {
            time.Advance(debounce);
            return true;
        });

        Assert.True(await UntilAsync(() => !engine.IsValidating));
        Assert.Equal([(true, true)], lit);
    }

    // A narrowed live check that cancels a re-check hands the re-check's fields to the next
    // re-check, since it answers none of them under the submit profile. Here the re-check's
    // callback edits Name inside that cancel, and the edit's check supersedes the narrowed check
    // before that check records its fields. The hand-on is still made: the next re-check lights
    // the cancelled re-check's field beside the two fields edited since.
    // Mutation: drop the carry from the superseded branch of RunPassAsync's scope block, and the
    // next re-check leaves Other dark.
    [Fact]
    public async Task A_narrowed_check_superseded_inside_its_cancel_still_hands_the_recheck_on()
    {
        var model = new Account();
        var editContext = new EditContext(model);
        var time = new FakeTimeProvider();
        var validator = new HeldValidator();
        using var engine = new FormidableEngine<Account>(
            model, editContext, new FluentValidationModelValidator<Account>(validator),
            new ReflectionModelIntrospector(), new FormidableOptions { LiveProfile = ValidationProfile.Draft },
            time, work => work());
        var other = new FieldIdentifier(model, nameof(Account.Other));
        var name = new FieldIdentifier(model, nameof(Account.Name));
        var third = new FieldIdentifier(model, nameof(Account.Third));
        var debounce = new FormidableOptions().RefreshDebounce;
        Assert.True((await WithoutContext(() => engine.ValidateForSubmitAsync())).CanProceed);
        Edit(editContext, model, nameof(Account.Other), "x"); // the narrowed check answers; the re-check is armed
        Assert.False(engine.IsValidating);

        validator.Hold = true;
        validator.OnCancel = () => Edit(editContext, model, nameof(Account.Name), "n");
        WithoutContext(() =>
        {
            time.Advance(debounce);
            return true;
        });
        Assert.True(await UntilAsync(() => validator.Held == 1)); // the re-check, held
        Assert.True(engine.GetFieldState(other).IsValidating);
        validator.OnCancel = null;
        validator.Hold = false;

        Edit(editContext, model, nameof(Account.Third), "t");
        Assert.True(await UntilAsync(() => !engine.IsValidating));

        var lit = new List<(bool Other, bool Name, bool Third)>();
        engine.StateChanged += (_, _) =>
        {
            if (engine.IsValidating)
            {
                lit.Add((
                    engine.GetFieldState(other).IsValidating,
                    engine.GetFieldState(name).IsValidating,
                    engine.GetFieldState(third).IsValidating));
            }
        };

        WithoutContext(() =>
        {
            time.Advance(debounce);
            return true;
        });

        Assert.True(await UntilAsync(() => !engine.IsValidating));
        Assert.Equal([(true, true, true)], lit);
    }

    // A call after the engine's disposal throws, and no check runs: the message store the disposal
    // cleared stays empty however often the call is made, whether or not a check ran before.
    // Mutation: drop the disposed test at BeginPass's entry, and a call runs a check whose rule
    // rejects Other and refills the store: the first call when no check ran before the disposal,
    // the second when one did (the first then throws as it cancels the disposed token).
    [Theory]
    [InlineData(CheckCall.Submit, true)]
    [InlineData(CheckCall.Submit, false)]
    [InlineData(CheckCall.Load, true)]
    [InlineData(CheckCall.Load, false)]
    public async Task A_disposed_engine_runs_no_check_however_often_it_is_called(CheckCall late, bool checkedBefore)
    {
        var model = new Account { Other = "x" };
        var editContext = new EditContext(model);
        var validator = new HeldValidator { Fails = true };
        var engine = new FormidableEngine<Account>(
            model, editContext, new FluentValidationModelValidator<Account>(validator),
            new ReflectionModelIntrospector(), new FormidableOptions { DisclosureOverride = _ => true },
            new FakeTimeProvider(), work => work());
        if (checkedBefore)
        {
            Assert.False((await engine.ValidateForSubmitAsync()).CanProceed);
            Assert.NotEmpty(editContext.GetValidationMessages());
        }

        engine.Dispose();
        var runs = validator.Runs;
        Assert.Empty(editContext.GetValidationMessages());

        await Assert.ThrowsAsync<ObjectDisposedException>(() => Start(engine, late));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => Start(engine, late)); // and again

        Assert.Equal(runs, validator.Runs);
        Assert.Empty(editContext.GetValidationMessages());
        Assert.False(engine.IsValidating);
    }

    /// <summary>Which of the engine's tokens a rule leaves its throwing callback on before the disposal.</summary>
    public enum ThrowingToken
    {
        /// <summary>The last check's: an edit's check ran the rule and answered.</summary>
        Check,

        /// <summary>The validity check's: the check every tracked form runs as it is built ran the rule.</summary>
        ValidityCheck,

        /// <summary>Both: the check at build and an edit's check each ran the rule.</summary>
        Both,
    }

    // A rule left a callback on its token that throws, and the token outlives the rule's answer:
    // the last check's token stays the engine's until the next check, and the validity check's
    // token lives as long as the engine. Disposing cancels both, so the callback throws inside
    // Dispose. The disposal still finishes: the message store is emptied and the edit context
    // told, and the callback's throw then reaches the caller, every callback's in one
    // AggregateException, as a check's cancel of an overtaken check hands it on.
    // Mutation: cancel the tokens with no guard, as before, and every row keeps its message in
    // the store and raises no notification; the Both row also loses the validity check's throw.
    [Theory]
    [InlineData(ThrowingToken.Check)]
    [InlineData(ThrowingToken.ValidityCheck)]
    [InlineData(ThrowingToken.Both)]
    public void Dispose_finishes_its_cleanup_when_a_cancellation_callback_throws(ThrowingToken token)
    {
        var model = new Account { Other = "x" };
        var editContext = new EditContext(model);
        var validator = new HeldValidator { Fails = true, ThrowsOnCancel = token != ThrowingToken.Check };
        var engine = new FormidableEngine<Account>(
            model, editContext, new FluentValidationModelValidator<Account>(validator),
            new ReflectionModelIntrospector(),
            new FormidableOptions { TrackFormValidity = token != ThrowingToken.Check },
            new FakeTimeProvider(), work => work());
        var other = new FieldIdentifier(model, nameof(Account.Other));

        if (token == ThrowingToken.ValidityCheck)
        {
            Assert.Equal(1, validator.Runs); // the check at build ran the rule
            engine.ApplyServerIssues([new ValidationIssue(nameof(Account.Other), "Server says no")]);
        }
        else
        {
            validator.ThrowsOnCancel = true;
            editContext.NotifyFieldChanged(other); // its check runs the rule and answers at once
            Assert.False(engine.IsValidating);
        }

        Assert.NotEmpty(editContext.GetValidationMessages(other));
        var notified = 0;
        editContext.OnValidationStateChanged += (_, _) => notified++;

        var thrown = Assert.Throws<AggregateException>(engine.Dispose);

        Assert.All(thrown.InnerExceptions, inner => Assert.IsType<InvalidOperationException>(inner));
        Assert.Equal(token == ThrowingToken.Both ? 2 : 1, thrown.InnerExceptions.Count);
        Assert.Empty(editContext.GetValidationMessages());
        Assert.Equal(1, notified);
        Assert.False(engine.IsValidating);

        engine.Dispose(); // a second call does nothing
        Assert.Equal(1, notified);
    }

    // The state-changed notification is the disposal's last step, so a handler that throws on it
    // meets whatever the callbacks threw. The handler's throw joins theirs in the one
    // AggregateException instead of replacing them, and the cleanup before it has finished.
    // Mutation: notify with no guard, as before, and the handler's throw reaches the caller alone.
    [Fact]
    public void Dispose_raises_a_throwing_state_handlers_exception_with_the_callbacks()
    {
        var model = new Account { Other = "x" };
        var editContext = new EditContext(model);
        var validator = new HeldValidator { Fails = true, ThrowsOnCancel = true };
        var engine = new FormidableEngine<Account>(
            model, editContext, new FluentValidationModelValidator<Account>(validator),
            new ReflectionModelIntrospector(), new FormidableOptions(),
            new FakeTimeProvider(), work => work());
        var other = new FieldIdentifier(model, nameof(Account.Other));
        editContext.NotifyFieldChanged(other); // its check runs the rule and answers at once
        Assert.NotEmpty(editContext.GetValidationMessages(other));
        editContext.OnValidationStateChanged += (_, _) => throw new ArgumentException("handler");

        var thrown = Assert.Throws<AggregateException>(engine.Dispose);

        Assert.Equal(2, thrown.InnerExceptions.Count);
        Assert.Single(thrown.InnerExceptions, inner => inner is InvalidOperationException);
        Assert.Single(thrown.InnerExceptions, inner => inner is ArgumentException { Message: "handler" });
        Assert.Empty(editContext.GetValidationMessages());
    }

    // With no callback throw to join, the handler's exception is the disposal's own, not wrapped.
    // Mutation: collect the handler's throw whether or not a callback threw, and this arrives
    // as an AggregateException.
    [Fact]
    public void Dispose_raises_a_throwing_state_handlers_exception_unwrapped_when_no_callback_threw()
    {
        var model = new Account { Other = "x" };
        var editContext = new EditContext(model);
        var validator = new HeldValidator { Fails = true };
        var engine = new FormidableEngine<Account>(
            model, editContext, new FluentValidationModelValidator<Account>(validator),
            new ReflectionModelIntrospector(), new FormidableOptions(),
            new FakeTimeProvider(), work => work());
        var other = new FieldIdentifier(model, nameof(Account.Other));
        editContext.NotifyFieldChanged(other);
        Assert.NotEmpty(editContext.GetValidationMessages(other));
        editContext.OnValidationStateChanged += (_, _) => throw new ArgumentException("handler");

        var thrown = Assert.Throws<ArgumentException>(engine.Dispose);

        Assert.Equal("handler", thrown.Message);
        Assert.Empty(editContext.GetValidationMessages());
    }
}
