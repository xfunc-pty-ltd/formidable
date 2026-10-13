using System.Linq.Expressions;
using Bunit;
using Bunit.Rendering;
using FluentValidation;
using Formidable.Blazor.Tests.Fixtures;
using Formidable.Introspection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Formidable.Blazor.Tests;

/// <summary>
/// The flag that says a submit is running, in its three homes. The engine's flag is true from the
/// call to <see cref="IFormidableEngine.ValidateForSubmitAsync"/> until its last check ends,
/// however it ends, through every check an edit makes it run again, and it falls once, with
/// <see cref="IFormidableEngine.StateChanged"/> raised as it does. A live check and a load leave it
/// false while <see cref="IFormidableEngine.IsValidating"/> is true. The form's flag runs from the
/// call to <see cref="FormidableForm{TModel}.SubmitAsync"/> until an awaited
/// <see cref="FormidableForm{TModel}.OnValidSubmit"/> returns, and a blocked submit lowers it, and
/// renders the form, before <see cref="FormidableForm{TModel}.OnInvalidSubmit"/> and the focus
/// move, so the move reaches an input the flag disabled. The context reads the form's flag inside a
/// form, false in attach mode, where the page's own handler keeps the flag, and the engine's on a
/// context a test builds. Every form case binds <c>disabled</c> to the context's flag inline, as
/// the recipe does, and counts the async rule's runs, so a render the flag causes is seen to start
/// no check.
/// </summary>
public class SubmittingFlagTests : BunitContext
{
    private const string NameRequired = "Name is required";

    private readonly FakeTimeProvider _time = new();
    private readonly RecordingFocusService _focus = new();

    public SubmittingFlagTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var module = JSInterop.SetupModule("./_content/Formidable.Blazor/formidable.js");
        module.Mode = JSRuntimeMode.Loose;

