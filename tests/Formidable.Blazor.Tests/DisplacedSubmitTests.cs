using System.Linq.Expressions;
using Bunit;
using Bunit.Rendering;
using FluentValidation;
using Formidable.Blazor.Tests.Fixtures;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Formidable.Blazor.Tests;

/// <summary>
/// Two submits on one form, as a second press on Blazor Server before the lock lands makes. The
/// newer submit decides: a blocked submit makes no focus move once a newer one has started, and a
/// submit a newer one overtakes stops holding <see cref="FormidableForm{TModel}.IsSubmitting"/>,
/// because it can no longer save. A submit whose check has answered keeps its share while it
/// saves, so the recipe's double-save lock stays up until the first save ends.
/// </summary>
/// <remarks>
/// Each form binds its input's and button's <c>disabled</c> to <c>context.IsSubmitting</c>, as
/// the lock recipe does, and the focus double answers as a browser does: a disabled input does
/// not take focus. Name has an error rule and a warning ("Bo is unusual"); Other has an async
/// submit rule a test can hold once, ignoring its token (a call made without it) or honouring it.
/// </remarks>
// Mutations that must break it: drop the newer-submit test from the blocked submit's focus move,
// and S1 (both rows), S2, S3, S5 (both rows) and C4 fail; lower the share of the latest submit at
// every start rather than only while the engine's submit check runs, and C8b fails.
public class DisplacedSubmitTests : BunitContext
{
    private readonly FakeTimeProvider _time = new();
    private readonly RecordingFocusService _focus = new();
    private readonly CapturingLoggerProvider _logs = new();

