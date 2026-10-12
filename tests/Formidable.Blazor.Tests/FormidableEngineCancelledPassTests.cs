using FluentValidation;
using Formidable.Blazor.Tests.Fixtures;
using Formidable.Introspection;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Time.Testing;

namespace Formidable.Blazor.Tests;

/// <summary>
/// A submit or a load that ends without an answer (its caller cancels it, or its validator throws)
/// gives back what it displaced. As it began it cancelled the check that was running (an edit's
/// live check, or the whole-form re-check after a submit or a server reply), and while it ran an
/// edit's own live check stood down for it. Its end gives each of those checks back: the re-check
/// starts again, and the live check runs for the fields it was answering (through the debounce
/// window when <see cref="FormidableOptions.LiveDebounce"/> is set).
/// <see cref="FormidableEngine{TModel}.IsFormValid"/> is owed a check only where it can be stale:
/// a load, a cancelled re-check, or an edit since the submit or load began. That check stands down
/// when something already on its way answers. A submit or load that lands gives back none of the
/// checks it cancelled, because its answer stands in for them. A submit checks again for an edit
/// made while it runs (<see cref="FormidableEngineOutdatedSubmitTests"/>). Once those restarts are
/// spent, a blocking answer the edit outdated lands and gives back the live check the edit stood
/// down, which <see cref="EditDuringSubmitTests"/> covers. Every async rule here counts each run as
/// it starts, and runs once for each check owed.
/// </summary>
public class FormidableEngineCancelledPassTests
{
    private const string NameRequired = "Name is required";

    // A value the draft rules reject, so a load holding it marks Name as a field with a value and an error.
    private const string NotAName = "?";

    private const string NotANameMessage = "Name is not a name";

    private static readonly TimeSpan Refresh = new FormidableOptions().RefreshDebounce;

    public sealed class Recheck
    {
        public string? Name { get; set; } = string.Empty;

        public string? Other { get; set; } = "x";
    }

    /// <summary>Name's rule in the draft bucket; a counting async rule on Other in the submit bucket, held on a gate while <see cref="Hold"/> is set.</summary>
    private sealed class HeldAsyncValidator : DraftSubmitValidator<Recheck>
    {
        private volatile bool _hold;
        private int _runs;

        /// <summary>How many times the async rule has started.</summary>
        public int Runs => Volatile.Read(ref _runs);

        /// <summary>Whether a run that starts waits on the gate.</summary>
        public bool Hold
        {
            get => _hold;
            set => _hold = value;
        }

        /// <summary>The run, counting from one, that throws as it starts; none while zero.</summary>
        public int ThrowOnRun { get; set; }

        private volatile TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Lets every held run, and every later one, answer.</summary>
        public void Release()
        {
            Hold = false;
            _gate.TrySetResult();
        }

        /// <summary>Lets the runs held now answer, and holds every later run on a fresh gate.</summary>
        public void ReleaseHeldRuns()
        {
            var held = _gate;
            _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            held.TrySetResult();
        }

        protected override void ConfigureDraftRules()
        {
            RuleFor(m => m.Name).NotEmpty().WithMessage(NameRequired);
            RuleFor(m => m.Name).Must(name => name != NotAName).WithMessage(NotANameMessage);
        }

        protected override void ConfigureSubmitRules() =>
            RuleFor(m => m.Other).MustAsync(async (_, ct) =>
            {
                if (Interlocked.Increment(ref _runs) == ThrowOnRun)
                {
                    throw new InvalidOperationException("The async rule failed.");
                }

                if (Hold)
                {
                    await _gate.Task.WaitAsync(ct);
                }

                return true;
            });
    }

    /// <summary>Hides the rule-by-rule seam, so every check is one whole-profile call that can reuse no stored answer.</summary>
    private sealed class WholeProfileValidator(IModelValidator<Recheck> inner) : IModelValidator<Recheck>
    {
        private int _calls;

        /// <summary>How many whole-profile calls have been made.</summary>
        public int Calls => Volatile.Read(ref _calls);

        public Task<ValidationReport> ValidateAsync(
            Recheck model, ValidationProfile profile, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            return inner.ValidateAsync(model, profile, cancellationToken);
        }

        public ValidationReport Validate(Recheck model, ValidationProfile profile)
        {
            Interlocked.Increment(ref _calls);
            return inner.Validate(model, profile);
        }
    }

    private sealed record Form(
        FormidableEngine<Recheck> Engine,
        EditContext EditContext,
        Recheck Model,
        HeldAsyncValidator Validator,
        FakeTimeProvider Time) : IDisposable
    {
        public FieldIdentifier Name => new(Model, nameof(Recheck.Name));

        public void Edit(string? name)
        {
            Model.Name = name;
            EditContext.NotifyFieldChanged(Name);
        }

        public List<string> NameMessages() => [.. Engine.GetIssues(Name).Select(i => i.Message)];

        public List<string> NameStore() => [.. EditContext.GetValidationMessages(Name)];

        public void Dispose() => Engine.Dispose();
    }