        // Ahead of AddFormidableBlazor, whose focus registration is a TryAdd.
        Services.AddSingleton<IFormidableFocusService>(_focus);
        Services.AddSingleton<TimeProvider>(_time);
        Services.AddFormidableBlazor();
    }

    public sealed class Account
    {
        public string? Name { get; set; } = "Bo";

        public string? Other { get; set; } = "x";
    }

    /// <summary>Name must be filled, in the draft bucket; a counting async rule on Other, in the submit bucket, waits on a gate while <see cref="Hold"/> is set.</summary>
    public sealed class HeldValidator : DraftSubmitValidator<Account>
    {
        private volatile bool _hold;
        private volatile bool _throws;
        private volatile bool _ignoresToken;
        private volatile TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _runs;

        /// <summary>How many times the async rule has started.</summary>
        public int Runs => Volatile.Read(ref _runs);

        /// <summary>Whether a run that starts waits on the gate.</summary>
        public bool Hold
        {
            get => _hold;
            set => _hold = value;
        }

        /// <summary>Whether a run throws once it is let through.</summary>
        public bool Throws
        {
            get => _throws;
            set => _throws = value;
        }

        /// <summary>Whether a held run waits out its gate whatever its token says.</summary>
        public bool IgnoresToken
        {
            get => _ignoresToken;
            set => _ignoresToken = value;
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
            RuleFor(m => m.Name).NotEmpty().WithMessage(NameRequired);

        protected override void ConfigureSubmitRules() =>
            RuleFor(m => m.Other).MustAsync(async (_, ct) =>
            {
                Interlocked.Increment(ref _runs);
                if (Hold)
                {
                    await (IgnoresToken ? _gate.Task : _gate.Task.WaitAsync(ct));
                }

                if (Throws)
                {
                    throw new InvalidOperationException("The rule threw.");
                }

                return true;
            });
    }

    /// <summary>How a submit, or the check standing in for one, ends.</summary>
    public enum Ending
    {
        /// <summary>The check passes and the submit may proceed.</summary>
        Proceeds,

        /// <summary>The check finds Name empty and blocks.</summary>
        Blocks,

        /// <summary>The async rule throws, and the call throws with it.</summary>
        Faults,

        /// <summary>The caller's token is cancelled while the async rule waits.</summary>
        IsCancelled,

        /// <summary>A second submit takes over while the first waits, and the second proceeds.</summary>
        IsOvertakenBySubmit,

        /// <summary>A load of values takes over while the submit waits on a rule that ignores its token.</summary>
        IsOvertakenByLoad,

        /// <summary>An edit during the first check makes the submit check again, and the second check proceeds.</summary>
        ChecksAgain,

        /// <summary>An edit during each of the three checks; the last passes, so the submit ends with nothing named and the edited field is checked after it.</summary>
        SpendsItsChecksPassing,

        /// <summary>An edit during each of the three checks; every check blocks, the last lands, and the edited field is checked after it.</summary>
        SpendsItsChecksBlocking,

        /// <summary>No submit: an edit's own check runs.</summary>
        LiveCheckOnly,

        /// <summary>No submit: a load of values runs.</summary>
        LoadOnly,
    }

    private sealed record EngineForm(
        FormidableEngine<Account> Engine,
        EditContext EditContext,
        Account Model,
        HeldValidator Validator,
        FakeTimeProvider Time) : IDisposable
    {
        public void Edit(string value)
        {
            Model.Other = value;
            EditContext.NotifyFieldChanged(new FieldIdentifier(Model, nameof(Account.Other)));
        }

        public void Dispose() => Engine.Dispose();
    }

    // One delegate at a time, as the renderer dispatches: a released gate resumes a held rule on a
    // worker thread.
    private static EngineForm BuildEngine()
    {
        var model = new Account();
        var editContext = new EditContext(model);
        var validator = new HeldValidator();
        var time = new FakeTimeProvider();
        var engine = new FormidableEngine<Account>(
            model, editContext, new FluentValidationModelValidator<Account>(validator),
            new ReflectionModelIntrospector(), new FormidableOptions(), time, EngineTestSync.OneAtATime());
        return new EngineForm(engine, editContext, model, validator, time);
    }

    /// <summary>The engine's flag and its checking flag, as each <see cref="IFormidableEngine.StateChanged"/> handler read them.</summary>
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

        /// <summary>The flag's values as the handlers read them, each run of equal values collapsed to one.</summary>
        public string Shape
        {
            get
            {
                var shape = new List<bool>();
                foreach (var (submitting, _) in Seen)
                {
                    if (shape.Count == 0 || shape[^1] != submitting)
                    {
                        shape.Add(submitting);
                    }
                }

                return string.Join(",", shape.Select(s => s ? "up" : "down"));
            }
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

    /// <summary>Moves the clock past every wait in steps, then waits out any check still running.</summary>
    private static async Task SettleAsync(EngineForm form)
    {
        for (var step = 0; step < 10; step++)
        {
            form.Time.Advance(TimeSpan.FromMilliseconds(100));
            await Task.Delay(5);
        }

        Assert.True(await UntilAsync(() => !form.Engine.IsValidating));
        form.Time.Advance(TimeSpan.FromHours(1));
        await Task.Delay(20);
        Assert.True(await UntilAsync(() => !form.Engine.IsValidating));
    }

    /// <summary>Edits Other while each of the submit's first two checks waits, so its third check is its last, and lets each of those two checks answer.</summary>
    private static async Task SpendTwoChecksAsync(EngineForm form, int runsBefore)
    {
        foreach (var other in new[] { "y", "z" })
        {
            var check = form.Validator.Runs - runsBefore;
            form.Edit(other);
            Assert.True(form.Engine.IsSubmitting);
            form.Validator.ReleaseHeldRuns(); // the check's answer predates the edit
            Assert.True(await UntilAsync(() => form.Validator.Runs - runsBefore == check + 1));
            Assert.True(form.Engine.IsSubmitting);
        }
    }

    // The engine's own flag, on every way a submit can end and on the two checks that are not
    // submits. Each row reads the flag while the check waits, records it at every notification,
    // and counts the async rule's runs. The record shows the flag rise once and fall once, a
    // notification carrying the fall; the two rows without a submit never see it up.
    [Theory]
    [InlineData(Ending.Proceeds, 1)]
    [InlineData(Ending.Blocks, 1)]
    [InlineData(Ending.Faults, 1)]
    [InlineData(Ending.IsCancelled, 1)]
    [InlineData(Ending.IsOvertakenBySubmit, 2)]
    [InlineData(Ending.IsOvertakenByLoad, 2)]
    [InlineData(Ending.ChecksAgain, 2)]
    [InlineData(Ending.SpendsItsChecksPassing, 4)]
    [InlineData(Ending.SpendsItsChecksBlocking, 4)]
    [InlineData(Ending.LiveCheckOnly, 1)]
    [InlineData(Ending.LoadOnly, 1)]
    public async Task The_engine_flag_is_true_only_while_a_submit_check_runs(Ending ending, int runs)
    {
        using var form = BuildEngine();
        var engine = form.Engine;
        var validator = form.Validator;
        if (ending is Ending.Blocks or Ending.SpendsItsChecksBlocking)
        {
            form.Model.Name = string.Empty;
        }

        Assert.False(engine.IsSubmitting);
        var record = new FlagRecord(engine);
        var runsBefore = validator.Runs;
        validator.Hold = true;

        if (ending is Ending.LiveCheckOnly or Ending.LoadOnly)
        {
            var load = ending == Ending.LoadOnly ? engine.DiscloseLoadedValuesAsync() : Task.CompletedTask;
            if (ending == Ending.LiveCheckOnly)
            {
                form.Edit("y");
            }

            Assert.True(await UntilAsync(() => validator.Runs > runsBefore));
            Assert.True(engine.IsValidating);
            Assert.False(engine.IsSubmitting);
            validator.Release();
            await load;
            await SettleAsync(form);

            Assert.DoesNotContain(record.Seen, seen => seen.Submitting);
            Assert.Contains(record.Seen, seen => seen.Validating);
            Assert.Equal(runs, validator.Runs - runsBefore);
            return;
        }

        using var cancel = new CancellationTokenSource();
        validator.IgnoresToken = ending == Ending.IsOvertakenByLoad;
        var submit = engine.ValidateForSubmitAsync(cancel.Token);

        // Up as the call returns its task, before any check has answered.
        Assert.True(engine.IsSubmitting);
        Assert.True(await UntilAsync(() => validator.Runs > runsBefore));
        Assert.True(engine.IsSubmitting);
        Assert.True(engine.IsValidating);

        Task? overtaking = null;
        Task<SubmitOutcome>? later = null;
        switch (ending)
        {
            case Ending.Faults:
                validator.Throws = true;
                break;

            case Ending.IsCancelled:
                cancel.Cancel();
                break;

            case Ending.IsOvertakenBySubmit:
                later = engine.ValidateForSubmitAsync();
                Assert.True(await UntilAsync(() => validator.Runs - runsBefore == 2));
                Assert.False((await submit).CanProceed); // the first ends blocked, naming nothing
                Assert.True(engine.IsSubmitting); // the second still runs
                break;

            case Ending.IsOvertakenByLoad:
                overtaking = engine.DiscloseLoadedValuesAsync();
                Assert.False(engine.IsSubmitting); // down as the load takes over
                Assert.True(await UntilAsync(() => validator.Runs - runsBefore == 2));
                Assert.False(submit.IsCompleted); // its rule ignores the token and still waits
                Assert.True(engine.IsValidating);
                Assert.False(engine.IsSubmitting);
                break;

            case Ending.ChecksAgain:
                form.Edit("y");
                validator.ReleaseHeldRuns(); // the first check's answer predates the edit
                Assert.True(await UntilAsync(() => validator.Runs - runsBefore == 2));
                Assert.True(engine.IsSubmitting);
                Assert.True(engine.IsValidating);
                break;

            case Ending.SpendsItsChecksPassing:
            case Ending.SpendsItsChecksBlocking:
                await SpendTwoChecksAsync(form, runsBefore);
                form.Edit("w"); // during the last check
                break;
        }

        validator.Release();
        switch (ending)
        {
            case Ending.Proceeds:
            case Ending.ChecksAgain:
                Assert.True((await submit).CanProceed);
                break;

            case Ending.Blocks:
            case Ending.SpendsItsChecksPassing:
            case Ending.SpendsItsChecksBlocking:
            case Ending.IsOvertakenByLoad:
                Assert.False((await submit).CanProceed);
                break;

            case Ending.Faults:
                await Assert.ThrowsAsync<InvalidOperationException>(() => submit);
                break;

            case Ending.IsCancelled:
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => submit);
                break;

            case Ending.IsOvertakenBySubmit:
                Assert.True((await later!).CanProceed);
                break;
        }

        if (overtaking is not null)
        {
            await overtaking;
        }

        Assert.False(engine.IsSubmitting);
        await SettleAsync(form);
        Assert.False(engine.IsSubmitting);

        // Up once and down once, the fall carried by a notification; nothing after it raises the
        // flag again, whatever the end of the submit gave back to check.
        Assert.Equal("up,down", record.Shape);
        Assert.Equal(runs, validator.Runs - runsBefore);
        if (ending is Ending.SpendsItsChecksPassing or Ending.SpendsItsChecksBlocking)
        {
            // The edit made during the last check is checked after the submit, with the flag down.
            var fall = record.Seen.FindIndex(seen => !seen.Submitting);
            Assert.Contains(record.Seen.Skip(fall + 1), seen => seen.Validating);
        }
    }

    // A disposed engine runs no submit, and says so, though no notification follows its disposal.
    [Fact]
    public async Task A_disposed_engine_reads_no_submit_running()
    {
        var form = BuildEngine();
        form.Validator.Hold = true;
        var submit = form.Engine.ValidateForSubmitAsync();
        Assert.True(await UntilAsync(() => form.Validator.Runs > 0));
        Assert.True(form.Engine.IsSubmitting);

        form.Dispose();

        Assert.False(form.Engine.IsSubmitting);
        form.Validator.Release();
        Assert.False((await submit).CanProceed);
        Assert.False(form.Engine.IsSubmitting);
    }

    // An engine written before the flag existed answers false, even while the engine it wraps
    // runs a submit, and so does a context a test builds over it by hand.
    [Fact]
    public async Task An_engine_without_the_flag_reads_no_submit_running()
    {
        using var form = BuildEngine();
        IFormidableEngine predating = new EngineWithoutTheFlag(form.Engine);
        form.Validator.Hold = true;
        var submit = predating.ValidateForSubmitAsync();
        Assert.True(await UntilAsync(() => form.Validator.Runs > 0));

        Assert.True(form.Engine.IsSubmitting);
        Assert.False(predating.IsSubmitting);
        Assert.False(new FormidableFormContext(predating).IsSubmitting);
        Assert.True(new FormidableFormContext(form.Engine).IsSubmitting);

        form.Validator.Release();
        Assert.True((await submit).CanProceed);
    }

    /// <summary>What the page shows of the flag at one moment: each home's flag, and whether the bound input and button are disabled.</summary>
    private sealed record Snapshot(
        bool Form, bool Context, bool Engine, bool InputDisabled, bool ButtonDisabled, string Status);

    private sealed class KitForm(
        IRenderedComponent<ContainerFragment> cut,
        IRenderedComponent<FormidableForm<Account>> form,
        Func<FormidableFormContext> context,
        Account model,
        HeldValidator validator)
    {
        public IRenderedComponent<ContainerFragment> Cut { get; } = cut;

        public IRenderedComponent<FormidableForm<Account>> Rendered { get; } = form;

        public FormidableForm<Account> Form => Rendered.Instance;

        public FormidableFormContext Context => context();

        public Account Model { get; } = model;

        public HeldValidator Validator { get; } = validator;

        public FieldIdentifier Name => new(Model, nameof(Account.Name));

        /// <summary>Reads the page; call it on the dispatcher.</summary>
        public Snapshot Read() => new(
            Form.IsSubmitting,
            Context.IsSubmitting,
            Form.Engine!.IsSubmitting,
            Cut.Find("input").HasAttribute("disabled"),
            Cut.Find("button").HasAttribute("disabled"),
            Cut.Find("p[role=status]").TextContent.Trim());
    }

    private static readonly Snapshot Locked = new(true, true, true, true, true, "Submitting…");

    /// <summary>Renders a form whose Name input and Submit button bind <c>disabled</c> to the context's flag inline, beside a status line, as the recipe does, and settles the check its first render starts.</summary>
    private async Task<KitForm> RenderFormAsync(
        Account model,
        Func<SubmitOutcome, Task>? onValid = null,
        Action<FormidableInvalidSubmitContext>? onInvalid = null)
    {
        var validator = new HeldValidator();
        Func<SubmitOutcome, Task> valid = onValid ?? (_ => Task.CompletedTask);
        Action<FormidableInvalidSubmitContext> invalid = onInvalid ?? (_ => { });
        FormidableFormContext? captured = null;
        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<Account>>(0);
            builder.AddComponentParameter(1, "Model", model);
            builder.AddComponentParameter(2, "Validator", new FluentValidationModelValidator<Account>(validator));
            builder.AddComponentParameter(3, "OnValidSubmit", EventCallback.Factory.Create(this, valid));
            builder.AddComponentParameter(4, "OnInvalidSubmit", EventCallback.Factory.Create(this, invalid));
            builder.AddComponentParameter(5, "ChildContent", (RenderFragment<FormidableFormContext>)(context => inner =>
            {
                captured = context;
                inner.OpenComponent<FormidableInputText>(0);
                inner.AddComponentParameter(1, "For", (Expression<Func<string?>>)(() => model.Name));
                inner.AddComponentParameter(2, "Value", model.Name);
                inner.AddComponentParameter(3, "ValueChanged", EventCallback.Factory.Create<string?>(this, v => model.Name = v));
                inner.AddMultipleAttributes(4, new Dictionary<string, object> { ["disabled"] = context.IsSubmitting });
                inner.CloseComponent();

                inner.OpenComponent<FormidableFieldMessage<string?>>(5);
                inner.AddComponentParameter(6, "For", (Expression<Func<string?>>)(() => model.Name));
                inner.CloseComponent();

                inner.OpenElement(7, "button");
                inner.AddAttribute(8, "type", "submit");
                inner.AddAttribute(9, "disabled", context.IsSubmitting);
                inner.AddContent(10, "Submit");
                inner.CloseElement();

                inner.OpenElement(11, "p");
                inner.AddAttribute(12, "role", "status");
                inner.AddContent(13, context.IsSubmitting ? "Submitting…" : null);
                inner.CloseElement();
            }));
            builder.CloseComponent();
        });

        var form = cut.FindComponent<FormidableForm<Account>>();
        var kit = new KitForm(cut, form, () => captured!, model, validator);
        await SettleAsync(kit);
        return kit;
    }

    /// <summary>Moves the clock an hour on, on the dispatcher, and waits out any check that starts.</summary>
    private async Task SettleAsync(KitForm kit)
    {
        for (var step = 0; step < 2; step++)
        {
            await kit.Cut.InvokeAsync(() => _time.Advance(TimeSpan.FromHours(1)));
            await Task.Delay(10);
            Assert.True(await UntilAsync(() => !kit.Form.Engine!.IsValidating));
        }
    }

    // A passing submit whose handler awaits a save keeps the form's flag up until the handler
    // returns, while the engine's falls as its check ends. The inline disabled and the status line
    // follow the form's flag, and the renders that raise and lower it start no check.
    [Fact]
    public async Task The_form_flag_covers_an_awaited_OnValidSubmit()
    {
        var saving = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Snapshot? atHandler = null;
        KitForm? kit = null;
        kit = await RenderFormAsync(new Account(), onValid: async _ =>
        {
            atHandler = kit!.Read();
            await saving.Task;
        });

        var runsBefore = kit.Validator.Runs;
        Assert.Equal(new Snapshot(false, false, false, false, false, string.Empty), await kit.Cut.InvokeAsync(kit.Read));

        kit.Validator.Hold = true;
        var submit = kit.Cut.InvokeAsync(() => kit.Form.SubmitAsync());
        await kit.Cut.InvokeAsync(() => { });
        Assert.True(await UntilAsync(() => kit.Validator.Runs > runsBefore));
        Assert.Equal(Locked, await kit.Cut.InvokeAsync(kit.Read));

        await kit.Cut.InvokeAsync(kit.Validator.Release);
        Assert.True(await UntilAsync(() => atHandler is not null));
        Assert.Equal(Locked with { Engine = false }, atHandler);
        Assert.Equal(Locked with { Engine = false }, await kit.Cut.InvokeAsync(kit.Read)); // still saving

        saving.SetResult();
        Assert.True((await submit).CanProceed);
        Assert.Equal(new Snapshot(false, false, false, false, false, string.Empty), await kit.Cut.InvokeAsync(kit.Read));

        await SettleAsync(kit);
        Assert.Equal(1, kit.Validator.Runs - runsBefore);
    }

    // A blocked submit lowers the form's flag and renders before its handler and its focus move,
    // so both find the input enabled, and the move reaches the first error.
    [Fact]
    public async Task A_blocked_submit_lowers_the_flag_before_OnInvalidSubmit_and_the_focus_move()
    {
        Snapshot? atHandler = null;
        Snapshot? atFocus = null;
        KitForm? kit = null;
        kit = await RenderFormAsync(new Account { Name = string.Empty }, onInvalid: _ => atHandler = kit!.Read());
        _focus.OnFocus = _ => atFocus = kit.Read();

        var runsBefore = kit.Validator.Runs;
        kit.Validator.Hold = true;
        var submit = kit.Cut.InvokeAsync(() => kit.Form.SubmitAsync());
        await kit.Cut.InvokeAsync(() => { });
        Assert.True(await UntilAsync(() => kit.Validator.Runs > runsBefore));
        Assert.Equal(Locked, await kit.Cut.InvokeAsync(kit.Read));

        await kit.Cut.InvokeAsync(kit.Validator.Release);
        Assert.False((await submit).CanProceed);

        var unlocked = new Snapshot(false, false, false, false, false, string.Empty);
        Assert.Equal([kit.Name], _focus.Requests);
        Assert.Equal(unlocked, atFocus);
        Assert.Equal(unlocked, atHandler);

        await SettleAsync(kit);
        Assert.Equal(1, kit.Validator.Runs - runsBefore);
    }

    // The lock is in the markup before the submit's task completes: after a call made in the same
    // work item, and after the form's own submit event, which is the route a browser takes.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_disabled_bound_through_the_context_is_in_the_markup_before_SubmitAsync_returns(bool viaFormEvent)
    {
        var kit = await RenderFormAsync(new Account());
        var runsBefore = kit.Validator.Runs;
        kit.Validator.Hold = true;

        Task<SubmitOutcome>? submit = null;
        var atReturn = await kit.Cut.InvokeAsync(() =>
        {
            if (viaFormEvent)
            {
                kit.Cut.Find("form").Submit();
            }
            else
            {
                submit = kit.Form.SubmitAsync();
                Assert.False(submit.IsCompleted);
            }

            return kit.Read();
        });

        Assert.Equal(Locked, atReturn);

        await kit.Cut.InvokeAsync(kit.Validator.Release);
        if (submit is not null)
        {
            Assert.True((await submit).CanProceed);
        }

        Assert.True(await UntilAsync(() => !kit.Form.IsSubmitting));
        Assert.Equal(new Snapshot(false, false, false, false, false, string.Empty), await kit.Cut.InvokeAsync(kit.Read));
        await SettleAsync(kit);
        Assert.Equal(1, kit.Validator.Runs - runsBefore);
    }

    // A submit whose check throws still lowers the form's flag and renders, as the throw reaches
    // the caller.
    [Fact]
    public async Task A_submit_that_throws_lowers_the_form_flag()
    {
        var kit = await RenderFormAsync(new Account());
        var runsBefore = kit.Validator.Runs;
        kit.Validator.Hold = true;
        kit.Validator.Throws = true;
        var submit = kit.Cut.InvokeAsync(() => kit.Form.SubmitAsync());
        await kit.Cut.InvokeAsync(() => { });
        Assert.True(await UntilAsync(() => kit.Validator.Runs > runsBefore));
        Assert.Equal(Locked, await kit.Cut.InvokeAsync(kit.Read));

        await kit.Cut.InvokeAsync(kit.Validator.Release);
        await Assert.ThrowsAsync<InvalidOperationException>(() => submit);

        Assert.Equal(new Snapshot(false, false, false, false, false, string.Empty), await kit.Cut.InvokeAsync(kit.Read));
    }

    // SubmitAsync called off the renderer's context, which its XML forbids, throws at its first
    // render. The flag it raised still falls, so a Submit button bound to it is not disabled for
    // good, and no check starts. Mutation: render above the try, and the flag reads true after the
    // throw.
    [Fact]
    public async Task A_SubmitAsync_off_the_dispatcher_throws_and_leaves_the_flag_down()
    {
        var kit = await RenderFormAsync(new Account());
        var runsBefore = kit.Validator.Runs;

        // Run on the thread pool on purpose, as a method group: it is the misuse under test, not a
        // DOM event the dispatcher should carry.
        Func<Task<SubmitOutcome>> submitOffTheDispatcher = kit.Form.SubmitAsync;
        var thrown = await Record.ExceptionAsync(() => Task.Run(submitOffTheDispatcher));

        Assert.IsType<InvalidOperationException>(thrown);
        Assert.Equal(
            (false, new Snapshot(false, false, false, false, false, string.Empty), 0),
            await kit.Cut.InvokeAsync(() => (kit.Form.IsSubmitting, kit.Read(), kit.Validator.Runs - runsBefore)));
    }

    // An OnValidSubmit handler that throws after an await: the throw reaches the caller, and the
    // form's flag still falls and renders, so the lock does not stand for good.
    [Fact]
    public async Task An_OnValidSubmit_that_throws_after_an_await_lowers_the_form_flag()
    {
        var saving = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Snapshot? atHandler = null;
        KitForm? kit = null;
        kit = await RenderFormAsync(new Account(), onValid: async _ =>
        {
            atHandler = kit!.Read();
            await saving.Task;
            throw new InvalidOperationException("The save threw.");
        });

        var runsBefore = kit.Validator.Runs;
        var submit = kit.Cut.InvokeAsync(() => kit.Form.SubmitAsync());
        await kit.Cut.InvokeAsync(() => { });
        Assert.True(await UntilAsync(() => atHandler is not null));
        Assert.Equal(Locked with { Engine = false }, atHandler);
        Assert.Equal(Locked with { Engine = false }, await kit.Cut.InvokeAsync(kit.Read));

        saving.SetResult();
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => submit);
        Assert.Equal("The save threw.", thrown.Message);
        Assert.False(kit.Form.IsSubmitting);
        Assert.Equal(new Snapshot(false, false, false, false, false, string.Empty), await kit.Cut.InvokeAsync(kit.Read));

        await SettleAsync(kit);
        Assert.Equal(1, kit.Validator.Runs - runsBefore);
    }

    // Two submits at once, the second pressed while the first waits: the form's flag stays up
    // until the second ends, though the first ends, blocked and naming nothing, before it.
    [Fact]
    public async Task Two_submits_keep_the_form_flag_until_the_later_one_ends()
    {
        Snapshot? atFirstEnd = null;
        KitForm? kit = null;
        kit = await RenderFormAsync(new Account(), onInvalid: _ => atFirstEnd = kit!.Read());
        var runsBefore = kit.Validator.Runs;
        kit.Validator.Hold = true;

        var first = kit.Cut.InvokeAsync(() => kit.Form.SubmitAsync());
        await kit.Cut.InvokeAsync(() => { });
        Assert.True(await UntilAsync(() => kit.Validator.Runs - runsBefore == 1));
        var second = kit.Cut.InvokeAsync(() => kit.Form.SubmitAsync());
        await kit.Cut.InvokeAsync(() => { });
        Assert.True(await UntilAsync(() => kit.Validator.Runs - runsBefore == 2));

        Assert.False((await first).CanProceed);
        Assert.Equal(Locked, atFirstEnd);
        Assert.Equal(Locked, await kit.Cut.InvokeAsync(kit.Read));

        await kit.Cut.InvokeAsync(kit.Validator.Release);
        Assert.True((await second).CanProceed);
        Assert.Equal(new Snapshot(false, false, false, false, false, string.Empty), await kit.Cut.InvokeAsync(kit.Read));
        await SettleAsync(kit);
        Assert.Equal(2, kit.Validator.Runs - runsBefore);
    }

    // With an error showing, two submits at once, as a second press on Blazor Server before the
    // lock lands makes: the first, displaced, fires OnInvalidSubmit and moves no focus while the
    // second still checks, under the lock the second keeps up. The second blocks and makes the one
    // move, once the flag has fallen. Mutation: drop the guard in SubmitAsync's blocked branch, and
    // the first submit asks for focus while the input is still disabled.
    [Fact]
    public async Task A_displaced_submit_moves_no_focus_while_the_newer_one_checks()
    {
        var invalidCalls = 0;
        KitForm? kit = null;
        kit = await RenderFormAsync(new Account { Name = string.Empty }, onInvalid: _ => invalidCalls++);
        await kit.Cut.InvokeAsync(() => kit.Form.SubmitAsync()); // puts "Name is required" on screen
        _focus.Requests.Clear();
        invalidCalls = 0;
        var atFocus = new List<Snapshot>();
        _focus.OnFocus = _ => atFocus.Add(kit.Read());

        var runsBefore = kit.Validator.Runs;
        kit.Validator.Hold = true;
        var first = kit.Cut.InvokeAsync(() => kit.Form.SubmitAsync());
        await kit.Cut.InvokeAsync(() => { });
        Assert.True(await UntilAsync(() => kit.Validator.Runs - runsBefore == 1));
        var second = kit.Cut.InvokeAsync(() => kit.Form.SubmitAsync());
        await kit.Cut.InvokeAsync(() => { });
        Assert.True(await UntilAsync(() => kit.Validator.Runs - runsBefore == 2));

        var firstOutcome = await first;
        await kit.Cut.InvokeAsync(() => { });
        Assert.Equal(
            (false, 0, 1, 0, Locked),
            (firstOutcome.CanProceed, firstOutcome.VisibleErrorSummary.Count, invalidCalls, _focus.Requests.Count, await kit.Cut.InvokeAsync(kit.Read)));

        await kit.Cut.InvokeAsync(kit.Validator.Release);
        Assert.False((await second).CanProceed);
        Assert.Equal(2, invalidCalls);
        Assert.Equal([kit.Name], _focus.Requests);
        Assert.Equal([new Snapshot(false, false, false, false, false, string.Empty)], atFocus);
    }

    // The other side of that guard: a submit that blocks while an earlier one is still in its
    // awaited OnValidSubmit moves focus, because the earlier submit is past its check and makes no
    // move of its own. The form's flag is still up for the save. Mutation: guard on the form's
    // count alone, and the blocked submit asks for no focus.
    [Fact]
    public async Task A_submit_that_blocks_while_an_earlier_one_saves_still_moves_focus()
    {
        var saving = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = new Account();
        var kit = await RenderFormAsync(model, onValid: _ => saving.Task);
        var atFocus = new List<Snapshot>();
        _focus.OnFocus = _ => atFocus.Add(kit.Read());

        var first = kit.Cut.InvokeAsync(() => kit.Form.SubmitAsync());
        Assert.True(await UntilAsync(() => kit.Form.IsSubmitting && !kit.Form.Engine!.IsSubmitting));

        // The visitor empties Name while the save runs (a page with no lock on its inputs).
        await kit.Cut.InvokeAsync(() =>
        {
            model.Name = string.Empty;
            kit.Form.Engine!.EditContext.NotifyFieldChanged(kit.Name);
        });
        await SettleAsync(kit);

        var second = await kit.Cut.InvokeAsync(() => kit.Form.SubmitAsync());
        Assert.False(second.CanProceed);
        Assert.Equal([kit.Name], _focus.Requests);
        Assert.Equal([Locked with { Engine = false }], atFocus);

        saving.SetResult();
        Assert.True((await first).CanProceed);
        Assert.False(kit.Form.IsSubmitting);
    }

    // A reset while a submit waits abandons it: the rebuilt form reads no submit running at once,
    // the abandoned submit's end renders nothing, and the next submit raises the flag as ever.
    [Fact]
    public async Task A_reset_while_a_submit_waits_lowers_the_flag_and_the_next_submit_raises_it()
    {
        var kit = await RenderFormAsync(new Account());
        var runsBefore = kit.Validator.Runs;
        kit.Validator.Hold = true;
        kit.Validator.IgnoresToken = true;
        var abandoned = kit.Cut.InvokeAsync(() => kit.Form.SubmitAsync());
        await kit.Cut.InvokeAsync(() => { });
        Assert.True(await UntilAsync(() => kit.Validator.Runs - runsBefore == 1));
        Assert.Equal(Locked, await kit.Cut.InvokeAsync(kit.Read));

        await kit.Cut.InvokeAsync(() => kit.Form.ResetAsync());
        Assert.Equal(new Snapshot(false, false, false, false, false, string.Empty), await kit.Cut.InvokeAsync(kit.Read));

        var renders = kit.Rendered.RenderCount;
        await kit.Cut.InvokeAsync(kit.Validator.ReleaseHeldRuns);
        Assert.False((await abandoned).CanProceed);
        await kit.Cut.InvokeAsync(() => { });
        Assert.Equal(renders, kit.Rendered.RenderCount);
        Assert.False(kit.Form.IsSubmitting);

        var next = kit.Cut.InvokeAsync(() => kit.Form.SubmitAsync());
        await kit.Cut.InvokeAsync(() => { });
        Assert.Equal(Locked, await kit.Cut.InvokeAsync(kit.Read));
        await kit.Cut.InvokeAsync(kit.Validator.Release);
        Assert.True((await next).CanProceed);
        Assert.False(kit.Form.IsSubmitting);
    }

    // A page that saves and then resets the form from its OnValidSubmit handler: the reset
    // rebuilds the engine inside the handler, and the next submit raises the flag as ever.
    [Fact]
    public async Task A_reset_inside_OnValidSubmit_leaves_the_next_submit_raising_the_flag()
    {
        KitForm? kit = null;
        var resets = 0;
        kit = await RenderFormAsync(new Account(), onValid: async _ =>
        {
            if (resets++ == 0)
            {
                await kit!.Form.ResetAsync();
            }
        });

        Assert.True((await kit.Cut.InvokeAsync(() => kit.Form.SubmitAsync())).CanProceed);
        Assert.False(kit.Form.IsSubmitting);

        var runsBefore = kit.Validator.Runs;
        kit.Validator.Hold = true;
        var next = kit.Cut.InvokeAsync(() => kit.Form.SubmitAsync());
        await kit.Cut.InvokeAsync(() => { });
        Assert.True(await UntilAsync(() => kit.Validator.Runs - runsBefore == 1));
        Assert.Equal(Locked, await kit.Cut.InvokeAsync(kit.Read));

        await kit.Cut.InvokeAsync(kit.Validator.Release);
        Assert.True((await next).CanProceed);
        Assert.Equal(new Snapshot(false, false, false, false, false, string.Empty), await kit.Cut.InvokeAsync(kit.Read));
    }

    // A form disposed while a submit waits reads no submit running, and the abandoned submit's
    // end leaves it so. The rule ignores its token, so the submit is still waiting after the
    // disposal.
    [Fact]
    public async Task A_form_disposed_while_a_submit_waits_reads_no_submit_running()
    {
        var kit = await RenderFormAsync(new Account());
        var runsBefore = kit.Validator.Runs;
        kit.Validator.Hold = true;
        kit.Validator.IgnoresToken = true;
        var abandoned = kit.Cut.InvokeAsync(() => kit.Form.SubmitAsync());
        await kit.Cut.InvokeAsync(() => { });
        Assert.True(await UntilAsync(() => kit.Validator.Runs - runsBefore == 1));
        var form = kit.Form;
        Assert.True(form.IsSubmitting);

        await DisposeComponentsAsync();
        Assert.True(kit.Rendered.IsDisposed, "DisposeComponentsAsync did not dispose the form.");
        Assert.False(abandoned.IsCompleted);
        Assert.False(form.IsSubmitting);

        kit.Validator.Release();
        Assert.False((await abandoned).CanProceed);
        Assert.False(form.IsSubmitting);
    }

    // In attach mode the page's own handler runs the submit and keeps the flag, so the context
    // reads false throughout, while the engine's flag says its check is running. A lock bound to
    // the engine's flag would still stand at the root's own focus move, which runs inside
    // ValidateForSubmitAsync before the page renders again.
    [Fact]
    public async Task In_attach_mode_the_context_reads_no_submit_running()
    {
        var model = new Account();
        var validator = new HeldValidator();
        var editContext = new EditContext(model);
        FormidableFormContext? captured = null;
        var cut = Render(builder =>
        {
            builder.OpenComponent<EditForm>(0);
            builder.AddComponentParameter(1, "EditContext", editContext);
            builder.AddComponentParameter(2, "ChildContent", (RenderFragment<EditContext>)(_ => inner =>
            {
                inner.OpenComponent<FormidableValidator<Account>>(0);
                inner.AddComponentParameter(1, "Validator", new FluentValidationModelValidator<Account>(validator));
                inner.AddComponentParameter(2, "ChildContent", (RenderFragment<FormidableFormContext>)(context => _ =>
                {
                    captured = context;
                }));
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        });

        var root = cut.FindComponent<FormidableValidator<Account>>();
        await cut.InvokeAsync(() => _time.Advance(TimeSpan.FromHours(1)));
        Assert.True(await UntilAsync(() => !root.Instance.Engine!.IsValidating));
        Assert.False(captured!.IsSubmitting);

        validator.Hold = true;
        var submit = cut.InvokeAsync(() => root.Instance.ValidateForSubmitAsync());
        await cut.InvokeAsync(() => { });
        Assert.True(root.Instance.Engine!.IsSubmitting);
        Assert.False(captured.IsSubmitting);

        await cut.InvokeAsync(validator.Release);
        Assert.True((await submit).CanProceed);
        Assert.False(captured.IsSubmitting);
        Assert.False(root.Instance.Engine.IsSubmitting);
    }

    /// <summary>An engine written before the flag existed: it forwards every member it knows to a real engine and implements no flag.</summary>
    private sealed class EngineWithoutTheFlag(IFormidableEngine inner) : IFormidableEngine
    {
        public EditContext EditContext => inner.EditContext;

        public FieldRegistry Registry => inner.Registry;

        public FormidableOptions Options => inner.Options;

        public bool IsValidating => inner.IsValidating;

        public bool HasSubmitted => inner.HasSubmitted;

        public bool IsFormValid => inner.IsFormValid;

        public event EventHandler<FormidableStateChangedEventArgs>? StateChanged
        {
            add => inner.StateChanged += value;
            remove => inner.StateChanged -= value;
        }

        public event EventHandler<FormidableValidationFaultedEventArgs>? ValidationFaulted
        {
            add => inner.ValidationFaulted += value;
            remove => inner.ValidationFaulted -= value;
        }

        public FieldState GetFieldState(FieldIdentifier field) => inner.GetFieldState(field);

        public FieldRequirement GetFieldRequirement(FieldIdentifier field) => inner.GetFieldRequirement(field);

        public IReadOnlyList<ValidationIssue> GetIssues(FieldIdentifier field) => inner.GetIssues(field);

        public IReadOnlyList<VisibleIssue> GetVisibleIssues() => inner.GetVisibleIssues();

        public void MarkTouched(FieldIdentifier field) => inner.MarkTouched(field);

        public Task<SubmitOutcome> ValidateForSubmitAsync(CancellationToken cancellationToken = default) =>
            inner.ValidateForSubmitAsync(cancellationToken);

        public Task DiscloseLoadedValuesAsync(CancellationToken cancellationToken = default) =>
            inner.DiscloseLoadedValuesAsync(cancellationToken);

        public void ApplyServerIssues(IEnumerable<ValidationIssue> issues) => inner.ApplyServerIssues(issues);
    }
}
