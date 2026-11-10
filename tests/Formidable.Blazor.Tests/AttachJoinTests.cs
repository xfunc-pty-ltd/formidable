using System.Linq.Expressions;
using Bunit;
using Bunit.Rendering;
using FluentValidation;
using Formidable.Blazor.Tests.Fixtures;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Microsoft.JSInterop;
using static Formidable.Blazor.Tests.Fixtures.EngineTestSync;

namespace Formidable.Blazor.Tests;

/// <summary>
/// A page's own <see cref="EditForm"/> wired with <c>OnValidSubmit</c> and <c>OnInvalidSubmit</c>,
/// with a <see cref="FormidableValidator{TModel}"/> inside it. On .NET 11 the validator answers the
/// form's validation request with its own submit, so <c>EditForm</c> picks the callback that submit
/// earns. On .NET 10 nothing answers the request, and a pristine form reaches <c>OnValidSubmit</c>.
/// </summary>
/// <remarks>
/// Name must hold a value and warns on "Bo"; Email must hold a value with an "@" in it; Other has an
/// async rule a test can hold, once (ignoring its token or honouring it) or on every run, and can
/// make throw. The focus double records each request the form makes.
/// </remarks>
public class AttachJoinTests : BunitContext
{
    private const string NameRequired = "Name is required";
    private const string BoUnusual = "Bo is unusual";
    private const string EmailRequired = "Email is required";
    private const string EmailInvalid = "Email needs an @";

    private readonly FakeTimeProvider _time = new();
    private readonly RecordingFocusService _focus = new();

    // Each EditForm callback as it runs, in order: "valid" or "invalid".
    private readonly List<string> _callbacks = [];

#if NET11_0_OR_GREATER
    // Whether the page's OnInvalidSubmit throws once it has recorded its call.
    private bool _invalidThrows;
#endif

    // The script module's plan, loose so the click guard's calls answer; a test can plan one call.
    private readonly BunitJSModuleInterop _module;