    // Every engine here dispatches one delegate at a time, as the renderer does. A released gate
    // resumes each held rule on a worker thread, so with inline dispatch two checks released
    // together would land on the engine at once, which a renderer never allows.
    private static Form Build(FormidableOptions options, Recheck? model = null)
    {
        model ??= new Recheck();
        var editContext = new EditContext(model);
        var validator = new HeldAsyncValidator();
        var time = new FakeTimeProvider();
        var engine = new FormidableEngine<Recheck>(
            model, editContext, new FluentValidationModelValidator<Recheck>(validator),
            new ReflectionModelIntrospector(), options, time, EngineTestSync.OneAtATime());
        return new Form(engine, editContext, model, validator, time);
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

    /// <summary>Counts each time the engine starts validating from rest, read from its notifications.</summary>
    private static Func<int> CountChecksBegun(FormidableEngine<Recheck> engine)
    {
        var begun = 0;
        var wasValidating = engine.IsValidating;
        engine.StateChanged += (_, _) =>
        {
            if (engine.IsValidating && !wasValidating)
            {
                Interlocked.Increment(ref begun);
            }

            wasValidating = engine.IsValidating;
        };
        return () => Volatile.Read(ref begun);
    }

    /// <summary>A form on the defaults whose Name shows "Name is required" from a live check, and whose next edit fills Name while the async rule is held.</summary>
    private static async Task<Form> FixedWhileHeldAsync()
    {
        var form = Build(new FormidableOptions { DisclosureOverride = _ => true }, new Recheck { Name = "x" });
        form.Edit(string.Empty);
        Assert.True(await UntilAsync(() => !form.Engine.IsValidating));
        Assert.Equal([NameRequired], form.NameMessages());

        form.Validator.Hold = true;
        form.Edit("Bo"); // its live check starts and waits on the async rule
        Assert.True(form.Engine.IsValidating);
        return form;
    }

    // Mutation: skip the give-back when the submit ends without an answer. Nothing runs the live
    // check again, so the message the empty Name earned stays under "Bo".
    [Fact]
    public async Task A_live_check_cancelled_by_a_cancelled_submit_reruns_and_clears_a_fixed_field()
    {
        using var form = await FixedWhileHeldAsync();

        using var cancel = new CancellationTokenSource();
        var submit = form.Engine.ValidateForSubmitAsync(cancel.Token); // cancels the live check
        var beforeCancel = form.Validator.Runs;
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => submit);

        form.Validator.Release();
        Assert.True(await UntilAsync(() => !form.Engine.IsValidating));

        Assert.Equal("Bo", form.Model.Name);
        Assert.Empty(form.NameMessages());
        Assert.Empty(form.NameStore());
        Assert.Equal(beforeCancel + 1, form.Validator.Runs);

        form.Time.Advance(TimeSpan.FromHours(1));
        await Task.Delay(50);
        Assert.Equal(beforeCancel + 1, form.Validator.Runs);
    }

    // Mutation: skip the give-back when the load ends without an answer, and the message stays.
    [Fact]
    public async Task A_live_check_cancelled_by_a_cancelled_load_reruns()
    {
        using var form = await FixedWhileHeldAsync();

        using var cancel = new CancellationTokenSource();
        var load = form.Engine.DiscloseLoadedValuesAsync(cancel.Token); // cancels the live check
        var beforeCancel = form.Validator.Runs;
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load);

        form.Validator.Release();
        Assert.True(await UntilAsync(() => !form.Engine.IsValidating));

        Assert.Empty(form.NameMessages());
        Assert.Empty(form.NameStore());
        Assert.Equal(beforeCancel + 1, form.Validator.Runs);

