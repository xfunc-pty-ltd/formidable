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
/// An edit committed while a submit runs. The edit's own live check stands down for the submit,
/// which read the value before the edit, so once the submit lands that check runs after all. The
/// field then shows what the same edit made just after the landing would show: its message, its
/// message-store entry, its summary entry, its state class and <see cref="FormidableEngine{TModel}.IsFormValid"/>.
/// Each case runs twice on fresh forms, the edit made during the submit and then just after it
/// lands, and compares the two. The controls keep the result they had: an edit a submit read, a
/// field a blocked submit flags, an edit a <see cref="FormidableOptions.LiveDebounce"/> window holds
/// until the submit ends, and an edit made during a load. Every async rule counts each run as it
/// starts, and every case counts the checks begun, against the same edit made after the landing.
/// </summary>
public class EditDuringSubmitTests : BunitContext
{
    private const string NameRequired = "Name is required";
    private const string ThirdRequired = "Third is required";
    private const string ConfirmMismatch = "Confirm must match Password";

    private static readonly TimeSpan Window = TimeSpan.FromMilliseconds(200);

    // The clock the kit's forms read from the container; the engine-level cases build their own.
    private readonly FakeTimeProvider _kitTime = new();

    public EditDuringSubmitTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<TimeProvider>(_kitTime);
        Services.AddFormidableBlazor();
    }

    public sealed class Account
    {
        public string? Name { get; set; } = "Bo";

        public string? Other { get; set; } = "x";

        public string? Third { get; set; } = "t";

        public string? Password { get; set; } = "pw";

        public string? Confirm { get; set; } = "pw";
    }

    /// <summary>A validator whose one async rule counts each run as it starts and, while <see cref="Hold"/> is set, waits on a gate.</summary>
    public abstract class HeldValidator : DraftSubmitValidator<Account>
    {
        private volatile bool _hold;
        private int _runs;
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>How many times the async rule has started.</summary>
        public int Runs => Volatile.Read(ref _runs);

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

        protected async Task HeldAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _runs);
            if (Hold)
            {
                await _gate.Task.WaitAsync(cancellationToken);
            }
        }
    }

    /// <summary>Name and Third must be filled and Confirm must match Password, in the draft bucket; the async rule sits on Other in the submit bucket, so a submit reads Name before it waits.</summary>
    public sealed class ReadsNameFirst : HeldValidator
    {
        protected override void ConfigureDraftRules()
        {
            RuleFor(m => m.Name).NotEmpty().WithMessage(NameRequired);
            RuleFor(m => m.Third).NotEmpty().WithMessage(ThirdRequired);
            RuleFor(m => m.Confirm).Equal(m => m.Password).WithMessage(ConfirmMismatch);
        }

        protected override void ConfigureSubmitRules() =>
            RuleFor(m => m.Other).MustAsync(async (_, ct) =>
            {
                await HeldAsync(ct);
                return true;
            });
    }

    /// <summary>Name's own async rule waits first and reads Name after, so an edit made while a submit waits is the value that submit judges.</summary>
    public sealed class ReadsNameLast : HeldValidator
    {
        protected override void ConfigureDraftRules()
        {
            RuleFor(m => m.Name).MustAsync(async (account, _, ct) =>
            {
                await HeldAsync(ct);
                return !string.IsNullOrEmpty(account.Name);
            }).WithMessage(NameRequired);
            RuleFor(m => m.Third).NotEmpty().WithMessage(ThirdRequired);
        }

        protected override void ConfigureSubmitRules() =>
            RuleFor(m => m.Other).NotEmpty();
    }

    /// <summary>Hides the rule-by-rule seam, so every check is one whole-profile call that can reuse no stored answer.</summary>
    private sealed class WholeProfile(IModelValidator<Account> inner) : IModelValidator<Account>
    {
        public Task<ValidationReport> ValidateAsync(
            Account model, ValidationProfile profile, CancellationToken cancellationToken = default) =>
            inner.ValidateAsync(model, profile, cancellationToken);

        public ValidationReport Validate(Account model, ValidationProfile profile) => inner.Validate(model, profile);
    }

    /// <summary>The form each case builds, and the pass it runs the edit against.</summary>
    public enum Shape
    {
        /// <summary>The defaults; the submit passes.</summary>
        PassingSubmit,

        /// <summary>The defaults; the submit blocks on Third, which it flags.</summary>
        BlockedSubmit,

        /// <summary><see cref="FormidableOptions.LiveProfile"/> narrowed to the draft rules; the submit passes.</summary>
        NarrowedLiveProfile,

        /// <summary>A validator that runs the whole profile in one call; the submit passes.</summary>
        WholeProfileValidator,

        /// <summary>Confirm is engaged and the edit changes Password, so Confirm fails; the submit passes.</summary>
        CrossFieldRule,

        /// <summary>Control: a <see cref="FormidableOptions.LiveDebounce"/> window, which closes while the submit runs.</summary>
        LiveDebounceWindow,

        /// <summary>Control: a load of values in place of the submit.</summary>
        Load,

        /// <summary>Control: an earlier blocked submit flagged Name, and the submit blocks on Third.</summary>
        FlaggedEarlier,

        /// <summary>Control: the submit reads Name after the edit, and blocks on it.</summary>
        SubmitReadsTheEdit,
    }

    /// <summary>What the watched field shows once everything settles.</summary>
    private sealed record Shown(string Messages, string Store, string Summary, string CssClass, bool IsFormValid);

    /// <summary>What a case shows, with the async rule's runs and the checks begun from the submit or load to the end.</summary>
    private sealed record Result(Shown Shown, int Runs, int ChecksBegun);

    private sealed record Form(
        FormidableEngine<Account> Engine,
        EditContext EditContext,
        Account Model,
        HeldValidator Validator,
        FakeTimeProvider Time) : IDisposable
    {
        public FieldIdentifier Field(string name) => new(Model, name);

        public void Notify(string name) => EditContext.NotifyFieldChanged(Field(name));

        public List<string> Messages(string name) => [.. Engine.GetIssues(Field(name)).Select(i => i.Message)];

        public void Dispose() => Engine.Dispose();
    }

    // Every engine dispatches one delegate at a time, as the renderer does. A released gate resumes
    // each held rule on a worker thread, so with inline dispatch two checks released together could
    // land on the engine at once, which a renderer never allows.
    private static Form Build(Shape shape, bool track = true)
    {
        var model = new Account();
        var editContext = new EditContext(model);
        HeldValidator validator = shape == Shape.SubmitReadsTheEdit ? new ReadsNameLast() : new ReadsNameFirst();
        IModelValidator<Account> modelValidator = new FluentValidationModelValidator<Account>(validator);
        if (shape == Shape.WholeProfileValidator)
        {
            modelValidator = new WholeProfile(modelValidator);
        }

        var time = new FakeTimeProvider();
        var options = new FormidableOptions
        {
            DisclosureOverride = _ => true,
            TrackFormValidity = track,
            LiveProfile = shape == Shape.NarrowedLiveProfile ? ValidationProfile.Draft : null,
            LiveDebounce = shape == Shape.LiveDebounceWindow ? Window : null,
        };
        var engine = new FormidableEngine<Account>(
            model, editContext, modelValidator, new ReflectionModelIntrospector(), options, time,
            EngineTestSync.OneAtATime());
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

    /// <summary>Moves the clock past every debounce in steps, waits out any check still running, then moves it an hour on.</summary>
    private static async Task SettleAsync(Form form)
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

    private static string WatchedOf(Shape shape) =>
        shape == Shape.CrossFieldRule ? nameof(Account.Confirm) : nameof(Account.Name);

    /// <summary>Builds the case's form, makes its edit during the submit or load, or just after it lands, and reads what the watched field shows once everything settles.</summary>
    private static async Task<Result> RunAsync(Shape shape, bool editDuring)
    {
        using var form = Build(shape);
        var watched = WatchedOf(shape);

        form.Notify(watched); // engaged, with a valid value
        await SettleAsync(form);

        if (shape == Shape.FlaggedEarlier)
        {
            form.Model.Name = string.Empty;
            form.Notify(nameof(Account.Name));
            await SettleAsync(form);
            Assert.False((await form.Engine.ValidateForSubmitAsync()).CanProceed); // flags Name
            form.Model.Name = "Bo";
            form.Notify(nameof(Account.Name));
            await SettleAsync(form);
        }

        if (shape is Shape.BlockedSubmit or Shape.FlaggedEarlier)
        {
            form.Model.Third = string.Empty;
            form.Notify(nameof(Account.Third));
            await SettleAsync(form);
        }

        void Edit()
        {
            if (shape == Shape.CrossFieldRule)
            {
                form.Model.Password = "changed";
                form.Notify(nameof(Account.Password));
            }
            else
            {
                form.Model.Name = string.Empty;
                form.Notify(nameof(Account.Name));
            }
        }

        var begun = CountChecksBegun(form.Engine);
        var runsBefore = form.Validator.Runs;
        form.Validator.Hold = true;
        var pass = shape == Shape.Load
            ? form.Engine.DiscloseLoadedValuesAsync()
            : form.Engine.ValidateForSubmitAsync();
        if (editDuring)
        {
            Edit();
            form.Time.Advance(TimeSpan.FromSeconds(1)); // every debounce comes due while the pass runs
            await Task.Delay(20);
        }

        form.Validator.Release();
        await pass;
        if (!editDuring)
        {
            Edit();
        }

        await SettleAsync(form);

        var field = form.Field(watched);
        var shown = new Shown(
            string.Join("|", form.Messages(watched)),
            string.Join("|", form.EditContext.GetValidationMessages(field)),
            string.Join("|", form.Engine.GetVisibleIssues().Select(i => $"{i.Field.FieldName}: {i.Issue.Message}")),
            FormidableCss.Compute(form.Engine.GetFieldState(field), form.Engine.Options.CssClasses),
            form.Engine.IsFormValid);
        return new Result(shown, form.Validator.Runs - runsBefore, begun());
    }

    // The edit's own live check stood down for the submit, and the submit's answer is for the value
    // before the edit. On a rule-capable validator the async rule runs as often as it did before the
    // landing gave that check back: the check given back runs it, and the re-check the edit armed
    // then finds it answered. On a whole-profile validator, which reuses nothing, the check given
    // back costs one call, so the edit costs what the same edit just after the landing costs.
    // Mutation: skip the landing's give-back, so the landing drops the whole record. Every row then
    // shows nothing on the watched field, against the message the same edit after the landing shows.
    [Theory]
    [InlineData(Shape.PassingSubmit, NameRequired, 2)]
    [InlineData(Shape.BlockedSubmit, NameRequired, 2)]
    [InlineData(Shape.NarrowedLiveProfile, NameRequired, 2)]
    [InlineData(Shape.WholeProfileValidator, NameRequired, 3)]
    [InlineData(Shape.CrossFieldRule, ConfirmMismatch, 2)]
    public async Task An_edit_during_a_submit_shows_its_message_once_the_submit_lands(
        Shape shape, string message, int runs)
    {
        var during = await RunAsync(shape, editDuring: true);
        var after = await RunAsync(shape, editDuring: false);

        Assert.Equal(message, during.Shown.Messages);
        Assert.Equal(after.Shown, during.Shown);
        Assert.Equal(runs, during.Runs);
        Assert.True(during.Runs <= after.Runs, $"{during.Runs} runs during the submit against {after.Runs} after it");
        Assert.Equal(after.ChecksBegun, during.ChecksBegun);
    }

    // Something besides the landing's give-back already shows these edits' messages, so each shows
    // what it showed before the landing gave anything back, and runs the async rule as often. A
    // window holds the edit and checks it once the submit ends. A load's own check after it answers
    // every engaged field, so nothing is given back there: the load and that check are the only
    // checks begun. A field a blocked submit flags shows the submit's answer, and a submit that read
    // the edit answers it itself. In those two the landing still gives a check back, which runs the
    // rules the re-check would have run, so the edit costs what the same edit after the landing does.
    // Mutation: give back at a load's landing too. The load row then begins a third check.
    [Theory]
    [InlineData(Shape.LiveDebounceWindow, 2)]
    [InlineData(Shape.Load, 2)]
    [InlineData(Shape.FlaggedEarlier, 2)]
    [InlineData(Shape.SubmitReadsTheEdit, 2)]
    public async Task An_edit_the_landing_already_answers_shows_its_message_as_before(Shape shape, int runs)
    {
        var during = await RunAsync(shape, editDuring: true);
        var after = await RunAsync(shape, editDuring: false);

        Assert.Equal(NameRequired, during.Shown.Messages);
        Assert.Equal(after.Shown, during.Shown);
        Assert.Equal(runs, during.Runs);
        Assert.True(during.Runs <= after.Runs, $"{during.Runs} runs during the pass against {after.Runs} after it");
        Assert.True(during.ChecksBegun <= after.ChecksBegun, $"{during.ChecksBegun} checks begun during the pass against {after.ChecksBegun} after it");
        if (shape == Shape.Load)
        {
            Assert.Equal(2, during.ChecksBegun);
        }
    }

    // Control: with no edit while the submit ran, its answer covers everything, and nothing starts
    // after it lands.
    [Fact]
    public async Task A_submit_with_no_edit_during_it_starts_nothing_after_it_lands()
    {
        using var form = Build(Shape.PassingSubmit);
        form.Notify(nameof(Account.Name));
        await SettleAsync(form);

        form.Validator.Hold = true;
        var submit = form.Engine.ValidateForSubmitAsync();
        var runs = form.Validator.Runs;
        var begun = CountChecksBegun(form.Engine);
        form.Validator.Release();
        Assert.True((await submit).CanProceed);
        await SettleAsync(form);

        Assert.Equal(0, begun());
        Assert.Equal(runs, form.Validator.Runs);
        Assert.Empty(form.Messages(nameof(Account.Name)));
    }

    // A submit the caller cancels gives back the live check that stood down for it. Here a second
    // submit begins as the first ends (a handler reacting to the end, as any work queued between
    // the two dispatches can on the renderer), so the given-back check stands down for the second.
    // The edit came before the second submit began, so its answer covers that edit, and its landing
    // gives nothing back. The re-check the edit armed still runs once, and executes nothing.
    // Mutation: give back at a submit's landing whether or not an edit arrived after it began. A
    // check then begins as the second submit lands.
    [Fact]
    public async Task A_submit_begun_after_the_edit_gives_nothing_back_as_it_lands()
    {
        using var form = Build(Shape.PassingSubmit, track: false);
        form.Notify(nameof(Account.Name));
        await SettleAsync(form);

        form.Validator.Hold = true;
        using var cancel = new CancellationTokenSource();
        var first = form.Engine.ValidateForSubmitAsync(cancel.Token); // waits on the async rule
        form.Model.Name = string.Empty;
        form.Notify(nameof(Account.Name)); // its live check stands down for the first submit

        Task<SubmitOutcome>? second = null;
        form.Engine.StateChanged += (_, _) =>
        {
            if (second is null && !form.Engine.IsValidating)
            {
                second = form.Engine.ValidateForSubmitAsync(); // waits on the async rule
            }
        };

        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.NotNull(second);
        Assert.True(form.Engine.IsValidating);

        var begun = CountChecksBegun(form.Engine);
        var runs = form.Validator.Runs;
        form.Validator.Release();
        Assert.False((await second).CanProceed); // it read the empty Name
        Assert.Equal(0, begun());
        Assert.Equal([NameRequired], form.Messages(nameof(Account.Name)));

        form.Time.Advance(TimeSpan.FromHours(1)); // the re-check the edit armed
        Assert.True(await UntilAsync(() => !form.Engine.IsValidating));
        Assert.Equal(1, begun());
        Assert.Equal(runs, form.Validator.Runs);
    }

    /// <summary>What the kit shows for Name once everything settles, with the async rule's runs and the checks begun from the submit on.</summary>
    private sealed record KitResult(string InputClass, string Message, string Summary, bool IsFormValid, int Runs, int ChecksBegun);

    // The same edit through the kit: a real form whose inputs register their fields, with no
    // override. Mutation: skip the landing's give-back. The input then wears no state class, the
    // field message is empty and the summary leaves Name out, while IsFormValid is false.
    [Theory]
    [InlineData(false)] // the submit passes
    [InlineData(true)] // the submit blocks on Third
    public async Task Through_the_kit_an_edit_during_a_submit_shows_its_message(bool blocked)
    {
        var during = await RunKitAsync(blocked, editDuring: true);
        var after = await RunKitAsync(blocked, editDuring: false);

        Assert.Contains("formidable-invalid", during.InputClass);
        Assert.Equal(NameRequired, during.Message);
        Assert.Contains(NameRequired, during.Summary);
        Assert.False(during.IsFormValid);
        Assert.Equal(after, during);
    }

    private async Task<KitResult> RunKitAsync(bool blocked, bool editDuring)
    {
        var model = new Account();
        var validator = new ReadsNameFirst();
        var options = new FormidableOptions { TrackFormValidity = true };

        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<Account>>(0);
            builder.AddComponentParameter(1, "Model", model);
            builder.AddComponentParameter(2, "Validator", new FluentValidationModelValidator<Account>(validator));
            builder.AddComponentParameter(3, "Options", options);
            builder.AddComponentParameter(4, "ChildContent", (RenderFragment<FormidableFormContext>)(_ => inner =>
            {
                inner.OpenComponent<FormidableInputText>(0);
                inner.AddComponentParameter(1, "For", (Expression<Func<string?>>)(() => model.Name));
                inner.AddComponentParameter(2, "Value", model.Name);
                inner.AddComponentParameter(3, "ValueChanged", EventCallback.Factory.Create<string?>(this, v => model.Name = v));
                inner.CloseComponent();

                inner.OpenComponent<FormidableFieldMessage<string?>>(4);
                inner.AddComponentParameter(5, "For", (Expression<Func<string?>>)(() => model.Name));
                inner.CloseComponent();

                inner.OpenComponent<FormidableInputText>(6);
                inner.AddComponentParameter(7, "For", (Expression<Func<string?>>)(() => model.Third));
                inner.AddComponentParameter(8, "Value", model.Third);
                inner.AddComponentParameter(9, "ValueChanged", EventCallback.Factory.Create<string?>(this, v => model.Third = v));
                inner.CloseComponent();

                inner.OpenComponent<FormidableSummary>(10);
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

        await cut.InvokeAsync(() => cut.FindAll("input")[0].Change("Bo")); // engaged, valid
        await Settle();
        if (blocked)
        {
            await cut.InvokeAsync(() => cut.FindAll("input")[1].Change(string.Empty));
            await Settle();
        }

        var begun = CountChecksBegun(engine);
        var runsBefore = validator.Runs;
        validator.Hold = true;
        var submit = cut.InvokeAsync(() => form.Instance.SubmitAsync());
        await cut.InvokeAsync(() => { }); // the submit has begun and waits on the async rule
        if (editDuring)
        {
            await cut.InvokeAsync(() => cut.FindAll("input")[0].Change(string.Empty));
        }

        await cut.InvokeAsync(validator.Release);
        await submit;
        if (!editDuring)
        {
            await cut.InvokeAsync(() => cut.FindAll("input")[0].Change(string.Empty));
        }

        await Settle();

        var result = new KitResult(
            cut.FindAll("input")[0].GetAttribute("class") ?? string.Empty,
            string.Join("|", cut.FindComponent<FormidableFieldMessage<string?>>().Nodes.Select(n => n.TextContent.Trim()).Where(t => t.Length > 0)),
            string.Join("|", cut.FindComponent<FormidableSummary>().Nodes.Select(n => n.TextContent.Trim()).Where(t => t.Length > 0)),
            engine.IsFormValid,
            validator.Runs - runsBefore,
            begun());
        await DisposeComponentsAsync();
        return result;
    }
}