    public AttachJoinTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        _module = JSInterop.SetupModule("./_content/Formidable.Blazor/formidable.js");
        _module.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IFormidableFocusService>(_focus);
        Services.AddSingleton<IFormidableFieldOrderService>(new RecordingFieldOrderService());
        Services.AddSingleton<IFormidableDomValueSync>(new RecordingDomValueSync());
        Services.AddSingleton<TimeProvider>(_time);
        Services.AddFormidableBlazor();
    }

    public sealed class Signup : INormalizableModel
    {
        public string? Name { get; set; }

        public string? Email { get; set; }

        public string? Other { get; set; } = "x";

        public void Normalize()
        {
            Name = Name?.Trim();
            Email = Email?.Trim();
        }
    }

    public sealed class SignupValidator : DraftSubmitValidator<Signup>
    {
        private TaskCompletionSource? _armed;
        private bool _armedIgnoresToken;
        private TaskCompletionSource? _held;
        private volatile bool _holdEvery;
        private volatile TaskCompletionSource _everyGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _runs;
        private int _nameReads;

        /// <summary>How many times Other's async rule has started.</summary>
        public int Runs => Volatile.Read(ref _runs);

        /// <summary>How many times Name's required rule has read the value.</summary>
        public int NameReads => Volatile.Read(ref _nameReads);

        /// <summary>Whether Other's async rule throws when it runs.</summary>
        public bool Throw { get; set; }

        /// <summary>Holds the next run of the async rule until <see cref="ReleaseHeldRun"/>.</summary>
        public void HoldNextRun(bool ignoresToken = true)
        {
            _armedIgnoresToken = ignoresToken;
            _armed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void ReleaseHeldRun() => _held?.TrySetResult();

        /// <summary>Whether every run that starts waits on a shared gate.</summary>
        public bool HoldEveryRun
        {
            get => _holdEvery;
            set => _holdEvery = value;
        }

        /// <summary>Lets the runs held on the shared gate answer, and holds every later run on a fresh gate.</summary>
        public void ReleaseHeldRuns()
        {
            var held = _everyGate;
            _everyGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            held.TrySetResult();
        }

        /// <summary>Lets every run held on the shared gate, and every later run, answer.</summary>
        public void ReleaseEveryRun()
        {
            _holdEvery = false;
            _everyGate.TrySetResult();
        }

        protected override void ConfigureDraftRules()
        {
        }

        protected override void ConfigureSubmitRules()
        {
            RuleFor(m => m.Name).Must(name =>
            {
                Interlocked.Increment(ref _nameReads);
                return !string.IsNullOrWhiteSpace(name);
            }).WithMessage(NameRequired);
            RuleFor(m => m.Name).Must(name => name != "Bo").WithSeverity(Severity.Warning).WithMessage(BoUnusual);
            RuleFor(m => m.Email).NotEmpty().WithMessage(EmailRequired);
            RuleFor(m => m.Email).Must(email => string.IsNullOrEmpty(email) || email.Contains('@')).WithMessage(EmailInvalid);
            RuleFor(m => m.Other).MustAsync(async (_, ct) =>
            {
                Interlocked.Increment(ref _runs);
                if (Throw)
                {
                    throw new InvalidOperationException("the async rule failed");
                }

                var gate = Interlocked.Exchange(ref _armed, null);
                if (gate is not null)
                {
                    _held = gate;
                    await (_armedIgnoresToken ? gate.Task : gate.Task.WaitAsync(ct));
                }

                if (_holdEvery)
                {
                    await _everyGate.Task.WaitAsync(ct);
                }

                return true;
            });
        }
    }

    private sealed class Page(
        IRenderedComponent<ContainerFragment> cut, Signup model, EditContext editContext, SignupValidator rules)
    {
        public IRenderedComponent<ContainerFragment> Cut { get; } = cut;

        public Signup Model { get; } = model;

        public EditContext EditContext { get; } = editContext;

        public SignupValidator Rules { get; } = rules;

        public FormidableValidator<Signup> Validator => Cut.FindComponent<FormidableValidator<Signup>>().Instance;

        public IFormidableEngine Engine => Validator.Engine!;

        public FormidableFormContext Context => Cut.FindComponent<CascadingValue<FormidableFormContext>>().Instance.Value!;

        public FieldIdentifier Name => new(Model, nameof(Signup.Name));

        public FieldIdentifier Email => new(Model, nameof(Signup.Email));

        /// <summary>Submits the rendered form and waits for its handler, callbacks included.</summary>
        public Task SubmitAsync() => Cut.InvokeAsync(() => Cut.Find("form").SubmitAsync());

        /// <summary>Submits the rendered form and hands back the handler's task without waiting for it.</summary>
        public async Task<Task> StartSubmitAsync()
        {
            Task? submit = null;
            await Cut.InvokeAsync(() => { submit = Cut.Find("form").SubmitAsync(); });
            await Cut.InvokeAsync(() => { });
            return submit!;
        }

        public Task EditAsync(string? name) => Cut.InvokeAsync(() =>
        {
            Model.Name = name;
            EditContext.NotifyFieldChanged(Name);
        });
    }

    /// <summary>
    /// The page: an <see cref="EditForm"/> over its own <see cref="EditContext"/>, wired with
    /// <c>OnValidSubmit</c> and <c>OnInvalidSubmit</c> unless <paramref name="onSubmit"/> is given,
    /// and a <see cref="FormidableValidator{TModel}"/> around two kit inputs, their messages, a
    /// summary and a submit button.
    /// </summary>
    private Page RenderPage(
        Signup model,
        FormidableOptions? options = null,
        Func<EditContext, Task>? onSubmit = null,
        bool boundary = false,
        Func<FieldIdentifier, ValueTask>? prepareFocus = null,
        Func<FieldIdentifier, ValueTask<bool>>? focusFallback = null,
        bool focusFirstError = true,
        bool nativeName = false,
        bool lockOnValidationPending = false)
    {
        var rules = new SignupValidator();
        IModelValidator<Signup> validator = new FluentValidationModelValidator<Signup>(rules);
        var editContext = new EditContext(model);

        RenderFragment form = builder =>
        {
            builder.OpenComponent<EditForm>(0);
            builder.AddComponentParameter(1, nameof(EditForm.EditContext), editContext);
            if (onSubmit is null)
            {
                builder.AddComponentParameter(
                    2, nameof(EditForm.OnValidSubmit), EventCallback.Factory.Create<EditContext>(this, _ => _callbacks.Add("valid")));
                builder.AddComponentParameter(
                    3, nameof(EditForm.OnInvalidSubmit), EventCallback.Factory.Create<EditContext>(this, _ =>
                    {
                        _callbacks.Add("invalid");
#if NET11_0_OR_GREATER
                        if (_invalidThrows)
                        {
                            throw new InvalidOperationException("the page's OnInvalidSubmit failed");
                        }
#endif
                    }));
            }
            else
            {
                builder.AddComponentParameter(4, nameof(EditForm.OnSubmit), EventCallback.Factory.Create<EditContext>(this, onSubmit));
            }

            builder.AddComponentParameter(5, nameof(EditForm.ChildContent), (RenderFragment<EditContext>)(_ => inner =>
            {
#if NET11_0_OR_GREATER
                // The lock .NET 11 documents: the inputs disabled while Blazor's form-level check
                // runs. EditForm renders it again after its submit handler's first step and once
                // the handler ends.
                if (lockOnValidationPending)
                {
                    inner.OpenElement(20, "fieldset");
                    inner.AddAttribute(21, "disabled", editContext.IsValidationPending());
                }
#endif
                inner.OpenComponent<FormidableValidator<Signup>>(0);
                inner.AddComponentParameter(1, nameof(FormidableValidator<Signup>.Validator), validator);
                if (options is not null)
                {
                    inner.AddComponentParameter(2, nameof(FormidableValidator<Signup>.Options), options);
                }

                inner.AddComponentParameter(3, nameof(FormidableValidator<Signup>.ChildContent), (RenderFragment<FormidableFormContext>)(_ => content =>
                {
                    // Blazor's own InputText takes its classes from the EditContext's provider.
                    if (nativeName)
                    {
                        content.OpenComponent<InputText>(0);
                    }
                    else
                    {
                        content.OpenComponent<FormidableInputText>(0);
                    }

                    content.AddComponentParameter(1, "Value", model.Name);
                    content.AddComponentParameter(2, "ValueChanged", EventCallback.Factory.Create<string?>(this, value => model.Name = value));
                    content.AddComponentParameter(3, "ValueExpression", (Expression<Func<string?>>)(() => model.Name));
                    if (nativeName)
                    {
                        content.AddAttribute(14, "id", "native-name");
                    }

                    content.CloseComponent();
                    content.OpenComponent<FormidableFieldMessage<string?>>(4);
                    content.AddComponentParameter(5, "For", (Expression<Func<string?>>)(() => model.Name));
                    content.CloseComponent();

                    content.OpenComponent<FormidableInputText>(6);
                    content.AddComponentParameter(7, "Value", model.Email);
                    content.AddComponentParameter(8, "ValueChanged", EventCallback.Factory.Create<string?>(this, value => model.Email = value));
                    content.AddComponentParameter(9, "ValueExpression", (Expression<Func<string?>>)(() => model.Email));
                    content.CloseComponent();
                    content.OpenComponent<FormidableFieldMessage<string?>>(10);
                    content.AddComponentParameter(11, "For", (Expression<Func<string?>>)(() => model.Email));
                    content.CloseComponent();

                    content.OpenComponent<FormidableSummary>(12);
                    content.CloseComponent();
                    content.AddMarkupContent(13, "<button type=\"submit\">Save</button>");
                }));
                if (prepareFocus is not null)
                {
                    inner.AddComponentParameter(4, nameof(FormidableValidator<Signup>.PrepareFocus), prepareFocus);
                }

                if (!focusFirstError)
                {
                    inner.AddComponentParameter(5, nameof(FormidableValidator<Signup>.FocusFirstErrorOnInvalidSubmit), false);
                }

                if (focusFallback is not null)
                {
                    inner.AddComponentParameter(6, nameof(FormidableValidator<Signup>.FocusFallback), focusFallback);
                }

                inner.CloseComponent();
#if NET11_0_OR_GREATER
                if (lockOnValidationPending)
                {
                    inner.CloseElement();
                }
#endif
            }));
            builder.CloseComponent();
        };

        var cut = Render(boundary
            ? builder =>
            {
                builder.OpenComponent<ErrorBoundary>(0);
                builder.AddComponentParameter(1, nameof(ErrorBoundary.ChildContent), form);
                builder.AddComponentParameter(2, nameof(ErrorBoundary.ErrorContent), (RenderFragment<Exception>)(exception => content =>
                {
                    content.OpenElement(0, "p");
                    content.AddAttribute(1, "class", "boundary-error");
                    content.AddContent(2, exception.GetType().Name);
                    content.CloseElement();
                }));
                builder.CloseComponent();
            }
            : form);

        return new Page(cut, model, editContext, rules);
    }

#if NET11_0_OR_GREATER
    // Pin 1. A pristine form: the submit blocks, so EditForm calls OnInvalidSubmit alone, both
    // fields' messages show, and focus moves to the first error.
    // Mutation that must break it: answer nothing (no subscription), and OnValidSubmit runs on the
    // pristine model.
    [Fact]
    public async Task A_pristine_form_calls_OnInvalidSubmit_shows_the_messages_and_moves_focus()
    {
        var page = RenderPage(new Signup());

        await page.SubmitAsync();
        await page.Cut.InvokeAsync(() => { });

        Assert.Equal(["invalid"], _callbacks);
        Assert.Contains(NameRequired, page.Cut.Markup, StringComparison.Ordinal);
        Assert.Contains(EmailRequired, page.Cut.Markup, StringComparison.Ordinal);
        Assert.Equal([page.Name], _focus.Requests);
    }

    // Pin 1, when the messages are gone by the time EditForm reads them. The submit blocks on
    // Name while a check the page registered on the same request still runs, and in that time the
    // visitor fixes Name, so the live check clears the EditContext's only message. The press
    // still blocked: EditForm calls OnInvalidSubmit.
    // Mutation that must break it: fail the join's task only for a submit that wrote nothing (a
    // blocked outcome whose summary names something counts as answered). The cleared messages
    // then let EditForm call OnValidSubmit.
    [Fact]
    public async Task A_blocked_submit_whose_messages_clear_while_another_check_runs_calls_OnInvalidSubmit()
    {
        var page = RenderPage(new Signup { Email = "a@b" });
        var other = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        page.EditContext.OnValidationRequested += (_, args) => args.AddAsyncValidator(_ => other.Task);

        var submit = await page.StartSubmitAsync();
        Assert.True(await UntilAsync(() => page.Engine.HasSubmitted && !page.Engine.IsValidating));
        Assert.Equal([NameRequired], await page.Cut.InvokeAsync(() => page.EditContext.GetValidationMessages().ToList()));

        await page.EditAsync("Al");
        await page.Cut.InvokeAsync(() => _time.Advance(TimeSpan.FromSeconds(1)));
        Assert.True(await UntilAsync(() => !page.EditContext.GetValidationMessages().Any()));

        await page.Cut.InvokeAsync(other.SetResult);
        await submit;
        await page.Cut.InvokeAsync(() => { });

        Assert.Equal(["invalid"], _callbacks);
    }

    // The move a blocked submit makes runs once EditContext.ValidateAsync() has ended, so Blazor's
    // form-level IsValidationPending() reads false, the page has rendered its lock on that flag
    // away, and EditForm has called OnInvalidSubmit. A rule held while the submit checks raises
    // the flag and the lock; with none held the request may end at once.
    // Mutation that must break it: move inside the join's own task, as before. The move then runs
    // with the flag up and the fieldset disabled (held row), and before OnInvalidSubmit (both rows).
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_blocked_submits_focus_move_runs_once_the_EditForms_validation_has_ended(bool holdTheCheck)
    {
        var page = RenderPage(new Signup { Email = "a@b" }, lockOnValidationPending: true);
        var atMove = new List<(bool Pending, bool Disabled, string Callbacks)>();
        _focus.OnFocus = _ => atMove.Add((
            page.EditContext.IsValidationPending(),
            page.Cut.Find("fieldset").HasAttribute("disabled"),
            string.Join(",", _callbacks)));

        if (holdTheCheck)
        {
            page.Rules.HoldNextRun();
        }

        var submit = await page.StartSubmitAsync();
        if (holdTheCheck)
        {
            Assert.True(await UntilAsync(() => page.Rules.Runs == 1));
            var during = await page.Cut.InvokeAsync(() => (
                page.EditContext.IsValidationPending(),
                page.Cut.Find("fieldset").HasAttribute("disabled")));
            Assert.Equal((true, true), during);
            await page.Cut.InvokeAsync(page.Rules.ReleaseHeldRun);
        }

        await submit;
        await page.Cut.InvokeAsync(() => { });

        Assert.Equal(["invalid"], _callbacks);
        Assert.Equal([(false, false, "invalid")], atMove);
        Assert.Equal([page.Name], _focus.Requests);
    }

    // The same move beside a check the page registered on the same request, which keeps Blazor's
    // flag up after this component's submit has blocked. The move waits for the request to end,
    // not only for this component's part of it.
    // Mutations that must break it: move as soon as this component's part ends (drop the wait for
    // the flag); look at the flag before Blazor has raised it (drop the step off the work item that
    // ends this component's part).
    [Fact]
    public async Task A_blocked_submits_focus_move_waits_for_every_check_the_request_runs()
    {
        var page = RenderPage(new Signup(), lockOnValidationPending: true);
        var other = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        page.EditContext.OnValidationRequested += (_, args) => args.AddAsyncValidator(_ => other.Task);
        var atMove = new List<(bool Pending, bool Disabled)>();
        _focus.OnFocus = _ => atMove.Add((
            page.EditContext.IsValidationPending(),
            page.Cut.Find("fieldset").HasAttribute("disabled")));

        var submit = await page.StartSubmitAsync();
        Assert.True(await UntilAsync(() => page.Engine.HasSubmitted && !page.Engine.IsValidating));
        await page.Cut.InvokeAsync(() => { });
        await page.Cut.InvokeAsync(() => { });
        var held = await page.Cut.InvokeAsync(() => (
            page.EditContext.IsValidationPending(), _focus.Requests.Count, _callbacks.Count));

        await page.Cut.InvokeAsync(other.SetResult);
        await submit;
        await page.Cut.InvokeAsync(() => { });

        Assert.Equal((true, 0, 0), held);
        Assert.Equal(["invalid"], _callbacks);
        Assert.Equal([(false, false)], atMove);
        Assert.Equal([page.Name], _focus.Requests);
    }

    // A blocked submit fails the join's task, which Blazor records on the EditContext as a whole:
    // the form-level IsValidationFaulted() reads true and the field-level one stays false, so a
    // native input's classes are untouched. The input reads invalid, then valid once its value is
    // fixed; a later submit that may proceed clears the form-level flag. A native input registers
    // nothing, so the override discloses its error, as an anchor beside it would.
    [Fact]
    public async Task A_blocked_submit_marks_only_the_form_faulted_and_leaves_a_native_inputs_classes_alone()
    {
        var page = RenderPage(
            new Signup { Email = "a@b" }, new FormidableOptions { DisclosureOverride = _ => true }, nativeName: true);

        await page.SubmitAsync();
        var afterBlock = await page.Cut.InvokeAsync(() => (
            Form: page.EditContext.IsValidationFaulted(),
            Field: page.EditContext.IsValidationFaulted(page.Name),
            Class: page.Cut.Find("#native-name").ClassName));

        await page.Cut.InvokeAsync(() => page.Cut.Find("#native-name").Change("Al"));
        await page.Cut.InvokeAsync(() => _time.Advance(TimeSpan.FromSeconds(1)));
        Assert.True(await UntilAsync(() => !page.Engine.IsValidating));
        var afterEdit = await page.Cut.InvokeAsync(() => (
            Form: page.EditContext.IsValidationFaulted(),
            Field: page.EditContext.IsValidationFaulted(page.Name),
            Class: page.Cut.Find("#native-name").ClassName));

        await page.SubmitAsync();
        var afterPass = await page.Cut.InvokeAsync(() => page.EditContext.IsValidationFaulted());

        Assert.Equal(["invalid", "valid"], _callbacks);
        Assert.Equal((true, false), (afterBlock.Form, afterBlock.Field));
        Assert.Contains("formidable-invalid", afterBlock.Class, StringComparison.Ordinal);
        Assert.Equal((true, false), (afterEdit.Form, afterEdit.Field));
        Assert.Contains("formidable-valid", afterEdit.Class, StringComparison.Ordinal);
        Assert.False(afterPass);
    }

    // Pin 2. A form that passes calls OnValidSubmit alone, and so does one whose only issue is a
    // warning, which never reaches the EditContext's messages.
    // Mutation that must break it: fault the join's task for every outcome. Both rows then call
    // OnInvalidSubmit.
    [Theory]
    [InlineData("Al")]
    [InlineData("Bo")]
    public async Task A_form_that_passes_calls_OnValidSubmit(string name)
    {
        var page = RenderPage(new Signup { Name = name, Email = "a@b" });

        await page.SubmitAsync();
        await page.Cut.InvokeAsync(() => { });

        Assert.Equal(["valid"], _callbacks);
        Assert.Equal(1, page.Rules.Runs);
        Assert.Empty(_focus.Requests);
    }

    // Pin 3. A rule that throws blocks the submit: OnInvalidSubmit, with the fault message showing.
    [Fact]
    public async Task A_rule_that_throws_calls_OnInvalidSubmit_with_the_fault_message_showing()
    {
        var page = RenderPage(new Signup { Name = "Al", Email = "a@b" });
        page.Rules.Throw = true;

        await page.SubmitAsync();

        Assert.Equal(["invalid"], _callbacks);
        Assert.Contains(new FormidableOptions().ValidationFaultMessage, page.Cut.Markup, StringComparison.Ordinal);
        Assert.False(Renderer.UnhandledException.IsCompleted);
    }

    // Pin 3, when the fault message is gone by the time EditForm reads the messages: a
    // ValidationFaulted handler that applies an empty server reply clears it. The rule still
    // failed and nothing answered, so EditForm calls OnInvalidSubmit.
    // Mutation that must break it: count a submit whose rule failed as answered. EditForm then
    // finds no message and calls OnValidSubmit.
    [Fact]
    public async Task A_rule_failure_whose_message_a_handler_cleared_still_calls_OnInvalidSubmit()
    {
        var page = RenderPage(new Signup { Name = "Al", Email = "a@b" });
        page.Rules.Throw = true;
        var validator = page.Validator;
        page.Engine.ValidationFaulted += (_, _) => validator.ApplyServerIssues(Array.Empty<ValidationIssue>());

        await page.SubmitAsync();

        Assert.Equal(["invalid"], _callbacks);
        Assert.Empty(page.EditContext.GetValidationMessages());
    }

    // Pin 4, first half. A second submit while the first still checks, on a form that passes with
    // a warning showing. Blazor starts a second validation and cancels neither, so each submit gets
    // a callback of its own. The first's rule ignores its token and answers after the second has
    // saved: OnValidSubmit once, from the second, and the first ends in OnInvalidSubmit with no
    // focus move, although the warning is there to move to.
    // Mutations that must break it: drop the fault for a submit that did not answer (the first then
    // calls OnValidSubmit too); move focus on every blocked outcome (the first then asks for Name).
    [Fact]
    public async Task A_second_submit_saves_once_and_the_overtaken_first_calls_OnInvalidSubmit_with_no_focus_move()
    {
        var page = RenderPage(new Signup { Name = "Bo", Email = "a@b" });
        await page.SubmitAsync();
        Assert.Contains(BoUnusual, page.Cut.Markup, StringComparison.Ordinal);
        _callbacks.Clear();

        page.Rules.HoldNextRun(ignoresToken: true);
        var first = await page.StartSubmitAsync();
        Assert.True(await UntilAsync(() => page.Rules.Runs == 2));

        await page.SubmitAsync();
        Assert.Equal(["valid"], _callbacks);
        Assert.False(first.IsCompleted);

        await page.Cut.InvokeAsync(page.Rules.ReleaseHeldRun);
        await first;
        await page.Cut.InvokeAsync(() => { });

        Assert.Equal(["valid", "invalid"], _callbacks);
        Assert.Empty(_focus.Requests);

        // The first press's call ended last, so the form that saved reads faulted.
        Assert.True(await page.Cut.InvokeAsync(() => page.EditContext.IsValidationFaulted()));
    }

    // Pin 4, the same two submits when the first's rule honours its token, so the first ends as
    // the second begins. Each still gets one callback, and the overtaken one moves no focus.
    [Fact]
    public async Task A_second_submit_that_cancels_the_first_check_still_saves_once()
    {
        var page = RenderPage(new Signup { Name = "Bo", Email = "a@b" });
        await page.SubmitAsync();
        _callbacks.Clear();

        page.Rules.HoldNextRun(ignoresToken: false);
        var first = await page.StartSubmitAsync();
        Assert.True(await UntilAsync(() => page.Rules.Runs == 2));

        await page.SubmitAsync();
        await first;
        await page.Cut.InvokeAsync(() => { });

        Assert.Equal(1, _callbacks.Count(c => c == "valid"));
        Assert.Equal(1, _callbacks.Count(c => c == "invalid"));
        Assert.Empty(_focus.Requests);
    }

    // Pin 4, second half. A value edited during each of the submit's three checks: the last check
    // passes, but an edit outdated it too, so nothing answered for the values the model holds and
    // EditForm calls OnInvalidSubmit.
    // Mutation that must break it: drop the fault for a submit that did not answer.
    [Fact]
    public async Task A_submit_edited_during_each_of_its_checks_calls_OnInvalidSubmit()
    {
        var page = RenderPage(new Signup { Name = "Al", Email = "a@b" });
        page.Rules.HoldEveryRun = true;

        var submit = await page.StartSubmitAsync();
        var made = 0;
        foreach (var value in new[] { "Ann", "Bea", "Cy" })
        {
            Assert.True(await UntilAsync(() => page.Rules.Runs > made));
            await page.EditAsync(value);
            made++;
            if (made < 3)
            {
                await page.Cut.InvokeAsync(page.Rules.ReleaseHeldRuns);
            }
        }

        await page.Cut.InvokeAsync(page.Rules.ReleaseEveryRun);
        await submit;
        await page.Cut.InvokeAsync(() => { });

        Assert.Equal(["invalid"], _callbacks);
        Assert.Equal("Cy", page.Model.Name);
    }

    // Pin 4b. A load of values that discloses an error, started while the submit checks. The load
    // takes the submit's place, so the submit answers nothing and EditForm calls OnInvalidSubmit.
    // A load is not a newer submit: as under FormidableForm, the move still runs, and lands on the
    // error the load disclosed. The submit's rule ignores its token, so it ends after the load.
    // Mutation that must break it: make no move for any submit that did not answer. Focus then
    // stays where it was.
    [Fact]
    public async Task A_load_that_takes_the_submits_place_calls_OnInvalidSubmit_and_focus_moves_to_its_error()
    {
        var page = RenderPage(new Signup { Name = "Al", Email = "bad" });

        page.Rules.HoldNextRun(ignoresToken: true);
        var submit = await page.StartSubmitAsync();
        Assert.True(await UntilAsync(() => page.Rules.Runs == 1));

        await page.Cut.InvokeAsync(() => page.Validator.DiscloseLoadedValuesAsync());
        Assert.Contains(EmailInvalid, page.Cut.Markup, StringComparison.Ordinal);
        Assert.Empty(_focus.Requests);

        await page.Cut.InvokeAsync(page.Rules.ReleaseHeldRun);
        await submit;
        await page.Cut.InvokeAsync(() => { });

        Assert.Equal(["invalid"], _callbacks);
        Assert.Equal([page.Email], _focus.Requests);
    }

    // Pin 5. A submit profile that selects no rule is the app's setup error, not a rule failure.
    // It reaches the boundary around the page, or with none the renderer's unhandled path (which
    // bUnit rethrows from the submit event), as it does through OnSubmit. EditForm then calls
    // OnInvalidSubmit, never OnValidSubmit.
    // Mutation that must break it: let Blazor contain the exception (fail the join's task without
    // handing it on). The boundary stays empty and the renderer hears nothing.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_misconfigured_submit_profile_reaches_the_fault_path_and_OnValidSubmit_does_not_run(bool boundary)
    {
        var options = new FormidableOptions
        {
            SubmitProfile = ValidationProfile.Named("Aprove", includeDefaultRules: false, "Aprove"),
        };
        var page = RenderPage(new Signup { Name = "Al", Email = "a@b" }, options, boundary: boundary);

        if (boundary)
        {
            await page.SubmitAsync();
            await page.Cut.InvokeAsync(() => { });
            Assert.Equal(nameof(InvalidOperationException), page.Cut.Find("p.boundary-error").TextContent);
            Assert.False(Renderer.UnhandledException.IsCompleted);
        }
        else
        {
            var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => page.SubmitAsync());
            Assert.Same(thrown, await Renderer.UnhandledException);
        }

        Assert.Equal(["invalid"], _callbacks);
    }

    // A static render (a form posted with no render mode, or in the prerender of a page that
    // names its form) runs the request where no script can run. A blocked submit then makes no
    // focus move, so the focus service, which throws as Blazor's script runtime does there, is
    // never called and nothing reaches the renderer's unhandled path.
    // Mutation that must break it: move focus whatever the renderer says. The service then
    // throws, the join hands that on, and bUnit rethrows it from the submit.
    [Fact]
    public async Task A_blocked_submit_on_a_static_renderer_makes_no_focus_move()
    {
        var focus = new StaticRenderFocusService();
        Services.AddSingleton<IFormidableFocusService>(focus);
        SetRendererInfo(new RendererInfo("Static", isInteractive: false));
        var page = RenderPage(new Signup());

        await page.SubmitAsync();
        await page.Cut.InvokeAsync(() => { });

        Assert.Equal(["invalid"], _callbacks);
        Assert.Contains(NameRequired, page.Cut.Markup, StringComparison.Ordinal);
        Assert.Equal(0, focus.Calls);
        Assert.False(Renderer.UnhandledException.IsCompleted);
    }

    // The shipped focus service, its script call failing as a lost connection or a script error
    // makes it fail. The move then moves nothing, and the blocked answer stands: OnInvalidSubmit,
    // and nothing reaches the renderer's unhandled path.
    // Mutation that must break it: drop the catch for a failed script call around the move. The
    // JSException then goes to the fault path, and bUnit rethrows it from the submit.
    [Fact]
    public async Task A_focus_move_whose_script_call_fails_leaves_the_blocked_answer_standing()
    {
        Services.AddScoped<IFormidableFocusService>(sp => new FormidableFocusService(sp.GetRequiredService<IJSRuntime>()));
        _module.Setup<bool>("focusField", _ => true).SetException(new JSException("focusField failed"));
        var page = RenderPage(new Signup());

        await page.SubmitAsync();
        await page.Cut.InvokeAsync(() => { });

        Assert.Equal(["invalid"], _callbacks);
        Assert.Single(_module.Invocations["focusField"]);
        Assert.False(Renderer.UnhandledException.IsCompleted);
    }

    // The move's checks after its wait, when the page's OnInvalidSubmit throws under a boundary: the
    // boundary takes the page's form away, this component with it, before the move runs, so no
    // move is made for a form that is gone.
    // Mutation that must break it: drop the disposal and engine checks after the wait. The move then
    // asks for focus on Name.
    [Fact]
    public async Task A_page_whose_OnInvalidSubmit_throws_under_a_boundary_gets_no_focus_move()
    {
        _invalidThrows = true;
        var page = RenderPage(new Signup(), boundary: true);

        await page.SubmitAsync();
        await page.Cut.InvokeAsync(() => { });
        await page.Cut.InvokeAsync(() => { });

        Assert.Equal(["invalid"], _callbacks);
        Assert.Equal(nameof(InvalidOperationException), page.Cut.Find("p.boundary-error").TextContent);
        Assert.Empty(_focus.Requests);
        Assert.False(Renderer.UnhandledException.IsCompleted);
    }

    // The same checks when this component is disposed while a check the page registered on the same
    // request still runs: once the request ends, nothing moves.
    // Mutation that must break it: drop the disposal and engine checks after the wait.
    [Fact]
    public async Task A_validator_disposed_while_the_move_waits_moves_nothing()
    {
        var page = RenderPage(new Signup());
        var other = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        page.EditContext.OnValidationRequested += (_, args) => args.AddAsyncValidator(_ => other.Task);
        var validator = page.Validator;

        var submit = await page.StartSubmitAsync();
        Assert.True(await UntilAsync(() => page.Engine.HasSubmitted && !page.Engine.IsValidating));
        await page.Cut.InvokeAsync(validator.Dispose);

        await page.Cut.InvokeAsync(other.SetResult);
        await submit;
        await page.Cut.InvokeAsync(() => { });
        await page.Cut.InvokeAsync(() => { });

        Assert.Equal(["invalid"], _callbacks);
        Assert.Empty(_focus.Requests);
        Assert.False(Renderer.UnhandledException.IsCompleted);
    }

    // The engine check alone: the host replaces the EditContext while a check it registered on the
    // old one still runs, so the component holds a new engine when the old request ends, and makes
    // no move for the engine it let go.
    // Mutation that must break it: drop the engine check after the wait and keep the disposal one.
    // The move then runs for the old engine.
    [Fact]
    public async Task A_move_whose_engine_was_replaced_during_its_wait_moves_nothing()
    {
        var rules = new SignupValidator();
        var cut = Render<SwappableContextHost>(parameters => parameters
            .Add(p => p.Model, new Signup())
            .Add(p => p.Validator, new FluentValidationModelValidator<Signup>(rules)));
        var oldContext = cut.Instance.EditContext!;
        var oldEngine = cut.FindComponent<FormidableValidator<Signup>>().Instance.Engine!;
        var other = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        oldContext.OnValidationRequested += (_, args) => args.AddAsyncValidator(_ => other.Task);

        Task<bool>? validating = null;
        await cut.InvokeAsync(() => { validating = oldContext.ValidateAsync(); });
        Assert.True(await UntilAsync(() => oldEngine.HasSubmitted && !oldEngine.IsValidating));
        await cut.InvokeAsync(() => { });
        cut.Render(parameters => parameters.Add(p => p.Model, new Signup()));
        Assert.NotSame(oldContext, cut.Instance.EditContext);

        await cut.InvokeAsync(other.SetResult);
        Assert.False(await validating!);
        await cut.InvokeAsync(() => { });
        await cut.InvokeAsync(() => { });

        Assert.Empty(_focus.Requests);
        Assert.False(Renderer.UnhandledException.IsCompleted);
    }

    // The newer-submit check after the wait: a second press while a check the page registered keeps
    // the first request open. The second saves, with a warning showing; the first, overtaken, makes
    // no move once the request ends, as ValidateForSubmitAsync() does.
    // Mutation that must break it: drop the count check after the wait. The first then takes focus
    // to the warning on Name after the second saved.
    [Fact]
    public async Task A_second_press_while_the_first_move_waits_saves_and_nothing_moves()
    {
        var page = RenderPage(new Signup { Name = "Bo" });
        var other = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        page.EditContext.OnValidationRequested += (_, args) => args.AddAsyncValidator(_ => other.Task);

        var first = await page.StartSubmitAsync();
        Assert.True(await UntilAsync(() => page.Engine.HasSubmitted && !page.Engine.IsValidating));
        await page.Cut.InvokeAsync(() =>
        {
            page.Model.Email = "a@b";
            page.EditContext.NotifyFieldChanged(page.Email);
        });
        await page.Cut.InvokeAsync(() => _time.Advance(TimeSpan.FromSeconds(1)));
        Assert.True(await UntilAsync(() => !page.Engine.IsValidating));

        var second = await page.StartSubmitAsync();
        Assert.True(await UntilAsync(() => !page.Engine.IsValidating));
        await page.Cut.InvokeAsync(() => { });
        Assert.Empty(_focus.Requests);

        await page.Cut.InvokeAsync(other.SetResult);
        await first;
        await second;
        await page.Cut.InvokeAsync(() => { });
        await page.Cut.InvokeAsync(() => { });

        Assert.Equal(["invalid", "valid"], _callbacks.Order());
        Assert.Contains(BoUnusual, page.Cut.Markup, StringComparison.Ordinal);
        Assert.Empty(_focus.Requests);
    }

    // The join's catch is for the focus service's own calls: a script that fails, a runtime that is
    // gone. PrepareFocus and FocusFallback are the page's code, and what they throw reaches the
    // boundary around the page, as it does from ValidateForSubmitAsync() through OnSubmit, even when
    // it is a type a failing script call throws too (a JSException from the page's own dialog
    // script, its own timeout, a page service already disposed).
    // Mutation that must break it: catch an interop failure around the whole move. Each row's
    // exception is then dropped, and the boundary stays empty.
    [Theory]
    [InlineData("PrepareFocus", nameof(JSException))]
    [InlineData("PrepareFocus", nameof(OperationCanceledException))]
    [InlineData("FocusFallback", nameof(ObjectDisposedException))]
    public async Task A_throw_from_the_pages_own_focus_callback_reaches_the_boundary(string callback, string thrown)
    {
        Exception Thrown() => thrown switch
        {
            nameof(JSException) => new JSException("the page's dialog script failed"),
            nameof(OperationCanceledException) => new OperationCanceledException("the page's own timeout"),
            _ => new ObjectDisposedException("a page service"),
        };
        Func<FieldIdentifier, ValueTask>? prepare = callback == "PrepareFocus" ? _ => throw Thrown() : null;
        Func<FieldIdentifier, ValueTask<bool>>? fallback = callback == "FocusFallback" ? _ => throw Thrown() : null;
        _focus.Lands = fallback is null;
        var page = RenderPage(new Signup(), boundary: true, prepareFocus: prepare, focusFallback: fallback);

        await page.SubmitAsync();
        await page.Cut.InvokeAsync(() => { });
        await page.Cut.InvokeAsync(() => { });

        Assert.Equal(["invalid"], _callbacks);
        Assert.Equal(thrown, page.Cut.Find("p.boundary-error").TextContent);
        Assert.False(Renderer.UnhandledException.IsCompleted);
    }

    // The other side of that line: the retry the shipped focus service makes after a fallback that
    // answered true is one of its own script calls, and one that fails moves nothing and reaches no
    // boundary, as a failed first attempt does.
    // Mutation that must break it: catch the failure around the first attempt only. The retry's
    // JSException then reaches the boundary.
    [Fact]
    public async Task A_retry_whose_script_call_fails_moves_nothing_and_reaches_no_boundary()
    {
        Services.AddScoped<IFormidableFocusService>(sp => new FormidableFocusService(sp.GetRequiredService<IJSRuntime>()));
        _module.Setup<bool>("focusField", call => call.Arguments.Count == 2).SetResult(false);
        _module.Setup<bool>("focusField", call => call.Arguments.Count == 3).SetException(new JSException("focusField failed"));
        var fallbacks = 0;
        var page = RenderPage(new Signup(), boundary: true, focusFallback: _ =>
        {
            fallbacks++;
            return ValueTask.FromResult(true);
        });

        await page.SubmitAsync();
        await page.Cut.InvokeAsync(() => { });
        await page.Cut.InvokeAsync(() => { });

        Assert.Equal(["invalid"], _callbacks);
        Assert.Equal(1, fallbacks);
        Assert.Equal(2, _module.Invocations["focusField"].Count);
        Assert.Empty(page.Cut.FindAll("p.boundary-error"));
        Assert.False(Renderer.UnhandledException.IsCompleted);
    }

    // Pin 6. The obsolete synchronous Validate() cannot await a submit, so the request it raises
    // gets nothing from the validator: it throws nothing and runs no rule.
    // Mutation that must break it: let the refusal Blazor throws for async work on a synchronous
    // request escape the handler. Validate() then throws it.
    [Fact]
    public async Task The_synchronous_Validate_throws_nothing_and_runs_no_rule()
    {
        var page = RenderPage(new Signup());
        var reads = page.Rules.NameReads;
        var runs = page.Rules.Runs;

#pragma warning disable CS0618 // Validate is obsolete on .NET 11; this pin calls it on purpose.
        var valid = await page.Cut.InvokeAsync(() => page.EditContext.Validate());
#pragma warning restore CS0618

        Assert.True(valid);
        Assert.Equal(reads, page.Rules.NameReads);
        Assert.Equal(runs, page.Rules.Runs);
        Assert.Empty(_callbacks);
    }

    // Pin 7. A page that submits through OnSubmit and ValidateForSubmitAsync() raises no validation
    // request, so each rule runs once per submit.
    // Mutation that must break it: have ValidateForSubmitAsync() also ask the EditContext to
    // validate. Each rule then runs twice.
    [Fact]
    public async Task An_OnSubmit_page_runs_each_rule_once_per_submit()
    {
        FormidableValidator<Signup>? validator = null;
        SubmitOutcome? outcome = null;
        var page = RenderPage(
            new Signup { Name = "Al", Email = "a@b" },
            onSubmit: async _ => outcome = await validator!.ValidateForSubmitAsync());
        validator = page.Validator;
        var reads = page.Rules.NameReads;

        await page.SubmitAsync();

        Assert.True(outcome!.CanProceed);
        Assert.Equal(1, page.Rules.Runs);
        Assert.Equal(reads + 1, page.Rules.NameReads);
    }

    // Pin 8, disposal. Once the validator is gone, the page's EditContext validates without it.
    [Fact]
    public async Task After_disposal_ValidateAsync_runs_no_rule()
    {
        var page = RenderPage(new Signup());
        var reads = page.Rules.NameReads;

        await DisposeComponentsAsync();
        var valid = await page.EditContext.ValidateAsync();

        Assert.True(valid);
        Assert.Equal(reads, page.Rules.NameReads);
        Assert.Equal(0, page.Rules.Runs);
        Assert.False(Renderer.UnhandledException.IsCompleted);
    }

    // Pin 8, a replaced EditContext. The host cascades the EditContext itself, so the same
    // validator sees the replacement (an EditForm would rebuild it instead). The old EditContext
    // then validates without it.
    // Mutation that must break it: drop the unsubscribe. The old EditContext's request then runs
    // the new engine's submit.
    [Fact]
    public async Task After_the_EditContext_is_replaced_ValidateAsync_on_the_old_one_runs_no_rule()
    {
        var rules = new SignupValidator();
        var first = new Signup();
        var cut = Render<SwappableContextHost>(parameters => parameters
            .Add(p => p.Model, first)
            .Add(p => p.Validator, new FluentValidationModelValidator<Signup>(rules)));
        var oldContext = cut.Instance.EditContext!;

        cut.Render(parameters => parameters.Add(p => p.Model, new Signup()));
        Assert.NotSame(oldContext, cut.Instance.EditContext);
        var reads = rules.NameReads;

        var valid = await cut.InvokeAsync(() => oldContext.ValidateAsync());

        Assert.True(valid);
        Assert.Equal(reads, rules.NameReads);
        Assert.Equal(0, rules.Runs);
    }

    // Pin 8's case at the moment of the answer: a passing submit whose engine the component lets
    // go as that answer lands. The page replaces the EditContext from a handler of the landing's
    // own notification, so the answer belongs to an engine the component no longer holds, and
    // the old EditContext's ValidateAsync() answers false.
    // Mutation that must break it: count a submit whose engine was let go as answered. The call
    // then finds no message and answers true.
    [Fact]
    public async Task A_submit_whose_engine_is_let_go_as_its_answer_lands_answers_false()
    {
        var rules = new SignupValidator();
        var cut = Render<SwappableContextHost>(parameters => parameters
            .Add(p => p.Model, new Signup { Name = "Al", Email = "a@b" })
            .Add(p => p.Validator, new FluentValidationModelValidator<Signup>(rules)));
        var oldContext = cut.Instance.EditContext!;
        var engine = cut.FindComponent<FormidableValidator<Signup>>().Instance.Engine!;
        var swapped = false;
        oldContext.OnValidationStateChanged += (_, _) =>
        {
            if (!swapped && engine.HasSubmitted)
            {
                swapped = true;
                cut.Instance.SwapToFreshModel();
            }
        };

        rules.HoldNextRun();
        Task<bool>? validating = null;
        await cut.InvokeAsync(() => { validating = oldContext.ValidateAsync(); });
        Assert.True(await UntilAsync(() => rules.Runs == 1));
        await cut.InvokeAsync(rules.ReleaseHeldRun);

        Assert.False(await validating!);
        Assert.True(swapped);
        Assert.NotSame(oldContext, cut.Instance.EditContext);
    }

    // FocusFirstErrorOnInvalidSubmit governs the join's move as it governs the one
    // ValidateForSubmitAsync() makes: off, a blocked submit shows its messages and moves nothing.
    // Mutation that must break it: drop the switch from the join's move. Focus is then asked for.
    [Fact]
    public async Task With_the_focus_switch_off_a_blocked_submit_moves_no_focus()
    {
        var page = RenderPage(new Signup(), focusFirstError: false);

        await page.SubmitAsync();
        await page.Cut.InvokeAsync(() => { });

        Assert.Equal(["invalid"], _callbacks);
        Assert.Contains(NameRequired, page.Cut.Markup, StringComparison.Ordinal);
        Assert.Empty(_focus.Requests);
    }

    // Pin 9. The cascaded context's IsSubmitting reads false throughout, because the page owns
    // the submit's handler. The engine's own flag reads true while the submit checks, which is
    // what shows the reads were taken during it.
    // Mutation that must break it: bind the context's flag to the engine's.
    [Fact]
    public async Task The_cascaded_context_reads_not_submitting_throughout()
    {
        var page = RenderPage(new Signup { Name = "Al", Email = "a@b" });
        var context = page.Context;
        var engine = page.Engine;
        var reads = new List<(bool Context, bool Engine)>();
        engine.StateChanged += (_, _) => reads.Add((context.IsSubmitting, engine.IsSubmitting));

        page.Rules.HoldNextRun();
        var submit = await page.StartSubmitAsync();
        Assert.True(await UntilAsync(() => page.Rules.Runs == 1));
        var during = await page.Cut.InvokeAsync(() => (context.IsSubmitting, engine.IsSubmitting));

        await page.Cut.InvokeAsync(page.Rules.ReleaseHeldRun);
        await submit;
        await page.Cut.InvokeAsync(() => { });

        Assert.Equal((false, true), during);
        Assert.Contains(reads, r => r.Engine);
        Assert.All(reads, r => Assert.False(r.Context));
        Assert.Equal(["valid"], _callbacks);
    }

    // A page's own ValidateAsync() call runs the whole submit: the model is normalized first
    // under NormalizeOnSubmit, every error shows, focus moves to the first, and the form counts
    // as submitted.
    [Fact]
    public async Task A_pages_own_ValidateAsync_runs_the_submit()
    {
        var page = RenderPage(new Signup { Name = "  Al  " }, new FormidableOptions { NormalizeOnSubmit = true });

        var valid = await page.Cut.InvokeAsync(() => page.EditContext.ValidateAsync());
        await page.Cut.InvokeAsync(() => { });

        Assert.False(valid);
        Assert.Equal("Al", page.Model.Name);
        Assert.Contains(EmailRequired, page.Cut.Markup, StringComparison.Ordinal);
        Assert.Equal([page.Email], _focus.Requests);
        Assert.True(page.Engine.HasSubmitted);
    }

    // A page that cancels its own ValidateAsync() call while the submit checks gets the
    // cancellation, and nothing reaches the fault path. The cancellation ends the wait only: the
    // submit runs on and its answer lands, so the form counts as submitted.
    // Mutation that must break it: hand the caller's token to the submit. The submit then lands
    // nothing, and the form never counts as submitted.
    [Fact]
    public async Task A_cancelled_ValidateAsync_throws_the_cancellation_and_the_submit_runs_on()
    {
        var page = RenderPage(new Signup { Name = "Al", Email = "a@b" });
        using var cancel = new CancellationTokenSource();

        page.Rules.HoldNextRun();
        Task<bool>? validating = null;
        await page.Cut.InvokeAsync(() => { validating = page.EditContext.ValidateAsync(cancel.Token); });
        Assert.True(await UntilAsync(() => page.Rules.Runs == 1));

        await page.Cut.InvokeAsync(cancel.Cancel);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => validating!);
        Assert.False(page.Engine.HasSubmitted);

        await page.Cut.InvokeAsync(page.Rules.ReleaseHeldRun);
        Assert.True(await UntilAsync(() => page.Engine.HasSubmitted && !page.Engine.IsValidating));
        await page.Cut.InvokeAsync(() => { });
        Assert.False(Renderer.UnhandledException.IsCompleted);
        Assert.Empty(_focus.Requests);
    }

    // A server reply applied while a passing submit checks stands, so the submit's report passes
    // but the reply's error is in the EditContext's messages: EditForm calls OnInvalidSubmit.
    // The submit itself passed, so focus does not move.
    [Fact]
    public async Task A_server_reply_applied_during_a_passing_submit_calls_OnInvalidSubmit()
    {
        var page = RenderPage(new Signup { Name = "Al", Email = "a@b" });

        page.Rules.HoldNextRun();
        var submit = await page.StartSubmitAsync();
        Assert.True(await UntilAsync(() => page.Rules.Runs == 1));
        await page.Cut.InvokeAsync(() => page.Validator.ApplyServerIssues(
            [new ValidationIssue(nameof(Signup.Email), "Email is taken")]));

        await page.Cut.InvokeAsync(page.Rules.ReleaseHeldRun);
        await submit;
        await page.Cut.InvokeAsync(() => { });

        Assert.Equal(["invalid"], _callbacks);
        Assert.Contains("Email is taken", page.Cut.Markup, StringComparison.Ordinal);
        Assert.Empty(_focus.Requests);
    }
