using System.Linq.Expressions;
using Bunit;
using FluentValidation;
using Formidable.Blazor.Tests.Fixtures;
using Formidable.Introspection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Formidable.Blazor.Tests;

/// <summary>
/// A visitor commits a change while a submit's check runs, so the answer the check reaches is for
/// the value before that change. The submit checks again before it reports, at most twice more,
/// so an edit made while it runs is judged as the same edit made just before Submit was pressed.
/// A field cleared while it runs blocks it; a field fixed while it runs lets it proceed. The async
/// rule runs no more often than for the same edit made just after the submit lands. Edits through
/// all three checks end the submit blocked: with nothing named when the last check passed, and on
/// that check's answer when it blocked. Every async rule counts each run as it starts.
/// </summary>
public class FormidableEngineOutdatedSubmitTests : BunitContext
{
    private const string NameRequired = "Name is required";

    private static readonly TimeSpan Window = TimeSpan.FromMilliseconds(200);

    private static readonly TimeSpan Refresh = new FormidableOptions().RefreshDebounce;

    // The clock the attach-mode and kit forms read from the container; engine-level forms build their own.
    private readonly FakeTimeProvider _kitTime = new();

    public FormidableEngineOutdatedSubmitTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<TimeProvider>(_kitTime);
        Services.AddFormidableBlazor();
    }

    public sealed class Account
    {
        public string? Name { get; set; } = "Bo";

        public string? Other { get; set; } = "x";
    }

    /// <summary>Name must hold more than white space, in the draft bucket, and counts each read; a counting async rule on Other in the submit bucket waits on a gate while <see cref="Hold"/> is set, so a submit reads Name before it waits.</summary>
    public sealed class HeldValidator : DraftSubmitValidator<Account>
    {
        private volatile bool _hold;
        private volatile TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _runs;
        private int _nameReads;

        /// <summary>How many times the async rule has started.</summary>
        public int Runs => Volatile.Read(ref _runs);

        /// <summary>How many times Name's rule has read the value.</summary>
        public int NameReads => Volatile.Read(ref _nameReads);

        /// <summary>Whether a run that starts waits on the gate.</summary>
        public bool Hold
        {
            get => _hold;
            set => _hold = value;
        }

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

        protected override void ConfigureDraftRules() =>
            RuleFor(m => m.Name).Must(name =>
            {
                Interlocked.Increment(ref _nameReads);
                return !string.IsNullOrWhiteSpace(name);
            }).WithMessage(NameRequired);

        protected override void ConfigureSubmitRules() =>
            RuleFor(m => m.Other).MustAsync(async (_, ct) =>
            {
                Interlocked.Increment(ref _runs);
                if (Hold)
                {
                    await _gate.Task.WaitAsync(ct);
                }

                return true;
            });
    }

    /// <summary>Hides the rule-by-rule seam, so every check is one whole-profile call that can reuse no stored answer.</summary>
    private sealed class WholeProfile(IModelValidator<Account> inner) : IModelValidator<Account>
    {
        public Task<ValidationReport> ValidateAsync(
            Account model, ValidationProfile profile, CancellationToken cancellationToken = default) =>
            inner.ValidateAsync(model, profile, cancellationToken);

        public ValidationReport Validate(Account model, ValidationProfile profile) => inner.Validate(model, profile);
    }

    /// <summary>The form each case builds.</summary>
    public enum Shape
    {
        /// <summary>The defaults, on the engine alone.</summary>
        Defaults,

        /// <summary><see cref="FormidableOptions.LiveProfile"/> narrowed to the draft rules.</summary>
        NarrowedLiveProfile,

        /// <summary>A 200 ms <see cref="FormidableOptions.LiveDebounce"/> window.</summary>
        LiveDebounceWindow,

        /// <summary><see cref="FormidableOptions.TrackFormValidity"/> on.</summary>
        TrackFormValidity,

        /// <summary>A validator that runs the whole profile in one call.</summary>
        WholeProfileValidator,

        /// <summary>A <see cref="FormidableValidator{TModel}"/> inside a page's own <see cref="EditForm"/>, submitted through its <c>ValidateForSubmitAsync()</c>.</summary>
        AttachMode,
    }

    /// <summary>When the case edits Name.</summary>
    public enum Timing
    {
        /// <summary>No edit.</summary>
        None,

        /// <summary>While the submit's async rule waits, after the check has read Name.</summary>
        During,

        /// <summary>Just after the submit has returned its outcome.</summary>
        After,
    }

    private sealed class Form
    {
        public required IFormidableEngine Engine { get; init; }

        public required Account Model { get; init; }

        public required HeldValidator Validator { get; init; }

        public required FakeTimeProvider Time { get; init; }

        /// <summary>Runs work where the form's own caller runs: under the engine's dispatch, or on the renderer.</summary>
        public required Func<Action, Task> OnDispatcher { get; init; }

        public required Func<Task<SubmitOutcome>> BeginSubmit { get; init; }

        public required Func<Task> Teardown { get; init; }

        public FieldIdentifier Name => new(Model, nameof(Account.Name));

        public Task EditAsync(string? name) => OnDispatcher(() =>
        {
            Model.Name = name;
            Engine.EditContext.NotifyFieldChanged(Name);
        });

        public string Messages() => string.Join("|", Engine.GetIssues(Name).Select(i => i.Message));
    }

    // Every engine dispatches one delegate at a time, as the renderer does. A released gate resumes
    // each held rule on a worker thread, so with inline dispatch two checks released together could
    // land on the engine at once, which a renderer never allows.
    private Form Build(Shape shape, string? name)
    {
        var model = new Account { Name = name };
        var validator = new HeldValidator();
        IModelValidator<Account> modelValidator = new FluentValidationModelValidator<Account>(validator);
        if (shape == Shape.WholeProfileValidator)
        {
            modelValidator = new WholeProfile(modelValidator);
        }

        var options = new FormidableOptions
        {
            DisclosureOverride = _ => true,
            TrackFormValidity = shape == Shape.TrackFormValidity,
            LiveProfile = shape == Shape.NarrowedLiveProfile ? ValidationProfile.Draft : null,
            LiveDebounce = shape == Shape.LiveDebounceWindow ? Window : null,
        };

        if (shape == Shape.AttachMode)
        {
            var editContext = new EditContext(model);
            var cut = Render(builder =>
            {
                builder.OpenComponent<EditForm>(0);
                builder.AddComponentParameter(1, "EditContext", editContext);
                builder.AddComponentParameter(2, "ChildContent", (RenderFragment<EditContext>)(_ => inner =>
                {
                    inner.OpenComponent<FormidableValidator<Account>>(0);
                    inner.AddComponentParameter(1, "Validator", modelValidator);
                    inner.AddComponentParameter(2, "Options", options);
                    inner.CloseComponent();
                }));
                builder.CloseComponent();
            });
            var attach = cut.FindComponent<FormidableValidator<Account>>();
            return new Form
            {
                Engine = attach.Instance.Engine!,
                Model = model,
                Validator = validator,
                Time = _kitTime,
                OnDispatcher = work => cut.InvokeAsync(work),
                BeginSubmit = () => cut.InvokeAsync(() => attach.Instance.ValidateForSubmitAsync()),
                Teardown = () => DisposeComponentsAsync(),
            };
        }

        var dispatch = EngineTestSync.OneAtATime();
        var time = new FakeTimeProvider();
        var engine = new FormidableEngine<Account>(
            model, new EditContext(model), modelValidator, new ReflectionModelIntrospector(), options,
            time, dispatch);
        return new Form
        {
            Engine = engine,
            Model = model,
            Validator = validator,
            Time = time,
            OnDispatcher = work => dispatch(() =>
            {
                work();
                return Task.CompletedTask;
            }),
            BeginSubmit = async () =>
            {
                Task<SubmitOutcome>? submit = null;
                await dispatch(() =>
                {
                    submit = engine.ValidateForSubmitAsync();
                    return Task.CompletedTask;
                });
                return await submit!;
            },
            Teardown = () =>
            {
                engine.Dispose();
                return Task.CompletedTask;
            },
        };
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

    /// <summary>Moves the clock past every debounce in steps, waits out any check still running, then moves it an hour on.</summary>
    private static async Task SettleAsync(Form form)
    {
        for (var step = 0; step < 10; step++)
        {
            await form.OnDispatcher(() => form.Time.Advance(TimeSpan.FromMilliseconds(100)));
            await Task.Delay(5);
        }

        Assert.True(await UntilAsync(() => !form.Engine.IsValidating));
        await form.OnDispatcher(() => form.Time.Advance(TimeSpan.FromHours(1)));
        await Task.Delay(20);
        Assert.True(await UntilAsync(() => !form.Engine.IsValidating));
    }

    /// <summary>Counts each time the engine starts validating from rest, read from its notifications.</summary>
    private static Func<int> CountChecksBegun(IFormidableEngine engine)
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

    /// <summary>What a case reports and shows, with the async rule's runs, Name's reads and the checks begun from the submit to the end.</summary>
    private sealed record Result(
        bool CanProceed,
        string Summary,
        string? NameAtOutcome,
        int ChecksBegunByOutcome,
        string Messages,
        bool IsFormValid,
        int Runs,
        int NameReads,
        int ChecksBegun);

    /// <summary>Builds the case's form with Name holding <paramref name="start"/>, engaged when it is filled, submits, and edits Name to <paramref name="edited"/> at <paramref name="timing"/>.</summary>
    private async Task<Result> RunAsync(Shape shape, string? start, string? edited, Timing timing)
    {
        var form = Build(shape, start);
        try
        {
            if (!string.IsNullOrEmpty(start))
            {
                await form.EditAsync(start); // engaged, with a valid value
                await SettleAsync(form);
            }

            var begun = CountChecksBegun(form.Engine);
            var runsBefore = form.Validator.Runs;
            var readsBefore = form.Validator.NameReads;
            form.Validator.Hold = true;
            var submit = form.BeginSubmit();
            if (timing == Timing.During)
            {
                Assert.True(await UntilAsync(() => form.Validator.Runs > runsBefore)); // Name read, the async rule waits
                await form.EditAsync(edited);
                await form.OnDispatcher(() => form.Time.Advance(TimeSpan.FromSeconds(1))); // every debounce comes due while the submit runs
            }

            form.Validator.Release();
            var outcome = await submit;
            var nameAtOutcome = form.Model.Name;
            var begunByOutcome = begun();
            if (timing == Timing.After)
            {
                await form.EditAsync(edited);
            }

            await SettleAsync(form);
            return new Result(
                outcome.CanProceed,
                string.Join("|", outcome.VisibleErrorSummary),
                nameAtOutcome,
                begunByOutcome,
                form.Messages(),
                form.Engine.IsFormValid,
                form.Validator.Runs - runsBefore,
                form.Validator.NameReads - readsBefore,
                begun());
        }
        finally
        {
            await form.Teardown();
        }
    }

    // A field cleared while the submit runs blocks it, and the summary names it, on every shape.
    // The form-wide check stays on through the check that answers the edit: it begins once and
    // never reads as finished between the two checks. The async rule runs as often as for the same
    // edit made just after the submit. With the restart the second run is the check that answers
    // the edit, and the re-check the edit armed then finds every rule answered, where after the
    // submit it is the edit's own live check. A whole-profile validator reuses nothing, so the
    // re-check costs it one more run either way.
    // Mutation: drop the landing's test for an edit made while the submit ran. Every row then
    // proceeds with the cleared value.
    [Theory]
    [InlineData(Shape.Defaults, 2)]
    [InlineData(Shape.NarrowedLiveProfile, 2)]
    [InlineData(Shape.LiveDebounceWindow, 2)]
    [InlineData(Shape.TrackFormValidity, 2)]
    [InlineData(Shape.WholeProfileValidator, 3)]
    [InlineData(Shape.AttachMode, 2)]
    public async Task A_field_cleared_during_a_submit_blocks_it(Shape shape, int runs)
    {
        var during = await RunAsync(shape, "Bo", string.Empty, Timing.During);
        var after = await RunAsync(shape, "Bo", string.Empty, Timing.After);

        Assert.False(during.CanProceed);
        Assert.Equal("Name", during.Summary);
        Assert.Equal(string.Empty, during.NameAtOutcome);
        Assert.Equal(1, during.ChecksBegunByOutcome);
        Assert.Equal(NameRequired, during.Messages);
        Assert.False(during.IsFormValid && shape == Shape.TrackFormValidity);
        Assert.Equal(runs, during.Runs);
        Assert.Equal(runs, during.NameReads);

        Assert.True(after.CanProceed);
        Assert.Equal("Bo", after.NameAtOutcome);
        Assert.Equal(after.Messages, during.Messages);
        Assert.Equal(after.Runs, during.Runs);
        Assert.True(
            during.ChecksBegun <= after.ChecksBegun,
            $"{during.ChecksBegun} checks begun with the edit during the submit against {after.ChecksBegun} after it");
    }

    // A field fixed while the submit runs lets it proceed with the fixed value, where the check
    // that read the empty value would have blocked on it, with a summary naming a field that is no
    // longer wrong. The async rule runs as often as for the same fix made just after the submit.
    // Mutation: drop the landing's test for an edit made while the submit ran. Every row then
    // blocks with the summary naming Name.
    [Theory]
    [InlineData(Shape.Defaults, 2)]
    [InlineData(Shape.NarrowedLiveProfile, 2)]
    [InlineData(Shape.LiveDebounceWindow, 2)]
    [InlineData(Shape.TrackFormValidity, 2)]
    [InlineData(Shape.WholeProfileValidator, 3)]
    [InlineData(Shape.AttachMode, 2)]
    public async Task A_field_fixed_during_a_submit_lets_it_proceed(Shape shape, int runs)
    {
        var during = await RunAsync(shape, string.Empty, "Bo", Timing.During);
        var after = await RunAsync(shape, string.Empty, "Bo", Timing.After);

        Assert.True(during.CanProceed);
        Assert.Equal(string.Empty, during.Summary);
        Assert.Equal("Bo", during.NameAtOutcome);
        Assert.Equal(1, during.ChecksBegunByOutcome);
        Assert.Equal(string.Empty, during.Messages);
        Assert.True(during.IsFormValid || shape != Shape.TrackFormValidity);
        Assert.Equal(runs, during.Runs);

        Assert.False(after.CanProceed);
        Assert.Equal("Name", after.Summary);
        Assert.Equal(after.Messages, during.Messages);
        Assert.Equal(after.Runs, during.Runs);
    }

    // A blur moves no value, so the submit's one check answers for everything, and nothing
    // runs twice. A whole-profile validator, which reuses nothing, shows a second check as a
    // second run.
    // Mutation: check again whether or not an edit was made. Both rows then run three checks.
    [Theory]
    [InlineData(Shape.Defaults)]
    [InlineData(Shape.WholeProfileValidator)]
    public async Task A_blur_with_no_commit_during_a_submit_restarts_nothing(Shape shape)
    {
        var form = Build(shape, "Bo");
        try
        {
            await form.EditAsync("Bo");
            await SettleAsync(form);

            var begun = CountChecksBegun(form.Engine);
            var runsBefore = form.Validator.Runs;
            var readsBefore = form.Validator.NameReads;
            form.Validator.Hold = true;
            var submit = form.BeginSubmit();
            Assert.True(await UntilAsync(() => form.Validator.Runs > runsBefore));
            await form.OnDispatcher(() => form.Engine.MarkTouched(form.Name)); // entered and left, nothing committed

            form.Validator.Release();
            Assert.True((await submit).CanProceed);
            await SettleAsync(form);

            Assert.Equal(1, form.Validator.Runs - runsBefore);
            Assert.Equal(1, form.Validator.NameReads - readsBefore);
            Assert.Equal(1, begun());
            Assert.Equal(string.Empty, form.Messages());
        }
        finally
        {
            await form.Teardown();
        }
    }

    // Controls. A submit with no edit runs one check and proceeds. An edit made after the submit
    // has returned its outcome does not reach it: the submit proceeds with the value it read, and
    // the edit is checked after it.
    // Mutation: check again whether or not an edit was made. The submit with no edit then runs
    // three checks.
    [Theory]
    [InlineData(Shape.Defaults, 1, 1, 2, 3)]
    [InlineData(Shape.WholeProfileValidator, 1, 1, 3, 3)]
    public async Task A_submit_with_no_edit_during_it_is_unchanged(
        Shape shape, int noEditRuns, int noEditChecks, int afterRuns, int afterChecks)
    {
        var none = await RunAsync(shape, "Bo", string.Empty, Timing.None);
        var after = await RunAsync(shape, "Bo", string.Empty, Timing.After);

        Assert.True(none.CanProceed);
        Assert.Equal("Bo", none.NameAtOutcome);
        Assert.Equal(string.Empty, none.Messages);
        Assert.Equal(noEditRuns, none.Runs);
        Assert.Equal(noEditChecks, none.ChecksBegun);

        Assert.True(after.CanProceed);
        Assert.Equal("Bo", after.NameAtOutcome);
        Assert.Equal(NameRequired, after.Messages);
        Assert.Equal(afterRuns, after.Runs);
        Assert.Equal(afterChecks, after.ChecksBegun);
    }

    /// <summary>What a submit with an edit made during each of its first checks reports and shows.</summary>
    private sealed record EditsResult(
        int EditsMade, bool CanProceed, string Summary, string? NameAtOutcome, string Messages, bool IsFormValid, int Runs);

    /// <summary>Submits, and while each of the first <paramref name="edits"/>.Length checks waits on the async rule, sets Name to the next value; every check reads Name before it waits.</summary>
    private static async Task<EditsResult> RunEditsAsync(Form form, string[] edits)
    {
        var runsBefore = form.Validator.Runs;
        form.Validator.Hold = true;
        var submit = form.BeginSubmit();
        var made = 0;
        foreach (var value in edits)
        {
            // The next check has read Name and waits on its own run of the async rule.
            if (!await UntilAsync(() => form.Validator.Runs > runsBefore + made || submit.IsCompleted)
                || submit.IsCompleted)
            {
                break;
            }

            await form.EditAsync(value);
            made++;
            if (made < edits.Length)
            {
                form.Validator.ReleaseHeldRuns();
            }
        }

        form.Validator.Release();
        var outcome = await submit;
        var nameAtOutcome = form.Model.Name;
        await SettleAsync(form);
        return new EditsResult(
            made,
            outcome.CanProceed,
            string.Join("|", outcome.VisibleErrorSummary),
            nameAtOutcome,
            form.Messages(),
            form.Engine.IsFormValid,
            form.Validator.Runs - runsBefore);
    }

    // An edit during each of the first two checks: the third check reads the last value and its
    // answer stands, whichever way it goes. Three checks run the async rule three times, and the
    // re-check the edits armed finds every rule answered.
    // Mutation: check again without reading the edit stamp again. Every answer then reads as
    // outdated, so the passing row ends blocked and the blocking row gives back a live check that
    // runs the async rule a fourth time.
    [Theory]
    [InlineData("", false)] // the last value blocks, and the summary names Name
    [InlineData("Cy", true)] // the last value passes, and the submit proceeds with it
    public async Task Edits_during_two_checks_answer_the_last_value(string last, bool proceeds)
    {
        var form = Build(Shape.Defaults, "Bo");
        try
        {
            await form.EditAsync("Bo");
            await SettleAsync(form);

            var result = await RunEditsAsync(form, ["Ann", last]);

            Assert.Equal(2, result.EditsMade);
            Assert.Equal(proceeds, result.CanProceed);
            Assert.Equal(last, result.NameAtOutcome);
            Assert.Equal(proceeds ? string.Empty : "Name", result.Summary);
            Assert.Equal(proceeds ? string.Empty : NameRequired, result.Messages);
            Assert.Equal(3, result.Runs);
        }
        finally
        {
            await form.Teardown();
        }
    }

    // An edit during each of the three checks the bound allows. The third check read a value that
    // passes, but an edit outdated it too, so the submit ends blocked with nothing named, even
    // though the last value passes as well. What the submit displaced is given back as for a
    // cancelled submit: the live check that stood down for it runs for the last value, and so runs
    // the async rule a fourth time.
    // Mutation: remove the bound. A fourth check then reads the last value and the submit proceeds.
    [Fact]
    public async Task Edits_during_three_checks_block_with_nothing_named()
    {
        var form = Build(Shape.Defaults, "Bo");
        try
        {
            await form.EditAsync("Bo");
            await SettleAsync(form);

            var result = await RunEditsAsync(form, ["Ann", "Bea", "Cy"]);

            Assert.Equal(3, result.EditsMade);
            Assert.False(result.CanProceed);
            Assert.Equal(string.Empty, result.Summary);
            Assert.Equal("Cy", result.NameAtOutcome);
            Assert.Equal(string.Empty, result.Messages);
            Assert.Equal(4, result.Runs);
        }
        finally
        {
            await form.Teardown();
        }
    }

    // The same bound, where the third check read a value that blocks. That answer lands as a
    // blocked submit always has, naming Name, though an edit outdated it: it lets nothing be
    // saved. Name's message then settles on the last value, which passes. (The live check given
    // back as the submit lands is pinned by EditDuringSubmitTests, on a field the landing does not
    // flag; here the re-check the edits armed corrects the flagged field as well.)
    // Mutation: end a blocking answer unanswered too once the bound is spent. The summary then
    // names nothing.
    [Fact]
    public async Task Edits_during_three_checks_land_a_blocking_answer_as_before()
    {
        var form = Build(Shape.Defaults, "Bo");
        try
        {
            await form.EditAsync("Bo");
            await SettleAsync(form);

            var result = await RunEditsAsync(form, ["Ann", string.Empty, "Cy"]);

            Assert.Equal(3, result.EditsMade);
            Assert.False(result.CanProceed);
            Assert.Equal("Name", result.Summary);
            Assert.Equal("Cy", result.NameAtOutcome);
            Assert.Equal(string.Empty, result.Messages);
            Assert.Equal(4, result.Runs);
        }
        finally
        {
            await form.Teardown();
        }
    }

    // A submit whose bound is spent gives back the live check that stood down for it, in the
    // dispatch after its end. The whole-form re-check the edits armed can come due between the two.
    // It waits for the give-back, as it waits for a check in flight, and runs after the live check
    // given back. So it is not cancelled by that live check, it finds every rule answered, and it
    // answers IsFormValid for the last value. Each row submits once first, so IsFormValid holds
    // that answer and a validity check behind the second submit would stand down for it.
    // Mutation: let the re-check's fire start between the end and the give-back. The live check
    // given back then cancels it and nothing starts it again: the async rule runs a fifth time,
    // and IsFormValid keeps the first submit's answer.
    [Theory]
    [InlineData("Bea")] // the third check read a passing value: the submit ends unanswered
    [InlineData("")] // the third check read an empty value: the blocking answer lands
    public async Task A_re_check_due_before_a_spent_submit_s_give_back_waits_for_it(string thirdCheckReads)
    {
        var model = new Account();
        var editContext = new EditContext(model);
        var validator = new HeldValidator();
        var time = new FakeTimeProvider();
        var dispatch = new HoldingDispatch();
        using var engine = new FormidableEngine<Account>(
            model, editContext, new FluentValidationModelValidator<Account>(validator),
            new ReflectionModelIntrospector(),
            new FormidableOptions { DisclosureOverride = _ => true, TrackFormValidity = true },
            time,
            dispatch.Dispatch);
        var form = new Form
        {
            Engine = engine,
            Model = model,
            Validator = validator,
            Time = time,
            OnDispatcher = work => dispatch.Dispatch(() =>
            {
                work();
                return Task.CompletedTask;
            }),
            BeginSubmit = () => engine.ValidateForSubmitAsync(),
            Teardown = () => Task.CompletedTask,
        };

        await form.EditAsync("Bo");
        await SettleAsync(form);
        Assert.True((await engine.ValidateForSubmitAsync()).CanProceed);
        await SettleAsync(form);
        Assert.True(engine.IsFormValid);

        // The spent submit's end holds the dispatch after it, where the give-back runs.
        var armed = false;
        engine.StateChanged += (_, _) =>
        {
            if (armed && !engine.IsValidating)
            {
                armed = false;
                dispatch.Holding = true;
            }
        };

        var runsBefore = validator.Runs;
        validator.Hold = true;
        var submit = engine.ValidateForSubmitAsync();
        string[] edits = ["Ann", thirdCheckReads, " "];
        for (var made = 0; made < edits.Length; made++)
        {
            Assert.True(await UntilAsync(() => validator.Runs > runsBefore + made));
            await form.EditAsync(edits[made]);
            if (made < edits.Length - 1)
            {
                validator.ReleaseHeldRuns();
            }
        }

        armed = true;
        validator.ReleaseHeldRuns(); // the third check ends, and the give-back waits behind it
        Assert.True(await UntilAsync(() => dispatch.Waiting == 1));

        dispatch.Holding = false;
        time.Advance(Refresh); // the re-check comes due between the end and the give-back
        dispatch.Drain(); // the give-back: Name's live check runs for the last value
        validator.Release();
        Assert.False((await submit).CanProceed);
        await SettleAsync(form);

        Assert.Equal(NameRequired, form.Messages());
        Assert.Equal(4, validator.Runs - runsBefore);
        Assert.False(engine.IsFormValid);
    }

    // A caller's cancellation during a repeat check ends the submit as one during its first check
    // does: the call throws, nothing lands, IsValidating falls, and what the submit displaced is
    // given back. Here that is the live check the edit stood down, which shows Name's message. The
    // record the first check opened is kept across the restart, so the edit made during the first
    // check is given back from the second. The async rule runs once per check, then once for the
    // live check given back.
    // Mutation: open a fresh record at each restart. The row cancelled during the repeat check then
    // gives nothing back, and Name shows nothing.
    [Theory]
    [InlineData(1, 2)] // cancelled during the first check
    [InlineData(2, 3)] // cancelled during the repeat check the edit started
    public async Task A_submit_cancelled_during_a_repeat_check_ends_as_one_cancelled_during_its_first(
        int cancelledDuringCheck, int runs)
    {
        var form = Build(Shape.Defaults, "Bo");
        try
        {
            await form.EditAsync("Bo");
            await SettleAsync(form);

            var runsBefore = form.Validator.Runs;
            form.Validator.Hold = true;
            using var cancel = new CancellationTokenSource();
            Task<SubmitOutcome>? submit = null;
            await form.OnDispatcher(() => submit = form.Engine.ValidateForSubmitAsync(cancel.Token));
            Assert.True(await UntilAsync(() => form.Validator.Runs == runsBefore + 1));
            await form.EditAsync(string.Empty); // its live check stands down for the submit
            if (cancelledDuringCheck == 2)
            {
                form.Validator.ReleaseHeldRuns(); // the first check's answer predates the edit
                Assert.True(await UntilAsync(() => form.Validator.Runs == runsBefore + 2));
            }

            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => submit!);
            form.Validator.Release();
            await SettleAsync(form);

            Assert.False(form.Engine.IsValidating);
            Assert.False(form.Engine.HasSubmitted);
            Assert.Equal(NameRequired, form.Messages());
            Assert.Equal(runs, form.Validator.Runs - runsBefore);
        }
        finally
        {
            await form.Teardown();
        }
    }

    // The wait above holds until the give-back of the spent submit that raised it, whatever else
    // ends in between. Here a commit lands between the spent submit's end and its give-back. Under
    // a LiveProfile narrowed to synchronous draft rules, its live check begins and ends at once, and
    // its end arms the waiting re-check at a RefreshDebounce of zero. That re-check still waits for
    // the give-back, then runs after it, re-checks Name, and clears the message the outdated
    // landing put on Name. The async rule runs five times: the three checks, the commit's
    // validity check, and the re-check.
    // Mutation: lower the wait at the end of every check, as the commit's live check ends. The
    // re-check then starts in between and the live check given back cancels it, along with a
    // second re-check the commit armed. Nothing starts one again, so Name keeps the message for
    // the value before the commit, and the async rule runs six times.
    [Fact]
    public async Task A_check_ending_between_a_spent_submit_and_its_give_back_leaves_the_re_check_waiting()
    {
        var model = new Account();
        var editContext = new EditContext(model);
        var validator = new HeldValidator();
        var time = new FakeTimeProvider();
        var dispatch = new HoldingDispatch();
        using var engine = new FormidableEngine<Account>(
            model, editContext, new FluentValidationModelValidator<Account>(validator),
            new ReflectionModelIntrospector(),
            new FormidableOptions
            {
                DisclosureOverride = _ => true,
                TrackFormValidity = true,
                LiveProfile = ValidationProfile.Draft,
                RefreshDebounce = TimeSpan.Zero,
            },
            time,
            dispatch.Dispatch);
        var form = new Form
        {
            Engine = engine,
            Model = model,
            Validator = validator,
            Time = time,
            OnDispatcher = work => dispatch.Dispatch(() =>
            {
                work();
                return Task.CompletedTask;
            }),
            BeginSubmit = () => engine.ValidateForSubmitAsync(),
            Teardown = () => Task.CompletedTask,
        };

        await form.EditAsync("Bo");
        await SettleAsync(form);

        // The spent submit's end holds the dispatch after it, where the give-back runs.
        var armed = false;
        engine.StateChanged += (_, _) =>
        {
            if (armed && !engine.IsValidating)
            {
                armed = false;
                dispatch.Holding = true;
            }
        };

        var runsBefore = validator.Runs;
        validator.Hold = true;
        var submit = engine.ValidateForSubmitAsync();
        string[] edits = ["Ann", string.Empty, " "]; // the third check reads "" and blocks
        for (var made = 0; made < edits.Length; made++)
        {
            Assert.True(await UntilAsync(() => validator.Runs > runsBefore + made));
            await form.EditAsync(edits[made]);
            if (made < edits.Length - 1)
            {
                validator.ReleaseHeldRuns();
            }
        }

        armed = true;
        validator.ReleaseHeldRuns(); // the blocking answer lands, and the give-back waits behind it
        Assert.True(await UntilAsync(() => dispatch.Waiting == 1));
        Assert.Equal([NameRequired], engine.GetIssues(form.Name).Select(i => i.Message));

        dispatch.Holding = false;
        await form.EditAsync("Cy"); // its live check begins and ends before the give-back
        dispatch.Drain();
        validator.Release();
        Assert.False((await submit).CanProceed);
        await SettleAsync(form);

        Assert.Equal(string.Empty, form.Messages());
        Assert.Equal(5, validator.Runs - runsBefore);
        Assert.True(engine.IsFormValid);
    }

    /// <summary>An account whose normalization trims Name.</summary>
    public sealed class TrimmedAccount : INormalizableModel
    {
        public string? Name { get; set; } = "Bo";

        public string? Other { get; set; } = "x";

        public void Normalize() => Name = Name?.Trim();
    }

    /// <summary>Name at most four characters long, in the draft bucket; a counting async rule on Other in the submit bucket waits on <see cref="Gate"/>.</summary>
    private sealed class TrimmedValidator : DraftSubmitValidator<TrimmedAccount>
    {
        private int _runs;

        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Runs => Volatile.Read(ref _runs);

        protected override void ConfigureDraftRules() =>
            RuleFor(m => m.Name).MaximumLength(4);

        protected override void ConfigureSubmitRules() =>
            RuleFor(m => m.Other).MustAsync(async (_, ct) =>
            {
                Interlocked.Increment(ref _runs);
                await Gate.Task.WaitAsync(ct);
                return true;
            });
    }

    // Under NormalizeOnSubmit a submit normalizes the model before each check it makes, so a value
    // an edit brings while the first check runs is normalized before the check that answers it
    // reads it. Here the edit's value is too long until it is trimmed.
    // Mutation: check again without normalizing first. That check reads the untrimmed value and
    // blocks on it.
    [Fact]
    public async Task A_submit_normalizes_again_before_it_checks_an_edit()
    {
        var model = new TrimmedAccount();
        var editContext = new EditContext(model);
        var validator = new TrimmedValidator();
        using var engine = new FormidableEngine<TrimmedAccount>(
            model, editContext, new FluentValidationModelValidator<TrimmedAccount>(validator),
            new ReflectionModelIntrospector(),
            new FormidableOptions { NormalizeOnSubmit = true, DisclosureOverride = _ => true },
            new FakeTimeProvider(),
            EngineTestSync.OneAtATime());

        var submit = engine.ValidateForSubmitAsync();
        Assert.True(await UntilAsync(() => validator.Runs == 1)); // Name read, the async rule waits
        model.Name = "  Cy  ";
        editContext.NotifyFieldChanged(new FieldIdentifier(model, nameof(TrimmedAccount.Name)));
        validator.Gate.SetResult();
        var outcome = await submit;

        Assert.True(outcome.CanProceed);
        Assert.Equal("Cy", model.Name);
        Assert.Equal(2, validator.Runs);
    }

    /// <summary>What the kit shows for Name once everything settles, with what the submit's callbacks saw and the async rule's runs.</summary>
    private sealed record KitResult(string Calls, string Message, bool IsFormValid, int Runs);

    // Through the kit: a real form whose input registers its field, with no override. A value
    // cleared while the submit runs never reaches OnValidSubmit; a value fixed while it runs does.
    // Mutation: drop the landing's test for an edit made while the submit ran. The cleared row then
    // reaches OnValidSubmit with Name empty, and the fixed row reaches OnInvalidSubmit.
    [Theory]
    [InlineData("Bo", "", "invalid(Name='', summary=[Name])", NameRequired, false)]
    [InlineData("", "Bo", "valid(Name='Bo')", "", true)]
    public async Task Through_the_kit_OnValidSubmit_runs_only_on_the_value_the_model_holds(
        string start, string edited, string calls, string message, bool isFormValid)
    {
        var during = await RunKitAsync(start, edited, editDuring: true);
        var after = await RunKitAsync(start, edited, editDuring: false);

        Assert.Equal(calls, during.Calls);
        Assert.Equal(message, during.Message);
        Assert.Equal(isFormValid, during.IsFormValid);
        Assert.Equal(after.Message, during.Message);
        Assert.Equal(after.Runs, during.Runs);
    }

    private async Task<KitResult> RunKitAsync(string start, string edited, bool editDuring)
    {
        var model = new Account { Name = start };
        var validator = new HeldValidator();
        var calls = new List<string>();

        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<Account>>(0);
            builder.AddComponentParameter(1, "Model", model);
            builder.AddComponentParameter(2, "Validator", new FluentValidationModelValidator<Account>(validator));
            builder.AddComponentParameter(3, "Options", new FormidableOptions { TrackFormValidity = true });
            builder.AddComponentParameter(4, "OnValidSubmit", EventCallback.Factory.Create<SubmitOutcome>(
                this, _ => calls.Add($"valid(Name='{model.Name}')")));
            builder.AddComponentParameter(5, "OnInvalidSubmit", EventCallback.Factory.Create<FormidableInvalidSubmitContext>(
                this, c => calls.Add($"invalid(Name='{model.Name}', summary=[{string.Join("|", c.Outcome.VisibleErrorSummary)}])")));
            builder.AddComponentParameter(6, "ChildContent", (RenderFragment<FormidableFormContext>)(_ => inner =>
            {
                inner.OpenComponent<FormidableInputText>(0);
                inner.AddComponentParameter(1, "For", (Expression<Func<string?>>)(() => model.Name));
                inner.AddComponentParameter(2, "Value", model.Name);
                inner.AddComponentParameter(3, "ValueChanged", EventCallback.Factory.Create<string?>(this, v => model.Name = v));
                inner.CloseComponent();

                inner.OpenComponent<FormidableFieldMessage<string?>>(4);
                inner.AddComponentParameter(5, "For", (Expression<Func<string?>>)(() => model.Name));
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        });

        var form = cut.FindComponent<FormidableForm<Account>>();
        var engine = form.Instance.Engine!;

        async Task Settle()
        {
            for (var step = 0; step < 10; step++)
            {
                await cut.InvokeAsync(() => _kitTime.Advance(TimeSpan.FromMilliseconds(100)));
                await Task.Delay(5);
            }

            Assert.True(await UntilAsync(() => !engine.IsValidating));
            await cut.InvokeAsync(() => _kitTime.Advance(TimeSpan.FromHours(1)));
            await Task.Delay(20);
            Assert.True(await UntilAsync(() => !engine.IsValidating));
        }

        if (start.Length > 0)
        {
            await cut.InvokeAsync(() => cut.Find("input").Change(start)); // engaged, valid
            await Settle();
        }

        var runsBefore = validator.Runs;
        validator.Hold = true;
        var submit = cut.InvokeAsync(() => form.Instance.SubmitAsync());
        Assert.True(await UntilAsync(() => validator.Runs > runsBefore)); // Name read, the async rule waits
        if (editDuring)
        {
            await cut.InvokeAsync(() => cut.Find("input").Change(edited));
        }

        await cut.InvokeAsync(validator.Release);
        await submit;
        if (!editDuring)
        {
            await cut.InvokeAsync(() => cut.Find("input").Change(edited));
        }

        await Settle();

        var result = new KitResult(
            string.Join(";", calls),
            string.Join("|", cut.FindComponent<FormidableFieldMessage<string?>>().Nodes.Select(n => n.TextContent.Trim()).Where(t => t.Length > 0)),
            engine.IsFormValid,
            validator.Runs - runsBefore);
        await DisposeComponentsAsync();
        return result;
    }
}