        form.Time.Advance(TimeSpan.FromHours(1));
        await Task.Delay(50);
        Assert.Equal(beforeCancel + 1, form.Validator.Runs);
    }

    // Mutation: drop the record of a live check that stood down for the load. The edit made while
    // the load ran is then never checked, and "Name is required" stays under "Bo".
    [Fact]
    public async Task An_edit_during_a_load_the_caller_cancels_is_checked_once()
    {
        using var form = Build(new FormidableOptions { DisclosureOverride = _ => true }, new Recheck { Name = "x" });
        form.Edit(string.Empty);
        Assert.True(await UntilAsync(() => !form.Engine.IsValidating));
        Assert.Equal([NameRequired], form.NameMessages());

        form.Validator.Hold = true;
        using var cancel = new CancellationTokenSource();
        var load = form.Engine.DiscloseLoadedValuesAsync(cancel.Token);
        Assert.True(form.Engine.IsValidating);

        var beforeEdit = form.Validator.Runs;
        form.Edit("Bo"); // its live check stands down for the load
        Assert.Equal(beforeEdit, form.Validator.Runs);

        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load);
        form.Validator.Release();
        Assert.True(await UntilAsync(() => !form.Engine.IsValidating));

        Assert.Empty(form.NameMessages());
        Assert.Empty(form.NameStore());
        Assert.Equal(beforeEdit + 1, form.Validator.Runs);

        form.Time.Advance(TimeSpan.FromHours(1));
        await Task.Delay(50);
        Assert.Equal(beforeEdit + 1, form.Validator.Runs);
    }

    // The live check runs the draft rules alone, so only the whole-form re-check after the blocked
    // submit can clear the submit's message.
    // Mutation: skip restarting the re-check when the submit ends without an answer. The blocked
    // submit's "Name is required" then stays under "Bo" until the next edit or submit.
    [Fact]
    public async Task A_recheck_cancelled_by_a_cancelled_submit_restarts()
    {
        using var form = Build(new FormidableOptions
        {
            DisclosureOverride = _ => true,
            LiveProfile = ValidationProfile.Draft,
        });

        var blocked = await form.Engine.ValidateForSubmitAsync();
        Assert.False(blocked.CanProceed);
        Assert.Equal([NameRequired], form.NameMessages());

        form.Validator.Hold = true;
        form.Edit("Bo"); // the draft-only live check passes; the re-check is armed
        Assert.Equal([NameRequired], form.NameMessages());
        form.Time.Advance(Refresh); // the re-check starts and waits on the async rule
        Assert.True(form.Engine.IsValidating);

        using var cancel = new CancellationTokenSource();
        var submit = form.Engine.ValidateForSubmitAsync(cancel.Token); // cancels the re-check
        var beforeCancel = form.Validator.Runs;
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => submit);
        Assert.False(form.Engine.IsValidating); // given back behind RefreshDebounce, not started yet
        Assert.Equal(beforeCancel, form.Validator.Runs);

        form.Validator.Release();
        form.Time.Advance(Refresh);
        Assert.True(await UntilAsync(() => !form.Engine.IsValidating));

        Assert.Empty(form.NameMessages());
        Assert.Empty(form.NameStore());
        Assert.Equal(beforeCancel + 1, form.Validator.Runs);

        form.Time.Advance(TimeSpan.FromHours(1));
        await Task.Delay(50);
        Assert.Equal(beforeCancel + 1, form.Validator.Runs);
    }

    /// <summary>A form under a window that never closes, after a server reply, whose edit fills Name and whose re-check is running, held on the async rule.</summary>
    private static async Task<Form> RecheckRunningUnderANeverClosingWindowAsync()
    {
        var form = Build(new FormidableOptions
        {
            TrackFormValidity = true,
            LiveDebounce = Timeout.InfiniteTimeSpan,
        });
        Assert.True(await UntilAsync(() => form.Validator.Runs >= 1)); // the check every tracked form runs as it is built
        await Task.Delay(50);
        Assert.False(form.Engine.IsFormValid);

        form.Engine.ApplyServerIssues([new ValidationIssue(nameof(Recheck.Other), "Taken")]);
        form.Validator.Hold = true;
        form.Edit("Bo"); // the window holds it for good; the re-check is armed
        form.Time.Advance(Refresh); // the re-check starts and waits on the async rule
        Assert.True(form.Engine.IsValidating);
        return form;
    }

    // Mutation: skip restarting the re-check, and also skip owing IsFormValid a check, when the
    // submit ends without an answer. Nothing under this window then checks the model again, and
    // IsFormValid stays false on a valid model.
    [Fact]
    public async Task Under_an_infinite_live_debounce_IsFormValid_answers_after_a_cancelled_submit()
    {
        using var form = await RecheckRunningUnderANeverClosingWindowAsync();

        using var cancel = new CancellationTokenSource();
        var submit = form.Engine.ValidateForSubmitAsync(cancel.Token); // cancels the re-check
        var beforeCancel = form.Validator.Runs;
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => submit);

        form.Validator.Release();
        form.Time.Advance(Refresh);
        Assert.True(await UntilAsync(() => form.Engine.IsFormValid));

        Assert.True(form.Engine.IsFormValid);
        Assert.Equal(beforeCancel + 1, form.Validator.Runs);

        form.Time.Advance(TimeSpan.FromHours(1));
        await Task.Delay(50);
        Assert.Equal(beforeCancel + 1, form.Validator.Runs);
    }

    // Mutation: as above, for a load.
    [Fact]
    public async Task Under_an_infinite_live_debounce_IsFormValid_answers_after_a_cancelled_load()
    {
        using var form = await RecheckRunningUnderANeverClosingWindowAsync();

        using var cancel = new CancellationTokenSource();
        var load = form.Engine.DiscloseLoadedValuesAsync(cancel.Token); // cancels the re-check
        var beforeCancel = form.Validator.Runs;
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load);

        form.Validator.Release();
        form.Time.Advance(Refresh);
        Assert.True(await UntilAsync(() => form.Engine.IsFormValid));

        Assert.True(form.Engine.IsFormValid);
        Assert.Equal(beforeCancel + 1, form.Validator.Runs);

        form.Time.Advance(TimeSpan.FromHours(1));
        await Task.Delay(50);
        Assert.Equal(beforeCancel + 1, form.Validator.Runs);
    }

    // Nothing is running as the load begins, so there is no re-check to restart. The load still
    // said the model holds new values, and IsFormValid answered for the values before them.
    // Mutation: skip owing IsFormValid a check when the load ends without an answer. IsFormValid
    // then stays true on a model whose Name is empty.
    [Fact]
    public async Task IsFormValid_answers_after_a_cancelled_load_that_displaced_nothing()
    {
        using var form = Build(new FormidableOptions { TrackFormValidity = true }, new Recheck { Name = "Bo" });
        Assert.True(await UntilAsync(() => form.Engine.IsFormValid));

        form.Model.Name = string.Empty; // written straight onto the model, as a page loading a record does
        form.Validator.Hold = true;
        using var cancel = new CancellationTokenSource();
        var load = form.Engine.DiscloseLoadedValuesAsync(cancel.Token);
        var beforeCancel = form.Validator.Runs;
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load);
        Assert.True(form.Engine.IsFormValid); // the check is owed behind RefreshDebounce

        form.Validator.Release();
        form.Time.Advance(Refresh);
        Assert.True(await UntilAsync(() => !form.Engine.IsFormValid));

        Assert.False(form.Engine.IsFormValid);
        Assert.Equal(beforeCancel + 1, form.Validator.Runs);

        form.Time.Advance(TimeSpan.FromHours(1));
        await Task.Delay(50);
        Assert.Equal(beforeCancel + 1, form.Validator.Runs);
    }

    // After an answered submit, every later edit arms the whole-form re-check, which is why the
    // validity check stands down for an answered submit. A load's own change arms nothing, so the
    // check the cancelled load owes must still run.
    // Mutation: restore the plain answered-submit stand-down. The owed check then stands down, and
    // IsFormValid stays true on a model whose Name is empty.
    [Fact]
    public async Task IsFormValid_answers_a_cancelled_load_after_an_answered_submit()
    {
        using var form = Build(new FormidableOptions { TrackFormValidity = true }, new Recheck { Name = "Bo" });
        Assert.True(await UntilAsync(() => form.Engine.IsFormValid));
        Assert.True((await form.Engine.ValidateForSubmitAsync()).CanProceed);

        form.Model.Name = string.Empty; // written straight onto the model, as a page loading a record does
        form.Validator.Hold = true;
        using var cancel = new CancellationTokenSource();
        var load = form.Engine.DiscloseLoadedValuesAsync(cancel.Token);
        var beforeCancel = form.Validator.Runs;
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load);
        Assert.True(form.Engine.IsFormValid); // the check is owed behind RefreshDebounce

        form.Validator.Release();
        form.Time.Advance(Refresh);
        Assert.True(await UntilAsync(() => !form.Engine.IsFormValid));

        Assert.False(form.Engine.IsFormValid);
        Assert.Equal(beforeCancel + 1, form.Validator.Runs);

        form.Time.Advance(TimeSpan.FromHours(1));
        await Task.Delay(50);
        Assert.Equal(beforeCancel + 1, form.Validator.Runs);
    }

    // A submit has answered, and the whole-form re-check has answered the edit made while it ran,
    // so IsFormValid already answers the model as it stands. A submit the caller then cancels
    // changes nothing. This validator can reuse no stored answer, so a check here would run the
    // async rule a second time for the same model.
    // Mutation: owe IsFormValid a check at every end without an answer, and let every owed check
    // pass the answered-submit stand-down, not only one owed for a load's change. The rule then
    // runs a second time. Either half alone leaves one run: the cancel then owes nothing, or the
    // check it owes stands down.
    [Fact]
    public async Task A_cancelled_submit_on_an_answered_form_runs_no_second_check()
    {
        var model = new Recheck { Name = "Bo" };
        var editContext = new EditContext(model);
        var validator = new HeldAsyncValidator();
        var whole = new WholeProfileValidator(new FluentValidationModelValidator<Recheck>(validator));
        var time = new FakeTimeProvider();
        using var engine = new FormidableEngine<Recheck>(
            model, editContext, whole, new ReflectionModelIntrospector(),
            new FormidableOptions { TrackFormValidity = true }, time, EngineTestSync.OneAtATime());
        var name = new FieldIdentifier(model, nameof(Recheck.Name));
        Assert.True(await UntilAsync(() => engine.IsFormValid));

        validator.Hold = true;
        var answered = engine.ValidateForSubmitAsync();
        model.Name = "Bob";
        editContext.NotifyFieldChanged(name); // while the submit runs: the re-check is armed
        validator.Release();
        Assert.True((await answered).CanProceed);
        time.Advance(Refresh); // the re-check runs and answers IsFormValid for "Bob"
        Assert.True(await UntilAsync(() => !engine.IsValidating));
        await Task.Delay(50);
        Assert.True(engine.IsFormValid);

        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.ValidateForSubmitAsync(cancel.Token));
        var runs = validator.Runs;
        var calls = whole.Calls; // the cancelled submit's own call is behind these

        time.Advance(TimeSpan.FromHours(1));
        await Task.Delay(50);
        Assert.Equal(runs, validator.Runs);
        Assert.Equal(calls, whole.Calls);
        Assert.True(engine.IsFormValid);
    }

    // The check a cancelled load owes after an answered submit comes due while a later submit runs,
    // and waits for it. That submit lands and answers IsFormValid itself, so the waiting check has
    // nothing left to answer. This validator can reuse no stored answer, so running it would run
    // the async rule a second time for the same model.
    // Mutation: keep the owed check's claim on the answered-submit stand-down when a submit lands.
    // The check then runs behind the landing, and the rule runs a second time.
    [Fact]
    public async Task An_owed_check_waiting_on_a_submit_that_lands_runs_nothing_more()
    {
        var model = new Recheck { Name = "Bo" };
        var editContext = new EditContext(model);
        var validator = new HeldAsyncValidator();
        var whole = new WholeProfileValidator(new FluentValidationModelValidator<Recheck>(validator));
        var time = new FakeTimeProvider();
        using var engine = new FormidableEngine<Recheck>(
            model, editContext, whole, new ReflectionModelIntrospector(),
            new FormidableOptions { TrackFormValidity = true }, time, EngineTestSync.OneAtATime());
        Assert.True(await UntilAsync(() => engine.IsFormValid));
        Assert.True((await engine.ValidateForSubmitAsync()).CanProceed);

        model.Name = string.Empty; // written straight onto the model, as a page loading a record does
        validator.Hold = true;
        using (var cancel = new CancellationTokenSource())
        {
            var load = engine.DiscloseLoadedValuesAsync(cancel.Token);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load);
        }

        var submit = engine.ValidateForSubmitAsync(); // waits on the async rule
        time.Advance(Refresh); // the owed check comes due and waits for the submit
        var runs = validator.Runs;
        var calls = whole.Calls;
        validator.Release();
        Assert.False((await submit).CanProceed);
        Assert.False(engine.IsFormValid);

        time.Advance(TimeSpan.FromHours(1));
        await Task.Delay(50);
        Assert.Equal(runs, validator.Runs);
        Assert.Equal(calls, whole.Calls);
        Assert.False(engine.IsFormValid);
    }

    // With both debounces never passing, an owed check has no timer to wait on, so it starts at
    // once, with no stand-down. A submit has answered and nothing has changed since, so IsFormValid
    // already answers the model as it stands, and a submit the caller cancels with nothing running
    // owes nothing. This validator can reuse no stored answer, so a check would run the async rule
    // a second time for the same model.
    // Mutation: owe IsFormValid a check at every end without an answer. The check then starts at
    // once, and the rule runs a second time.
    [Fact]
    public async Task Under_debounces_that_never_pass_a_cancelled_submit_that_changed_nothing_runs_no_check()
    {
        var model = new Recheck { Name = "Bo" };
        var editContext = new EditContext(model);
        var validator = new HeldAsyncValidator();
        var whole = new WholeProfileValidator(new FluentValidationModelValidator<Recheck>(validator));
        var time = new FakeTimeProvider();
        using var engine = new FormidableEngine<Recheck>(
            model, editContext, whole, new ReflectionModelIntrospector(),
            new FormidableOptions
            {
                TrackFormValidity = true,
                LiveDebounce = Timeout.InfiniteTimeSpan,
                RefreshDebounce = Timeout.InfiniteTimeSpan,
            },
            time,
            EngineTestSync.OneAtATime());
        var name = new FieldIdentifier(model, nameof(Recheck.Name));
        Assert.True(await UntilAsync(() => engine.IsFormValid));

        model.Name = "Bob";
        editContext.NotifyFieldChanged(name); // its validity check is armed behind a wait that never passes
        Assert.True((await engine.ValidateForSubmitAsync()).CanProceed); // answers IsFormValid for "Bob"

        validator.Hold = true;
        using var cancel = new CancellationTokenSource();
        var submit = engine.ValidateForSubmitAsync(cancel.Token); // its run waits on the async rule
        var runs = validator.Runs;
        var calls = whole.Calls;
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => submit);
        validator.Release();

        time.Advance(TimeSpan.FromHours(1));
        await Task.Delay(50);
        Assert.Equal(runs, validator.Runs);
        Assert.Equal(calls, whole.Calls);
        Assert.True(engine.IsFormValid);
    }

    // Under a RefreshDebounce that never passes, the re-check an edit arms never runs, so the check
    // the cancelled submit owes is the only thing left to answer IsFormValid for an edit made while
    // it ran. The given-back live check and that check each run the async rule once, as the edit
    // itself would have started both with no submit running.
    // Mutation: drop "an edit since the record opened" from the cases that owe IsFormValid a check.
    // IsFormValid then stays true on a model whose Name is empty.
    [Fact]
    public async Task Under_a_RefreshDebounce_that_never_passes_an_edit_during_a_cancelled_submit_answers_IsFormValid()
    {
        using var form = Build(
            new FormidableOptions { TrackFormValidity = true, RefreshDebounce = Timeout.InfiniteTimeSpan },
            new Recheck { Name = "Bo" });
        Assert.True(await UntilAsync(() => form.Engine.IsFormValid));

        form.Validator.Hold = true;
        using var cancel = new CancellationTokenSource();
        var submit = form.Engine.ValidateForSubmitAsync(cancel.Token);
        form.Edit(string.Empty); // while the submit runs: its live check and its validity check stand down
        var beforeCancel = form.Validator.Runs;
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => submit);
        form.Validator.Release();

        Assert.True(await UntilAsync(() => !form.Engine.IsFormValid));
        Assert.False(form.Engine.IsFormValid);
        Assert.True(await UntilAsync(() => !form.Engine.IsValidating));
        Assert.Equal([NameRequired], form.NameMessages());
        Assert.Equal(beforeCancel + 2, form.Validator.Runs);

        form.Time.Advance(TimeSpan.FromHours(1));
        await Task.Delay(50);
        Assert.Equal(beforeCancel + 2, form.Validator.Runs);
    }

    // The re-check the cancelled submit cancelled was going to answer IsFormValid. The page sets
    // RefreshDebounce to a wait that never passes while it runs, so the restarted re-check never
    // fires, and only the check the cancel owes answers IsFormValid.
    // Mutation: drop "a cancelled re-check" from the cases that owe IsFormValid a check.
    // IsFormValid then stays false on a valid model.
    [Fact]
    public async Task A_cancelled_recheck_that_cannot_restart_still_has_IsFormValid_answered()
    {
        var options = new FormidableOptions
        {
            TrackFormValidity = true,
            LiveDebounce = Timeout.InfiniteTimeSpan,
        };
        using var form = Build(options);
        Assert.True(await UntilAsync(() => form.Validator.Runs >= 1)); // the check every tracked form runs as it is built
        await Task.Delay(50);
        Assert.False(form.Engine.IsFormValid);

        form.Engine.ApplyServerIssues([new ValidationIssue(nameof(Recheck.Other), "Taken")]);
        form.Validator.Hold = true;
        form.Edit("Bo"); // the window holds it for good; the re-check is armed
        form.Time.Advance(Refresh); // the re-check starts and waits on the async rule
        Assert.True(form.Engine.IsValidating);

        using var cancel = new CancellationTokenSource();
        var submit = form.Engine.ValidateForSubmitAsync(cancel.Token); // cancels the re-check
        options.RefreshDebounce = Timeout.InfiniteTimeSpan;
        var beforeCancel = form.Validator.Runs;
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => submit);
        form.Validator.Release();

        Assert.True(await UntilAsync(() => form.Engine.IsFormValid));
        Assert.True(form.Engine.IsFormValid);
        Assert.Equal(beforeCancel + 1, form.Validator.Runs);

        form.Time.Advance(TimeSpan.FromHours(1));
        await Task.Delay(50);
        Assert.Equal(beforeCancel + 1, form.Validator.Runs);
    }

    // A load lands and answers IsFormValid itself. The live check that follows it is running when a
    // submit cancels it, and the caller then cancels that submit. The live check is given back, but
    // nothing has changed since the load answered, so IsFormValid is owed nothing. This validator
    // can reuse no stored answer, so a check would run the async rule a second time.
    // Mutation: count a cancelled live check among the cases that owe IsFormValid a check. The
    // owed check then runs the rule beside the given-back live check.
    [Fact]
    public async Task A_live_check_after_a_load_cancelled_by_a_cancelled_submit_owes_IsFormValid_nothing()
    {
        var model = new Recheck { Name = "Bo" };
        var editContext = new EditContext(model);
        var validator = new HeldAsyncValidator();
        var whole = new WholeProfileValidator(new FluentValidationModelValidator<Recheck>(validator));
        var time = new FakeTimeProvider();
        using var engine = new FormidableEngine<Recheck>(
            model, editContext, whole, new ReflectionModelIntrospector(),
            new FormidableOptions { TrackFormValidity = true }, time, EngineTestSync.OneAtATime());
        Assert.True(await UntilAsync(() => engine.IsFormValid));

        model.Name = NotAName; // written straight onto the model: a value the rules reject
        validator.Hold = true;
        var beforeLoad = validator.Runs;
        var load = engine.DiscloseLoadedValuesAsync();
        validator.ReleaseHeldRuns(); // the load lands; the live check that follows it starts and waits
        Assert.True(await UntilAsync(() => validator.Runs == beforeLoad + 2 && engine.IsValidating));
        Assert.False(engine.IsFormValid); // the load's own answer

        using var cancel = new CancellationTokenSource();
        var submit = engine.ValidateForSubmitAsync(cancel.Token); // cancels the live check
        var beforeCancel = validator.Runs;
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => submit);
        validator.Release();
        await load;
        Assert.True(await UntilAsync(() => !engine.IsValidating));

        Assert.Equal([NotANameMessage], engine.GetIssues(new FieldIdentifier(model, nameof(Recheck.Name))).Select(i => i.Message));
        Assert.Equal(beforeCancel + 1, validator.Runs); // the given-back live check alone

        time.Advance(TimeSpan.FromHours(1));
        await Task.Delay(50);
        Assert.Equal(beforeCancel + 1, validator.Runs);
        Assert.False(engine.IsFormValid);
    }

    /// <summary>A tracked form on a validator that can reuse no stored answer, holding a valid model.</summary>
    private static (FormidableEngine<Recheck> Engine, EditContext EditContext, Recheck Model, HeldAsyncValidator Validator, WholeProfileValidator Whole, FakeTimeProvider Time)
        BuildWholeProfile(TimeSpan? liveDebounce = null)
    {
        var model = new Recheck { Name = "Bob" };
        var editContext = new EditContext(model);
        var validator = new HeldAsyncValidator();
        var whole = new WholeProfileValidator(new FluentValidationModelValidator<Recheck>(validator));
        var time = new FakeTimeProvider();
        var engine = new FormidableEngine<Recheck>(
            model, editContext, whole, new ReflectionModelIntrospector(),
            new FormidableOptions { TrackFormValidity = true, LiveDebounce = liveDebounce },
            time, EngineTestSync.OneAtATime());
        return (engine, editContext, model, validator, whole, time);
    }

    // No submit has answered. A load the caller cancels owes IsFormValid a check. A second load
    // then lands with no edit since it began, so its answer is IsFormValid's for the model as it
    // stands, and the owed check, waiting for that load or still to come due, has nothing left to
    // answer. This validator can reuse no stored answer, so running it would run the async rule a
    // second time.
    // Mutation: keep a validity check armed or waiting when a load's answer is adopted. The check
    // then runs behind the landing, and the rule runs a second time. Mutation: drop only a check
    // that waited for the load, leaving the timer of one still to come due armed. The row where the
    // load lands first then runs the rule a second time.
    [Theory]
    [InlineData(true)] // the check comes due while the second load runs, and waits for it
    [InlineData(false)] // the second load lands before the check comes due
    public async Task A_check_owed_before_a_load_that_lands_runs_nothing_more(bool dueDuringLoad)
    {
        var (engine, _, _, validator, whole, time) = BuildWholeProfile();
        using var disposeEngine = engine;
        Assert.True(await UntilAsync(() => engine.IsFormValid));

        validator.Hold = true;
        using (var cancel = new CancellationTokenSource())
        {
            var cancelled = engine.DiscloseLoadedValuesAsync(cancel.Token);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled); // owes a check behind RefreshDebounce
        }

        var load = engine.DiscloseLoadedValuesAsync(); // waits on the async rule
        if (dueDuringLoad)
        {
            time.Advance(Refresh); // the owed check comes due and waits for the load
        }

        var runs = validator.Runs;
        var calls = whole.Calls;
        validator.Release();
        await load;
        Assert.True(await UntilAsync(() => !engine.IsValidating));
        Assert.True(engine.IsFormValid);

        time.Advance(TimeSpan.FromHours(1));
        await Task.Delay(50);
        Assert.Equal(runs, validator.Runs);
        Assert.Equal(calls, whole.Calls);
        Assert.True(engine.IsFormValid);
    }

    // On a dispatcher that queues work, the validity timer can fire and post its check while a
    // load is landing, and the landing's drop of the check (its answer is adopted) cannot recall
    // that post. The fire then runs after the drop, for a check with nothing left to answer. This
    // validator can reuse no stored answer, so a check would run the async rule a second time.
    // Mutation: remove the fire's test for a check that is no longer armed. The queued fire then
    // runs the probe, and the rule runs a second time.
    [Fact]
    public async Task A_validity_fire_queued_before_a_landing_drops_its_check_runs_nothing()
    {
        var model = new Recheck { Name = "Bob" };
        var editContext = new EditContext(model);
        var validator = new HeldAsyncValidator();
        var whole = new WholeProfileValidator(new FluentValidationModelValidator<Recheck>(validator));
        var time = new FakeTimeProvider();
        var dispatch = new HoldingDispatch();
        using var engine = new FormidableEngine<Recheck>(
            model, editContext, whole, new ReflectionModelIntrospector(),
            new FormidableOptions { TrackFormValidity = true }, time, dispatch.Dispatch);
        Assert.True(await UntilAsync(() => engine.IsFormValid));

        validator.Hold = true;
        using (var cancel = new CancellationTokenSource())
        {
            var cancelled = engine.DiscloseLoadedValuesAsync(cancel.Token);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled); // owes a check behind RefreshDebounce
        }

        var load = engine.DiscloseLoadedValuesAsync(); // waits on the async rule
        dispatch.Holding = true;
        time.Advance(Refresh); // the owed check's timer fires; its dispatch waits in the queue
        dispatch.Holding = false;
        Assert.Equal(1, dispatch.Waiting);

        var runs = validator.Runs;
        var calls = whole.Calls;
        validator.Release();
        await load; // lands with its answer adopted, and drops the check
        Assert.True(await UntilAsync(() => !engine.IsValidating));
        Assert.True(engine.IsFormValid);

        dispatch.Drain(); // the queued fire runs after the drop
        time.Advance(TimeSpan.FromHours(1));
        await Task.Delay(50);
        Assert.Equal(runs, validator.Runs);
        Assert.Equal(calls, whole.Calls);
        Assert.True(engine.IsFormValid);
    }

    // LiveDebounce never closes and no submit has answered, so an edit made while a load runs arms
    // the validity check. The load lands, but that edit has outdated its answer, so the check,
    // waiting for the load or still to come due, is the edit's answer. A check still to come due
    // keeps its own time, RefreshDebounce after the edit. The live check that follows the load and
    // the validity check each run the async rule once.
    // Mutation: drop a validity check armed or waiting at every load landing, outdated or not. The
    // outdated landing then arms a check of its own, due RefreshDebounce after the landing rather
    // than after the edit, so in the row where the check is still to come due nothing has answered
    // the edit when its own check was due.
    [Theory]
    [InlineData(true)] // the check comes due while the load runs, and waits for it
    [InlineData(false)] // the load lands halfway through the check's wait
    public async Task A_check_armed_by_an_edit_during_a_load_answers_that_edit(bool dueDuringLoad)
    {
        var (engine, editContext, model, validator, whole, time) = BuildWholeProfile(Timeout.InfiniteTimeSpan);
        using var disposeEngine = engine;
        Assert.True(await UntilAsync(() => engine.IsFormValid));
        var half = TimeSpan.FromTicks(Refresh.Ticks / 2);

        validator.Hold = true;
        var load = engine.DiscloseLoadedValuesAsync(); // waits on the async rule
        model.Name = string.Empty;
        editContext.NotifyFieldChanged(new FieldIdentifier(model, nameof(Recheck.Name))); // arms the check
        time.Advance(dueDuringLoad ? Refresh : half); // when due, the check waits for the load

        var runs = validator.Runs;
        var calls = whole.Calls;
        validator.Release();
        await load;
        Assert.True(await UntilAsync(() => !engine.IsValidating));
        Assert.Equal(runs + 1, validator.Runs); // the live check that follows the load
        Assert.True(engine.IsFormValid); // the load's answer predates the edit, so nothing has adopted it

        // RefreshDebounce after the landing re-armed the check that waited; RefreshDebounce after
        // the edit for the one still to come due.
        time.Advance(dueDuringLoad ? Refresh : Refresh - half);
        Assert.True(await UntilAsync(() => !engine.IsFormValid));
        Assert.False(engine.IsFormValid);
        Assert.Equal(runs + 2, validator.Runs);
        Assert.Equal(calls + 2, whole.Calls);

        time.Advance(TimeSpan.FromHours(1));
        await Task.Delay(50);
        Assert.Equal(runs + 2, validator.Runs);
        Assert.False(engine.IsFormValid);
    }

    // Mutation: drop the window route, so the live check is never given back under a window, and
    // the message stays. Mutation: run it at once whatever the window, and it starts before the
    // window passes.
    [Fact]
    public async Task A_debounced_live_check_cancelled_by_a_cancelled_submit_reruns_after_the_window()
    {
        var window = TimeSpan.FromMilliseconds(200);
        using var form = Build(
            new FormidableOptions { DisclosureOverride = _ => true, LiveDebounce = window },
            new Recheck { Name = "x" });
        form.Edit(string.Empty);
        form.Time.Advance(window);
        Assert.True(await UntilAsync(() => !form.Engine.IsValidating));
        Assert.Equal([NameRequired], form.NameMessages());

        form.Validator.Hold = true;
        form.Edit("Bo");
        form.Time.Advance(window); // the window's live check starts and waits on the async rule
        Assert.True(form.Engine.IsValidating);

        using var cancel = new CancellationTokenSource();
        var submit = form.Engine.ValidateForSubmitAsync(cancel.Token); // cancels the live check
        var beforeCancel = form.Validator.Runs;
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => submit);
        form.Validator.Release();

        form.Time.Advance(window - TimeSpan.FromTicks(1));
        await Task.Delay(50);
        Assert.False(form.Engine.IsValidating);
        Assert.Equal(beforeCancel, form.Validator.Runs);
        Assert.Equal([NameRequired], form.NameMessages());

        form.Time.Advance(TimeSpan.FromTicks(1)); // the window passes
        Assert.True(await UntilAsync(() => form.NameMessages().Count == 0 && !form.Engine.IsValidating));

        Assert.Empty(form.NameMessages());
        Assert.Empty(form.NameStore());
        Assert.Equal(beforeCancel + 1, form.Validator.Runs);

        form.Time.Advance(TimeSpan.FromHours(1));
        await Task.Delay(50);
        Assert.Equal(beforeCancel + 1, form.Validator.Runs);
    }

    // The submit's own run of the async rule throws, so the submit ends without an answer, as a
    // cancelled one does, and the throw reaches its caller.
    // Mutation: give back only when the caller cancelled. The live check is then never run again,
    // and the message the empty Name earned stays under "Bo".
    [Fact]
    public async Task A_live_check_cancelled_by_a_submit_whose_validator_throws_reruns()
    {
        using var form = await FixedWhileHeldAsync();

        var beforeSubmit = form.Validator.Runs;
        form.Validator.ThrowOnRun = beforeSubmit + 1; // the submit's run
        await Assert.ThrowsAsync<InvalidOperationException>(() => form.Engine.ValidateForSubmitAsync());

        form.Validator.Release();
        Assert.True(await UntilAsync(() => !form.Engine.IsValidating));

        Assert.Empty(form.NameMessages());
        Assert.Empty(form.NameStore());
        Assert.Equal(beforeSubmit + 2, form.Validator.Runs); // the submit's run, then the live check given back

        form.Time.Advance(TimeSpan.FromHours(1));
        await Task.Delay(50);
        Assert.Equal(beforeSubmit + 2, form.Validator.Runs);
    }

    // Control: nothing supersedes the live check, which lands and clears the message itself.
    [Fact]
    public async Task A_live_check_nothing_supersedes_clears_a_fixed_field()
    {
        using var form = await FixedWhileHeldAsync();
        var held = form.Validator.Runs;

        form.Validator.Release();
        Assert.True(await UntilAsync(() => !form.Engine.IsValidating));

        Assert.Empty(form.NameMessages());
        Assert.Empty(form.NameStore());
        Assert.Equal(held, form.Validator.Runs);

        form.Time.Advance(TimeSpan.FromHours(1));
        await Task.Delay(50);
        Assert.Equal(held, form.Validator.Runs);
    }

    // Control: the submit that cancelled the re-check lands, and its answer covers what the
    // re-check would have said, so nothing starts after it, nor after a later submit the caller
    // cancels before it runs.
    // Mutation: give back what a landing submit displaced, as an unanswered one does. The re-check
    // then starts again behind the landing. Mutation: leave the record standing when a submit
    // lands. The later cancelled submit then takes it over and restarts the re-check.
    [Fact]
    public async Task Under_an_infinite_live_debounce_a_submit_that_answers_gives_nothing_back()
    {
        using var form = await RecheckRunningUnderANeverClosingWindowAsync();

        var submit = form.Engine.ValidateForSubmitAsync(); // cancels the re-check
        var beforeLanding = form.Validator.Runs;
        form.Validator.Release();
        Assert.True((await submit).CanProceed);
        Assert.True(form.Engine.IsFormValid);

        var begun = CountChecksBegun(form.Engine);
        form.Time.Advance(TimeSpan.FromHours(1));
        await Task.Delay(50);

        Assert.Equal(0, begun());
        Assert.Equal(beforeLanding, form.Validator.Runs);
        Assert.True(form.Engine.IsFormValid);

        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => form.Engine.ValidateForSubmitAsync(cancel.Token));
        var begunAfterCancel = CountChecksBegun(form.Engine);
        form.Time.Advance(TimeSpan.FromHours(1));
        await Task.Delay(50);

        Assert.Equal(0, begunAfterCancel());
        Assert.Equal(beforeLanding, form.Validator.Runs);
        Assert.True(form.Engine.IsFormValid);
    }

    // Control: the load lands, and the live check that follows it answers the edit the visitor
    // made while it ran.
    // Mutation: keep the record when a load lands. A second live check then starts beside the one
    // that follows the load.
    [Fact]
    public async Task An_edit_during_a_load_that_finishes_is_checked_once()
    {
        using var form = Build(new FormidableOptions { DisclosureOverride = _ => true }, new Recheck { Name = "x" });
        form.Edit(string.Empty);
        Assert.True(await UntilAsync(() => !form.Engine.IsValidating));
        Assert.Equal([NameRequired], form.NameMessages());

        form.Validator.Hold = true;
        var load = form.Engine.DiscloseLoadedValuesAsync();
        Assert.True(form.Engine.IsValidating);

        var beforeEdit = form.Validator.Runs;
        var begun = CountChecksBegun(form.Engine);
        form.Edit("Bo"); // its live check stands down for the load

        form.Validator.Release();
        await load;
        Assert.True(await UntilAsync(() => !form.Engine.IsValidating));

        Assert.Empty(form.NameMessages());
        Assert.Empty(form.NameStore());
        Assert.Equal(1, begun());
        Assert.Equal(beforeEdit + 1, form.Validator.Runs);

        form.Time.Advance(TimeSpan.FromHours(1));
        await Task.Delay(50);
        Assert.Equal(1, begun());
        Assert.Equal(beforeEdit + 1, form.Validator.Runs);
    }
}
