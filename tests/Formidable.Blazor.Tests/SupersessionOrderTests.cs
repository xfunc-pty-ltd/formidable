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
/// registered on its token can make it do, and a disposed engine starts no check at all.
/// </summary>
public class SupersessionOrderTests
{
    public sealed class Account
    {
        public string? Other { get; set; }
    }

    /// <summary>A counting async rule on Other, in the submit bucket, that waits on a gate while <see cref="Hold"/> is set and honours its token.</summary>
    private sealed class HeldValidator : DraftSubmitValidator<Account>
    {
        private volatile bool _hold;
        private volatile bool _fails;
        private volatile bool _throwsOnCancel;
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _runs;

        /// <summary>How many times the async rule has started.</summary>
        public int Runs => Volatile.Read(ref _runs);

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

        /// <summary>Lets every held run, and every later one, answer.</summary>
        public void Release()
        {
            Hold = false;
            _gate.TrySetResult();
        }

        protected override void ConfigureDraftRules()
        {
        }

        protected override void ConfigureSubmitRules() =>
            RuleFor(m => m.Other).MustAsync(async (_, ct) =>
            {
                Interlocked.Increment(ref _runs);
                if (ThrowsOnCancel)
                {
                    // Registered before the wait, so it runs after the wait's own callback.
                    ct.Register(() => throw new InvalidOperationException("The rule's cancellation callback threw."));
                }

                if (Hold)
                {
                    await _gate.Task.WaitAsync(ct);
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
}