#else
    // The .NET 10 build answers no validation request, so a pristine attach form reaches
    // OnValidSubmit, which is why the docs route attach mode through OnSubmit there.
    [Fact]
    public async Task On_net10_a_pristine_form_reaches_OnValidSubmit()
    {
        var page = RenderPage(new Signup());

        await page.SubmitAsync();

        Assert.Equal(["valid"], _callbacks);
        Assert.Equal(0, page.Rules.Runs);
    }
#endif

    /// <summary>A focus service that throws as Blazor's script runtime throws during a static render.</summary>
    private sealed class StaticRenderFocusService : IFormidableFocusService
    {
        public int Calls { get; private set; }

        public ValueTask<bool> FocusAsync(FieldIdentifier field)
        {
            Calls++;
            throw new InvalidOperationException(
                "JavaScript interop calls cannot be issued at this time. This is because the component is being statically rendered.");
        }
    }

    /// <summary>
    /// Cascades an <see cref="EditContext"/> the host owns, rebuilt whenever <see cref="Model"/> is
    /// swapped, with a <see cref="FormidableValidator{TModel}"/> beneath it, so the same validator
    /// instance sees the replacement through its parameters.
    /// </summary>
    private sealed class SwappableContextHost : ComponentBase
    {
        private EditContext? _boundFor;
        private Signup? _boundModel;

        [Parameter]
        public Signup Model { get; set; } = default!;

        [Parameter]
        public IModelValidator<Signup>? Validator { get; set; }

        public EditContext? EditContext => _boundFor;

        /// <summary>Cascades a new <see cref="EditContext"/> over a fresh model at once, as a page that resets its form does; call it on the renderer's dispatcher.</summary>
        public void SwapToFreshModel()
        {
            _boundModel = new Signup();
            _boundFor = new EditContext(_boundModel);
            StateHasChanged();
        }

        protected override void OnParametersSet()
        {
            if (!ReferenceEquals(_boundModel, Model))
            {
                _boundModel = Model;
                _boundFor = new EditContext(Model);
            }
        }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<CascadingValue<EditContext>>(0);
            builder.AddComponentParameter(1, "Value", _boundFor);
            builder.AddComponentParameter(2, "ChildContent", (RenderFragment)(inner =>
            {
                inner.OpenComponent<FormidableValidator<Signup>>(0);
                inner.AddComponentParameter(1, nameof(FormidableValidator<Signup>.Validator), Validator);
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        }
    }
}