    public DisplacedSubmitTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var module = JSInterop.SetupModule("./_content/Formidable.Blazor/formidable.js");
        module.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IFormidableFocusService>(_focus);
        Services.AddSingleton<TimeProvider>(_time);
        Services.AddSingleton<ILoggerFactory>(LoggerFactory.Create(builder => builder.AddProvider(_logs)));
        Services.AddFormidableBlazor();
    }

    public sealed class Account
    {
        public string? Name { get; set; } = "Al";

        public string? Other { get; set; } = "x";
    }

    public sealed class AccountValidator : DraftSubmitValidator<Account>
    {
        private TaskCompletionSource? _armed;
        private bool _armedIgnoresToken;
        private TaskCompletionSource? _held;
        private int _runs;

        public int Runs => Volatile.Read(ref _runs);

        public void HoldNextRun(bool ignoresToken = true)
        {
            _armedIgnoresToken = ignoresToken;
            _armed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void ReleaseHeldRun() => _held?.TrySetResult();

        protected override void ConfigureDraftRules()
        {
            RuleFor(m => m.Name).NotEmpty().WithMessage("Name is required");
            RuleFor(m => m.Name).Must(n => n != "Bo").WithSeverity(Severity.Warning).WithMessage("Bo is unusual");
        }

        protected override void ConfigureSubmitRules() =>
            RuleFor(m => m.Other).MustAsync(async (_, ct) =>
            {
                Interlocked.Increment(ref _runs);
                var gate = Interlocked.Exchange(ref _armed, null);
                if (gate is not null)
                {
                    _held = gate;
                    await (_armedIgnoresToken ? gate.Task : gate.Task.WaitAsync(ct));
                }

                return true;
            });
    }

    private sealed record Snapshot(bool Form, bool Engine, bool InputDisabled);

    private sealed class KitForm(
        IRenderedComponent<ContainerFragment> cut,
        IRenderedComponent<FormidableForm<Account>> form,
        AccountValidator validator)
    {
        public IRenderedComponent<ContainerFragment> Cut { get; } = cut;

        public FormidableForm<Account> Form => form.Instance;

        public AccountValidator Validator { get; } = validator;

        public Snapshot Read() => new(
            Form.IsSubmitting,
            Form.Engine!.IsSubmitting,
            Cut.Find("input").HasAttribute("disabled"));
    }

    // S1. A's rule ignores its token. B overtakes A, passes with a warning showing, and saves; A's
    // rule ends during B's save. No focus request: B passed. With a fallback wired, it is not run.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task S1_an_overtaken_submit_that_ends_during_the_newer_save_moves_no_focus(bool withFallback)
    {
        var saving = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saveEntered = false;
        var fallbackCalls = 0;
        var kit = await RenderFormAsync(
            new Account { Name = "Bo" },
            onValid: async _ =>
            {
                saveEntered = true;
                await saving.Task;
            },
            onInvalid: _ => Task.CompletedTask,
            fallback: withFallback
                ? _ =>
                {
                    fallbackCalls++;
                    return ValueTask.FromResult(false);
                }
                : null);
        var atFocus = RecordFocus(kit);
        var first = await StartHeldAsync(kit);

        var second = kit.Cut.InvokeAsync(() => kit.Form.SubmitAsync());
        Assert.True(await UntilAsync(() => saveEntered));
        await kit.Cut.InvokeAsync(kit.Validator.ReleaseHeldRun);
        Assert.False((await first).CanProceed);
        await kit.Cut.InvokeAsync(() => { });

        saving.SetResult();
        Assert.True((await second).CanProceed);
        await SettleAsync(kit);

        Assert.Empty(atFocus);
        Assert.Equal(0, fallbackCalls);
        Assert.DoesNotContain(_logs.Entries, entry => entry.Message.Contains("did not take focus", StringComparison.Ordinal));
    }

    // S2. A's rule ignores its token. B overtakes A and blocks while A's rule still runs. B's own
    // move lands, unlocked; the input is enabled as soon as B returns; A's later end makes no
    // second move.
    [Fact]
    public async Task S2_a_newer_submit_that_blocks_moves_focus_unlocked_and_the_overtaken_one_moves_none()
    {
        var kit = await RenderFormAsync(new Account { Name = string.Empty }, onValid: _ => Task.CompletedTask, onInvalid: _ => Task.CompletedTask);
        var atFocus = RecordFocus(kit);
        var first = await StartHeldAsync(kit);

        Assert.False((await kit.Cut.InvokeAsync(() => kit.Form.SubmitAsync())).CanProceed);
        var afterB = await kit.Cut.InvokeAsync(kit.Read);

        await kit.Cut.InvokeAsync(kit.Validator.ReleaseHeldRun);
        await first;
        await kit.Cut.InvokeAsync(() => { });
        await SettleAsync(kit);

        Assert.False(afterB.InputDisabled);
        Assert.Equal([new Snapshot(false, false, false)], atFocus);
    }

    // S3. A's rule ignores its token. B overtakes A, passes with a warning showing and finishes its
    // save; A's rule ends afterwards. The lock falls when B's save ends, and A's end moves no focus.
    [Fact]
    public async Task S3_an_overtaken_submit_that_ends_after_the_newer_save_holds_no_lock_and_moves_no_focus()
    {
        var kit = await RenderFormAsync(new Account { Name = "Bo" }, onValid: _ => Task.CompletedTask, onInvalid: _ => Task.CompletedTask);
        var atFocus = RecordFocus(kit);
        var first = await StartHeldAsync(kit);

        Assert.True((await kit.Cut.InvokeAsync(() => kit.Form.SubmitAsync())).CanProceed);
        var afterB = await kit.Cut.InvokeAsync(kit.Read);

        await kit.Cut.InvokeAsync(kit.Validator.ReleaseHeldRun);
        await first;
        await kit.Cut.InvokeAsync(() => { });
        await SettleAsync(kit);

        Assert.False(afterB.InputDisabled);
        Assert.Empty(atFocus);
    }

    // S5. A's rule honours its token, so A is overtaken at once, but A's OnInvalidSubmit is async
    // and finishes during B's save, or after it; B passed with a warning showing. No focus request.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task S5_an_overtaken_submits_handler_that_finishes_during_or_after_the_newer_save_moves_no_focus(bool afterSave)
    {
        var saving = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlerGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saveEntered = false;
        var invalidEntered = false;
        var kit = await RenderFormAsync(
            new Account { Name = "Bo" },
            onValid: async _ =>
            {
                saveEntered = true;
                await saving.Task;
            },
            onInvalid: async _ =>
            {
                invalidEntered = true;
                await handlerGate.Task;
            });
        var atFocus = RecordFocus(kit);
        var first = await StartHeldAsync(kit, ignoresToken: false);

        var second = kit.Cut.InvokeAsync(() => kit.Form.SubmitAsync());
        Assert.True(await UntilAsync(() => saveEntered && invalidEntered));
        if (afterSave)
        {
            saving.SetResult();
            Assert.True((await second).CanProceed);
        }

        await kit.Cut.InvokeAsync(handlerGate.SetResult);
        await first;
        await kit.Cut.InvokeAsync(() => { });
        saving.TrySetResult();
        await second;
        await SettleAsync(kit);

        Assert.Empty(atFocus);
    }

    // C4. A's rule honours its token; B overtakes A and passes with a warning showing, its check
    // answering without awaiting, so A's blocked outcome reaches the form after B's save. No focus
    // request, one save, no lock afterwards.
    [Fact]
    public async Task C4_a_double_press_whose_second_passes_moves_no_focus()
    {
        var saves = 0;
        var kit = await RenderFormAsync(
            new Account { Name = "Bo" },
            onValid: _ =>
            {
                saves++;
                return Task.CompletedTask;
            },
            onInvalid: _ => Task.CompletedTask);
        var atFocus = RecordFocus(kit);
        var first = await StartHeldAsync(kit, ignoresToken: false);

        var b = await kit.Cut.InvokeAsync(() => kit.Form.SubmitAsync());
        await first;
        await kit.Cut.InvokeAsync(() => { });
        var after = await kit.Cut.InvokeAsync(kit.Read);
        await SettleAsync(kit);

        Assert.True(b.CanProceed);
        Assert.Equal(1, saves);
        Assert.Empty(atFocus);
        Assert.False(after.InputDisabled);
    }

    // C3, the recipe's double-save remedy. A passes and saves behind a _saving flag; B is pressed
    // during A's save, passes, and its OnValidSubmit returns at once. One save, and the lock stays
    // up after B ends until A's save ends.
    [Fact]
    public async Task C3_the_double_save_remedy_keeps_the_lock_until_the_first_save_ends()
    {
        var saving = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var savingFlag = false;
        var saves = 0;
        var kit = await RenderFormAsync(
            new Account(),
            onValid: async _ =>
            {
                if (savingFlag)
                {
                    return;
                }

                savingFlag = true;
                saves++;
                try
                {
                    await saving.Task;
                }
                finally
                {
                    savingFlag = false;
                }
            },
            onInvalid: _ => Task.CompletedTask);

        var first = kit.Cut.InvokeAsync(() => kit.Form.SubmitAsync());
        Assert.True(await UntilAsync(() => savingFlag));
        Assert.True((await kit.Cut.InvokeAsync(() => kit.Form.SubmitAsync())).CanProceed);
        var afterB = await kit.Cut.InvokeAsync(kit.Read);

        saving.SetResult();
        Assert.True((await first).CanProceed);
        var afterA = await kit.Cut.InvokeAsync(kit.Read);
        await SettleAsync(kit);

        Assert.Equal(1, saves);
        Assert.True(afterB.InputDisabled);
        Assert.False(afterA.InputDisabled);
    }

    // C8. B starts between A's passing answer and A's resume in SubmitAsync, so the engine has
    // ended A's check and B overtakes nothing. Whichever reaches OnValidSubmit first saves; once
    // the other has ended, the lock stays up until the save ends.
    [Fact]
    public async Task C8_a_submit_started_after_an_answer_but_before_its_resume_does_not_lower_its_share()
    {
        var saving = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var savingFlag = false;
        var saves = 0;
        Task<SubmitOutcome>? second = null;
        var kit = await RenderFormAsync(new Account(), onValid: SaveOnce, onInvalid: _ => Task.CompletedTask);

        async Task SaveOnce(SubmitOutcome _)
        {
            if (savingFlag)
            {
                return;
            }

            savingFlag = true;
            saves++;
            try
            {
                await saving.Task;
            }
            finally
            {
                savingFlag = false;
            }
        }

        PostSecondSubmitAtTheFirstAnswer(kit, () => second = kit.Cut.InvokeAsync(() => kit.Form.SubmitAsync()));
        var first = await StartHeldAsync(kit, ignoresToken: false);
        await kit.Cut.InvokeAsync(() =>
        {
            _armed = true;
            kit.Validator.ReleaseHeldRun();
        });
        Assert.True(await UntilAsync(() => second is not null && savingFlag && (first.IsCompleted || second.IsCompleted)));

        await kit.Cut.InvokeAsync(() => { });
        var afterOther = await kit.Cut.InvokeAsync(kit.Read);

        saving.SetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(10));
        await second!.WaitAsync(TimeSpan.FromSeconds(10));
        var afterBoth = await kit.Cut.InvokeAsync(kit.Read);
        await SettleAsync(kit);

        Assert.Equal(1, saves);
        Assert.True(afterOther.InputDisabled);
        Assert.False(afterBoth.InputDisabled);
    }

    // C8b. As C8, with B's rule held, so A resumes first and is the one that saves. After B passes
    // and its handler returns at once, the lock stays up until A's save ends: A's check had
    // answered when B started, so B lowered nothing of A's.
    [Fact]
    public async Task C8b_a_submit_whose_check_answered_keeps_its_share_while_it_saves()
    {
        var saving = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var savingFlag = false;
        var saves = 0;
        Task<SubmitOutcome>? second = null;
        var kit = await RenderFormAsync(new Account(), onValid: SaveOnce, onInvalid: _ => Task.CompletedTask);

        async Task SaveOnce(SubmitOutcome _)
        {
            if (savingFlag)
            {
                return;
            }

            savingFlag = true;
            saves++;
            try
            {
                await saving.Task;
            }
            finally
            {
                savingFlag = false;
            }
        }

        PostSecondSubmitAtTheFirstAnswer(kit, () =>
        {
            kit.Validator.HoldNextRun(ignoresToken: false);
            second = kit.Cut.InvokeAsync(() => kit.Form.SubmitAsync());
        });
        var first = await StartHeldAsync(kit, ignoresToken: false);
        await kit.Cut.InvokeAsync(() =>
        {
            _armed = true;
            kit.Validator.ReleaseHeldRun();
        });
        Assert.True(await UntilAsync(() => second is not null && savingFlag));

        await kit.Cut.InvokeAsync(kit.Validator.ReleaseHeldRun);
        Assert.True((await second!.WaitAsync(TimeSpan.FromSeconds(10))).CanProceed);
        await kit.Cut.InvokeAsync(() => { });
        var afterB = await kit.Cut.InvokeAsync(kit.Read);

        saving.SetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(10));
        var afterA = await kit.Cut.InvokeAsync(kit.Read);
        await SettleAsync(kit);

        Assert.Equal(1, saves);
        Assert.True(afterB.InputDisabled);
        Assert.False(afterA.InputDisabled);
    }

    // A submit that throws before its own check begins (its model's Normalize throws) overtakes
    // nothing. The submit still checking keeps its share of IsSubmitting through its save if it
    // passes, and makes its own focus move if it blocks. Mutation that must break it: lower the
    // running submit's share and take the newer number before knowing the newer check began, and
    // the lock is down during the save, and nobody moves focus.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_newer_submit_that_throws_before_its_check_overtakes_nothing(bool passes)
    {
        var model = new NormalizingOrder { Name = passes ? "ok" : "bad" };
        var validator = new NormalizingOrderValidator();
        var saving = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool? lockedDuringSave = null;
        FormidableForm<NormalizingOrder>? form = null;
        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<NormalizingOrder>>(0);
            builder.AddComponentParameter(1, "Model", model);
            builder.AddComponentParameter(2, "Validator", new FluentValidationModelValidator<NormalizingOrder>(validator));
            builder.AddComponentParameter(3, "Options", new FormidableOptions { NormalizeOnSubmit = true });
            builder.AddComponentParameter(4, "OnValidSubmit", EventCallback.Factory.Create<SubmitOutcome>(this, async _ =>
            {
                lockedDuringSave = form!.IsSubmitting;
                await saving.Task;
            }));
            builder.AddComponentParameter(5, "ChildContent", (RenderFragment<FormidableFormContext>)(_ => inner =>
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
        form = cut.FindComponent<FormidableForm<NormalizingOrder>>().Instance;
        await cut.InvokeAsync(() => { });
        await cut.InvokeAsync(() => { });
        var focusBefore = _focus.Requests.Count;

        validator.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runs = validator.Runs;
        var first = cut.InvokeAsync(() => form.SubmitAsync());
        Assert.True(await UntilAsync(() => validator.Runs > runs));
        Assert.True(await cut.InvokeAsync(() => form.IsSubmitting));

        model.ThrowOnNormalize = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => cut.InvokeAsync(() => form.SubmitAsync()));
        model.ThrowOnNormalize = false;
        var stillLocked = await cut.InvokeAsync(() => form.IsSubmitting);

        validator.Gate.SetResult();
        Assert.True(await UntilAsync(() => first.IsCompleted || lockedDuringSave is not null));
        await cut.InvokeAsync(() => { });
        var focusMoves = _focus.Requests.Count - focusBefore;
        saving.TrySetResult();
        Assert.Equal(passes, (await first).CanProceed);

        Assert.True(stillLocked);
        if (passes)
        {
            Assert.True(lockedDuringSave);
        }
        else
        {
            Assert.Equal(1, focusMoves);
        }
    }

    // A submit that threw before its check began leaves the running submit as the one a later
    // check overtakes. So a third submit whose check begins while the first still runs lowers the
    // first one's share, and the flag falls when the third one's save ends although the first
    // one's rule, which ignores its token, has not answered. Mutation that must break it: leave
    // the throwing submit's record in place, and the third submit releases that record instead,
    // so the flag stays up after the save.
    [Fact]
    public async Task A_submit_after_one_that_threw_before_its_check_overtakes_the_running_one()
    {
        var model = new NormalizingOrder { Name = "ok" };
        var validator = new NormalizingOrderValidator();
        var saving = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saves = 0;
        FormidableForm<NormalizingOrder>? form = null;
        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<NormalizingOrder>>(0);
            builder.AddComponentParameter(1, "Model", model);
            builder.AddComponentParameter(2, "Validator", new FluentValidationModelValidator<NormalizingOrder>(validator));
            builder.AddComponentParameter(3, "Options", new FormidableOptions { NormalizeOnSubmit = true });
            builder.AddComponentParameter(4, "OnValidSubmit", EventCallback.Factory.Create<SubmitOutcome>(this, async _ =>
            {
                Interlocked.Increment(ref saves);
                await saving.Task;
            }));
            builder.AddComponentParameter(5, "ChildContent", (RenderFragment<FormidableFormContext>)(_ => inner =>
            {
                inner.OpenComponent<FormidableInputText>(0);
                inner.AddComponentParameter(1, "For", (Expression<Func<string?>>)(() => model.Name));
                inner.AddComponentParameter(2, "Value", model.Name);
                inner.AddComponentParameter(3, "ValueChanged", EventCallback.Factory.Create<string?>(this, v => model.Name = v));
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        });
        form = cut.FindComponent<FormidableForm<NormalizingOrder>>().Instance;
        await cut.InvokeAsync(() => { });
        await cut.InvokeAsync(() => { });

        var firstGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        validator.Gate = firstGate;
        var runs = validator.Runs;
        var first = cut.InvokeAsync(() => form.SubmitAsync());
        Assert.True(await UntilAsync(() => validator.Runs > runs));

        model.ThrowOnNormalize = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => cut.InvokeAsync(() => form.SubmitAsync()));
        model.ThrowOnNormalize = false;

        validator.Gate = null;
        var third = cut.InvokeAsync(() => form.SubmitAsync());
        Assert.True(await UntilAsync(() => Volatile.Read(ref saves) == 1));
        saving.SetResult();
        Assert.True((await third).CanProceed);
        var lockedAfterSave = await cut.InvokeAsync(() => form.IsSubmitting);

        firstGate.SetResult();
        Assert.False((await first).CanProceed);

        Assert.False(lockedAfterSave);
        Assert.Equal(1, saves);
        Assert.False(await cut.InvokeAsync(() => form.IsSubmitting));
    }

    public sealed class NormalizingOrder : INormalizableModel
    {
        public string? Name { get; set; } = "ok";

        public bool ThrowOnNormalize { get; set; }

        public void Normalize()
        {
            if (ThrowOnNormalize)
            {
                throw new InvalidOperationException("normalize failed");
            }
        }
    }

    public sealed class NormalizingOrderValidator : AbstractValidator<NormalizingOrder>
    {
        private int _runs;

        public NormalizingOrderValidator() =>
            RuleFor(x => x.Name).MustAsync(async (name, _) =>
            {
                Interlocked.Increment(ref _runs);
                if (Gate is { } gate)
                {
                    await gate.Task;
                }

                return name != "bad";
            }).WithMessage("Name is bad");

        public TaskCompletionSource? Gate { get; set; }

        public int Runs => Volatile.Read(ref _runs);
    }

    private bool _armed;

    // Posts the second submit from the first submit's answer, so it runs after the engine has
    // ended the first check and before the first submit resumes in SubmitAsync. Posted rather
    // than invoked, because an InvokeAsync from the dispatcher runs inline, inside the answer; the
    // posted work runs on the dispatcher, so the InvokeAsync the start makes there runs inline.
    private void PostSecondSubmitAtTheFirstAnswer(KitForm kit, Action start)
    {
        var engine = kit.Form.Engine!;
        var started = 0;
        engine.StateChanged += (_, _) =>
        {
            if (_armed && !engine.IsSubmitting && Interlocked.Exchange(ref started, 1) == 0)
            {
                SynchronizationContext.Current!.Post(_ => start(), null);
            }
        };
    }

    private static async Task<bool> UntilAsync(Func<bool> condition)
    {
        for (var waited = 0; waited < 500 && !condition(); waited++)
        {
            await Task.Delay(10);
        }

        return condition();
    }

    private async Task SettleAsync(KitForm kit)
    {
        for (var step = 0; step < 2; step++)
        {
            await kit.Cut.InvokeAsync(() => _time.Advance(TimeSpan.FromHours(1)));
            await Task.Delay(10);
            Assert.True(await UntilAsync(() => !kit.Form.Engine!.IsValidating));
        }
    }

    private async Task<KitForm> RenderFormAsync(
        Account model,
        Func<SubmitOutcome, Task> onValid,
        Func<FormidableInvalidSubmitContext, Task> onInvalid,
        Func<FieldIdentifier, ValueTask<bool>>? fallback = null)
    {
        var validator = new AccountValidator();
        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<Account>>(0);
            builder.AddComponentParameter(1, "Model", model);
            builder.AddComponentParameter(2, "Validator", new FluentValidationModelValidator<Account>(validator));
            builder.AddComponentParameter(3, "OnValidSubmit", EventCallback.Factory.Create(this, onValid));
            builder.AddComponentParameter(4, "OnInvalidSubmit", EventCallback.Factory.Create(this, onInvalid));
            if (fallback is not null)
            {
                builder.AddComponentParameter(5, "FocusFallback", fallback);
            }

            builder.AddComponentParameter(6, "ChildContent", (RenderFragment<FormidableFormContext>)(context => inner =>
            {
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
            }));
            builder.CloseComponent();
        });

        var kit = new KitForm(cut, cut.FindComponent<FormidableForm<Account>>(), validator);
        await SettleAsync(kit);
        return kit;
    }

    // Records the page as each focus request finds it, and answers as a browser does: a disabled
    // input does not take focus.
    private List<Snapshot> RecordFocus(KitForm kit)
    {
        var atFocus = new List<Snapshot>();
        _focus.OnFocus = _ =>
        {
            var seen = kit.Read();
            atFocus.Add(seen);
            _focus.Lands = !seen.InputDisabled;
        };
        return atFocus;
    }

    // Starts submit A with its async rule held, and waits until the rule is running.
    private static async Task<Task<SubmitOutcome>> StartHeldAsync(KitForm kit, bool ignoresToken = true)
    {
        var runsBefore = kit.Validator.Runs;
        var first = kit.Cut.InvokeAsync(() =>
        {
            kit.Validator.HoldNextRun(ignoresToken);
            return kit.Form.SubmitAsync();
        });
        await kit.Cut.InvokeAsync(() => { });
        Assert.True(await UntilAsync(() => kit.Validator.Runs - runsBefore == 1));
        return first;
    }
}
