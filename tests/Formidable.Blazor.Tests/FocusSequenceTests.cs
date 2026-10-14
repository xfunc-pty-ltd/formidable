using System.Linq.Expressions;
using Bunit;
using FluentValidation;
using Formidable.Blazor.Tests.Fixtures;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Formidable.Blazor.Tests;

/// <summary>
/// The sequence a page takes over a blocked submit with: the handler says it has answered the
/// block itself, and the page asks for the first-error move later, when whatever it put in front
/// of the form is out of the way again. Two claims run through everything here. The move a page
/// asks for is the move a blocked submit makes — same target, same seams, one implementation
/// behind both — and the handler's word covers the submit it was given and no other. The cases at
/// the end add a third: of two moves, the newer one wins, so an older move whose fallback answers
/// after a newer move began does not retry.
/// </summary>
public class FocusSequenceTests : BunitContext
{
    private readonly RecordingFocusService _focus = new();
    private readonly BunitJSModuleInterop _module;
    private bool _shippedFocusService;

    public FocusSequenceTests()
    {
        // No timer fires unless a test advances it, so the re-check a first render schedules
        // cannot land in the middle of a held fallback and re-render the summary under a click.
        Services.AddSingleton<TimeProvider>(new FakeTimeProvider());
        Services.AddSingleton<FluentValidation.IValidator<EngineOrder>, EngineOrderValidator>();
        _module = JSInterop.SetupModule("./_content/Formidable.Blazor/formidable.js");
        _module.Setup<IReadOnlyList<string>>("orderFields", _ => true).SetResult([]);
        _module.SetupVoid("observeLayout", _ => true).SetVoidResult();
        _module.SetupVoid("disconnectLayoutObserver", _ => true).SetVoidResult();
    }

    /// <summary>
    /// Registration is deferred out of the constructor because bUnit builds the provider on first
    /// resolution, which a render is and a constructor is not — and one test needs a host that
    /// resolves no focus service at all.
    /// </summary>
    private void RegisterServices(bool withFocusService)
    {
        if (withFocusService)
        {
            // Ahead of AddFormidableBlazor, whose focus registration is a TryAdd, so this
            // instance is the one both roots resolve. A test of the shipped service registers
            // nothing here, and the JavaScript plan stands in for the page.
            if (!_shippedFocusService)
            {
                Services.AddSingleton<IFormidableFocusService>(_focus);
            }

            Services.AddFormidableBlazor();
            return;
        }

        Services.AddFormidable();
    }

    /// <summary>
    /// Both submit-bucket presence rules fail, so there is a first error and a second one and the
    /// assertions can tell "the first" from "an error". Declaration order puts Description ahead
    /// of Customer, and no field order service answers with anything that would reorder them.
    /// </summary>
    private static EngineOrder OrderFailingOnTwoFields() => new();

    private static FieldIdentifier FirstError(EngineOrder order) =>
        new(order, nameof(EngineOrder.Description));

    private static FieldIdentifier SecondError(EngineOrder order) =>
        new(order, nameof(EngineOrder.Customer));

    private IRenderedComponent<FormidableForm<EngineOrder>> RenderForm(
        EngineOrder order,
        Action<FormidableInvalidSubmitContext>? onInvalidSubmit = null,
        bool focusFirstErrorOnInvalidSubmit = true,
        Func<FieldIdentifier, ValueTask<bool>>? focusFallback = null,
        bool withFocusService = true,
        Func<FormidableInvalidSubmitContext, Task>? onInvalidSubmitAsync = null,
        Func<FieldIdentifier, ValueTask>? prepareFocus = null,
        Action<FormidableFormContext>? captureContext = null)
    {
        RegisterServices(withFocusService);
        var container = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<EngineOrder>>(0);
            builder.AddComponentParameter(1, nameof(FormidableForm<EngineOrder>.Model), order);
            builder.AddComponentParameter(
                2,
                nameof(FormidableForm<EngineOrder>.Options),
                new FormidableOptions { DisclosureOverride = _ => true });
            builder.AddComponentParameter(
                3,
                nameof(FormidableForm<EngineOrder>.FocusFirstErrorOnInvalidSubmit),
                focusFirstErrorOnInvalidSubmit);
            if (onInvalidSubmit is not null)
            {
                builder.AddComponentParameter(
                    4,
                    nameof(FormidableForm<EngineOrder>.OnInvalidSubmit),
                    EventCallback.Factory.Create(this, onInvalidSubmit));
            }
            else if (onInvalidSubmitAsync is not null)
            {
                builder.AddComponentParameter(
                    4,
                    nameof(FormidableForm<EngineOrder>.OnInvalidSubmit),
                    EventCallback.Factory.Create(this, onInvalidSubmitAsync));
            }

            if (focusFallback is not null)
            {
                builder.AddComponentParameter(
                    5, nameof(FormidableForm<EngineOrder>.FocusFallback), focusFallback);
            }

            if (prepareFocus is not null)
            {
                builder.AddComponentParameter(
                    7, nameof(FormidableForm<EngineOrder>.PrepareFocus), prepareFocus);
            }

            builder.AddComponentParameter(
                6,
                nameof(FormidableForm<EngineOrder>.ChildContent),
                (RenderFragment<FormidableFormContext>)(context => inner =>
                {
                    captureContext?.Invoke(context);
                    inner.AddMarkupContent(0, "<button type=\"submit\">Go</button>");
                }));
            builder.CloseComponent();
        });

        return container.FindComponent<FormidableForm<EngineOrder>>();
    }

    private IRenderedComponent<FormidableValidator<EngineOrder>> RenderAttached(
        EngineOrder order,
        Func<FieldIdentifier, ValueTask<bool>>? focusFallback = null,
        Func<FieldIdentifier, ValueTask>? prepareFocus = null,
        Action<FormidableFormContext>? captureContext = null)
    {
        RegisterServices(withFocusService: true);
        var container = Render(builder =>
        {
            builder.OpenComponent<EditForm>(0);
            builder.AddComponentParameter(1, nameof(EditForm.Model), order);
            builder.AddComponentParameter(
                2,
                nameof(EditForm.ChildContent),
                (RenderFragment<EditContext>)(_ => inner =>
                {
                    inner.OpenComponent<FormidableValidator<EngineOrder>>(0);
                    inner.AddComponentParameter(
                        1,
                        nameof(FormidableValidator<EngineOrder>.Options),
                        new FormidableOptions { DisclosureOverride = _ => true });
                    inner.AddComponentParameter(
                        2,
                        nameof(FormidableValidator<EngineOrder>.FocusFirstErrorOnInvalidSubmit),
                        false);
                    if (focusFallback is not null)
                    {
                        inner.AddComponentParameter(
                            3,
                            nameof(FormidableValidator<EngineOrder>.FocusFallback),
                            focusFallback);
                    }

                    if (prepareFocus is not null)
                    {
                        inner.AddComponentParameter(
                            4,
                            nameof(FormidableValidator<EngineOrder>.PrepareFocus),
                            prepareFocus);
                    }

                    // The cascade wraps this component's own content only, so a context is
                    // reachable at all only when there is content to cascade to.
                    inner.AddComponentParameter(
                        5,
                        nameof(FormidableValidator<EngineOrder>.ChildContent),
                        (RenderFragment<FormidableFormContext>)(context => nested =>
                        {
                            captureContext?.Invoke(context);
                            nested.AddMarkupContent(0, "<span></span>");
                        }));
                    inner.CloseComponent();
                }));
            builder.CloseComponent();
        });

        return container.FindComponent<FormidableValidator<EngineOrder>>();
    }

    // The automatic half of the pair below. Both name the same field, and a change to how the
    // shared helper chooses one moves both together.
    [Fact]
    public async Task A_blocked_submits_own_move_lands_on_the_first_error()
    {
        var order = OrderFailingOnTwoFields();
        var cut = RenderForm(order);

        await cut.InvokeAsync(() => cut.Instance.SubmitAsync());

        Assert.Equal(FirstError(order), Assert.Single(_focus.Requests));

        await Services.DisposeAsync();
    }

    // The asked-for half. It is the same method the submit path calls, so this is a claim about
    // reachability rather than about a second implementation — but the target is asserted
    // outright, and against the second error by name, so a helper that started choosing
    // differently would fail here and in the test above at once.
    [Fact]
    public async Task The_move_a_page_asks_for_lands_on_the_same_first_error()
    {
        var order = OrderFailingOnTwoFields();
        var cut = RenderForm(order, focusFirstErrorOnInvalidSubmit: false);

        await cut.InvokeAsync(() => cut.Instance.SubmitAsync());
        Assert.Empty(_focus.Requests);

        var landed = await cut.InvokeAsync(() => cut.Instance.FocusFirstErrorAsync());

        Assert.True(landed);
        Assert.Equal(FirstError(order), Assert.Single(_focus.Requests));
        Assert.NotEqual(SecondError(order), _focus.Requests[0]);

        await Services.DisposeAsync();
    }

    // FocusFirstErrorOnInvalidSubmit decides what a blocked submit does unasked. It is not a
    // master switch over the move itself, and a page that turned it off precisely so it could
    // choose the moment has to be able to choose one.
    [Fact]
    public async Task The_switch_that_stops_the_automatic_move_does_not_gate_the_asked_for_one()
    {
        var order = OrderFailingOnTwoFields();
        var cut = RenderForm(order, focusFirstErrorOnInvalidSubmit: false);

        await cut.InvokeAsync(() => cut.Instance.SubmitAsync());
        Assert.Empty(_focus.Requests);

        Assert.True(await cut.InvokeAsync(() => cut.Instance.FocusFirstErrorAsync()));

        await Services.DisposeAsync();
    }

    // The try -> fall back once -> retry pipeline, reached from the page's own call rather than
    // from a submit: the seams are threaded because there is one path, not because this call
    // rebuilds them.
    [Fact]
    public async Task An_asked_for_moves_miss_falls_back_and_retries_once()
    {
        var order = OrderFailingOnTwoFields();
        _focus.Lands = false;
        var fallbackFields = new List<FieldIdentifier>();
        var cut = RenderForm(
            order,
            focusFirstErrorOnInvalidSubmit: false,
            focusFallback: field =>
            {
                fallbackFields.Add(field);
                return ValueTask.FromResult(true);
            });

        await cut.InvokeAsync(() => cut.Instance.SubmitAsync());
        var landed = await cut.InvokeAsync(() => cut.Instance.FocusFirstErrorAsync());

        Assert.False(landed); // the retry missed too, which is what Lands = false says
        Assert.Equal(FirstError(order), Assert.Single(fallbackFields));
        Assert.Equal(2, _focus.Requests.Count);

        await Services.DisposeAsync();
    }

    // The answer a page needs after closing a dialog: nothing was focused, so if it has somewhere
    // else to send the visitor, now is when it sends them.
    [Fact]
    public async Task A_form_with_no_visible_issue_reports_that_nothing_was_focused()
    {
        var order = new EngineOrder { Description = "ok", Customer = new EngineCustomer() };
        var cut = RenderForm(order);

        var landed = await cut.InvokeAsync(() => cut.Instance.FocusFirstErrorAsync());

        Assert.False(landed);
        Assert.Empty(_focus.Requests);

        await Services.DisposeAsync();
    }

    // The other silent case: a host that registered only AddFormidable has no focus service for
    // the move to go through, and a best-effort move stays best-effort when it is asked for
    // rather than automatic.
    [Fact]
    public async Task A_host_with_no_focus_service_reports_that_nothing_was_focused()
    {
        var order = OrderFailingOnTwoFields();
        var cut = RenderForm(order, focusFirstErrorOnInvalidSubmit: false, withFocusService: false);

        await cut.InvokeAsync(() => cut.Instance.SubmitAsync());

        Assert.False(await cut.InvokeAsync(() => cut.Instance.FocusFirstErrorAsync()));
        Assert.Empty(_focus.Requests);

        await Services.DisposeAsync();
    }

    // Attach mode's page owns its submit handler and so could always hold the move back; what it
    // could not do is make it later with PrepareFocus and FocusFallback threaded in.
    [Fact]
    public async Task An_attached_page_can_ask_for_the_move_after_its_submit()
    {
        var order = OrderFailingOnTwoFields();
        var cut = RenderAttached(order);

        await cut.InvokeAsync(() => cut.Instance.ValidateForSubmitAsync());
        Assert.Empty(_focus.Requests);

        var landed = await cut.InvokeAsync(() => cut.Instance.FocusFirstErrorAsync());

        Assert.True(landed);
        Assert.Equal(FirstError(order), Assert.Single(_focus.Requests));

        await Services.DisposeAsync();
    }

    // The whole sequence in one test: the handler declares it has covered the form, the submit
    // moves nothing, and the page asks for the move when it is ready.
    [Fact]
    public async Task A_handler_that_suppresses_stops_this_submits_move_and_leaves_it_askable()
    {
        var order = OrderFailingOnTwoFields();
        FormidableInvalidSubmitContext? seen = null;
        var cut = RenderForm(order, onInvalidSubmit: context =>
        {
            seen = context;
            context.SuppressFirstErrorFocus();
        });

        await cut.InvokeAsync(() => cut.Instance.SubmitAsync());

        Assert.True(seen!.FirstErrorFocusSuppressed);
        Assert.False(seen.Outcome.CanProceed);
        Assert.Empty(_focus.Requests);

        Assert.True(await cut.InvokeAsync(() => cut.Instance.FocusFirstErrorAsync()));
        Assert.Equal(FirstError(order), Assert.Single(_focus.Requests));

        await Services.DisposeAsync();
    }

    // The other side of the same parameter, and the reason a hard coupling was rejected: a
    // handler that logs, counts or scrolls a banner has not covered the form, and the move it
    // would have got without a handler at all is the move it still gets.
    [Fact]
    public async Task A_handler_that_says_nothing_leaves_the_automatic_move_alone()
    {
        var order = OrderFailingOnTwoFields();
        FormidableInvalidSubmitContext? seen = null;
        var cut = RenderForm(order, onInvalidSubmit: context => seen = context);

        await cut.InvokeAsync(() => cut.Instance.SubmitAsync());

        Assert.False(seen!.FirstErrorFocusSuppressed);
        Assert.Equal(FirstError(order), Assert.Single(_focus.Requests));

        await Services.DisposeAsync();
    }

    // Suppression is a statement about one submit. A handler that opens a dialog for the first
    // block and answers the second one some other way gets exactly that, because the object it
    // says it on is built fresh for each block rather than remembered on the form.
    [Fact]
    public async Task Suppressing_one_submit_does_not_suppress_the_next()
    {
        var order = OrderFailingOnTwoFields();
        var blocks = 0;
        var cut = RenderForm(order, onInvalidSubmit: context =>
        {
            blocks++;
            if (blocks == 1)
            {
                context.SuppressFirstErrorFocus();
            }
        });

        await cut.InvokeAsync(() => cut.Instance.SubmitAsync());
        Assert.Empty(_focus.Requests);

        await cut.InvokeAsync(() => cut.Instance.SubmitAsync());

        Assert.Equal(2, blocks);
        Assert.Equal(FirstError(order), Assert.Single(_focus.Requests));

        await Services.DisposeAsync();
    }

    // The read the form makes is the one a handler's own await has to beat. An async handler
    // suppressing after a yield still lands ahead of it, because the form awaits the callback.
    [Fact]
    public async Task A_handler_that_suppresses_after_awaiting_is_still_in_time()
    {
        var order = OrderFailingOnTwoFields();
        var cut = RenderForm(order, onInvalidSubmitAsync: async context =>
        {
            await Task.Yield();
            context.SuppressFirstErrorFocus();
        });

        await cut.InvokeAsync(() => cut.Instance.SubmitAsync());

        Assert.Empty(_focus.Requests);

        await Services.DisposeAsync();
    }

    // The move a component inside the form asks for, which is the case a page's @ref cannot
    // reach: a shared dialog reads the cascade and has no reference to the root. Asserted as the
    // whole move rather than as a call count — the same field the automatic move lands on, the
    // root's PrepareFocus awaited ahead of the attempt, the root's FocusFallback consulted on the
    // miss, and its one retry — because a context that reached the shared helper directly would
    // still focus the right field while quietly dropping both of the root's own seams.
    [Fact]
    public async Task The_move_the_cascaded_context_asks_for_is_the_forms_own_move()
    {
        var order = OrderFailingOnTwoFields();
        _focus.Lands = false;
        var prepared = new List<FieldIdentifier>();
        var fellBackFor = new List<FieldIdentifier>();
        FormidableFormContext? context = null;
        var cut = RenderForm(
            order,
            focusFirstErrorOnInvalidSubmit: false,
            focusFallback: field =>
            {
                fellBackFor.Add(field);
                return ValueTask.FromResult(true);
            },
            prepareFocus: field =>
            {
                prepared.Add(field);
                return ValueTask.CompletedTask;
            },
            captureContext: captured => context = captured);

        await cut.InvokeAsync(() => cut.Instance.SubmitAsync());
        Assert.Empty(_focus.Requests);

        var landed = await cut.InvokeAsync(() => context!.FocusFirstErrorAsync());

        Assert.False(landed); // the retry missed too, which is what Lands = false says
        Assert.Equal(FirstError(order), Assert.Single(prepared));
        Assert.Equal(FirstError(order), Assert.Single(fellBackFor));
        Assert.Equal(2, _focus.Requests.Count);
        Assert.All(_focus.Requests, field => Assert.Equal(FirstError(order), field));

        await Services.DisposeAsync();
    }

    // The same claim under the root that owns no form element. Attach mode is where a shared
    // dialog is likeliest to sit, since the page already owns its EditForm and its submit.
    [Fact]
    public async Task The_move_the_cascaded_context_asks_for_is_the_attached_components_own_move()
    {
        var order = OrderFailingOnTwoFields();
        _focus.Lands = false;
        var prepared = new List<FieldIdentifier>();
        var fellBackFor = new List<FieldIdentifier>();
        FormidableFormContext? context = null;
        var cut = RenderAttached(
            order,
            focusFallback: field =>
            {
                fellBackFor.Add(field);
                return ValueTask.FromResult(true);
            },
            prepareFocus: field =>
            {
                prepared.Add(field);
                return ValueTask.CompletedTask;
            },
            captureContext: captured => context = captured);

        await cut.InvokeAsync(() => cut.Instance.ValidateForSubmitAsync());
        Assert.Empty(_focus.Requests);

        var landed = await cut.InvokeAsync(() => context!.FocusFirstErrorAsync());

        Assert.False(landed);
        Assert.Equal(FirstError(order), Assert.Single(prepared));
        Assert.Equal(FirstError(order), Assert.Single(fellBackFor));
        Assert.Equal(2, _focus.Requests.Count);

        await Services.DisposeAsync();
    }

    // The public constructor stays supported for tests that cascade a context around an engine or
    // a double, and it carries no root — so there is no move to make. The engine handed over is
    // the form's own, holding real disclosed errors, so the false below is the missing root and
    // not a form with nothing to focus: the last line asks the form itself and it lands.
    [Fact]
    public async Task A_hand_built_context_reports_that_nothing_was_focused()
    {
        var order = OrderFailingOnTwoFields();
        var cut = RenderForm(order, focusFirstErrorOnInvalidSubmit: false);
        await cut.InvokeAsync(() => cut.Instance.SubmitAsync());

        var handBuilt = new FormidableFormContext(cut.Instance.Engine!);

        Assert.False(await cut.InvokeAsync(() => handBuilt.FocusFirstErrorAsync()));
        Assert.Empty(_focus.Requests);

        Assert.True(await cut.InvokeAsync(() => cut.Instance.FocusFirstErrorAsync()));
        Assert.Equal(FirstError(order), Assert.Single(_focus.Requests));

        await Services.DisposeAsync();
    }

    // A page testing its own handler has no rendered form to get a context from, which is the
    // one reason the constructor is public.
    [Fact]
    public void A_context_starts_unsuppressed_and_carries_the_outcome_it_was_built_from()
    {
        var outcome = new SubmitOutcome(false, ValidationReport.Empty, ["Order description"]);

        var context = new FormidableInvalidSubmitContext(outcome);

        Assert.Same(outcome, context.Outcome);
        Assert.False(context.FirstErrorFocusSuppressed);

        context.SuppressFirstErrorFocus();
        context.SuppressFirstErrorFocus();

        Assert.True(context.FirstErrorFocusSuppressed);
    }

    // ------------------------------------------------------------------ the newer move wins

    public sealed class Pair
    {
        public string? First { get; set; } = string.Empty;

        public string? Second { get; set; } = string.Empty;
    }

    public sealed class Solo
    {
        public string? Third { get; set; } = string.Empty;
    }

    private sealed class PairValidator : AbstractValidator<Pair>
    {
        public PairValidator()
        {
            RuleFor(m => m.First).NotEmpty().WithMessage("First required");
            RuleFor(m => m.Second).NotEmpty().WithMessage("Second required");
        }
    }

    private sealed class SoloValidator : AbstractValidator<Solo>
    {
        public SoloValidator() => RuleFor(m => m.Third).NotEmpty().WithMessage("Third required");
    }

    /// <summary>
    /// A consumer's own focus service, written against the interface's one member: a field named
    /// in <see cref="Unreachable"/> misses, and any other takes focus and becomes
    /// <see cref="Focused"/>. The log reads as the visitor would see it, one line per request.
    /// </summary>
    private sealed class ScriptedFocusService : IFormidableFocusService
    {
        public List<string> Log { get; } = [];

        public HashSet<string> Unreachable { get; } = [];

        public string? Focused { get; private set; }

        public ValueTask<bool> FocusAsync(FieldIdentifier field)
        {
            if (Unreachable.Contains(field.FieldName))
            {
                Log.Add($"miss {field.FieldName}");
                return ValueTask.FromResult(false);
            }

            Focused = field.FieldName;
            Log.Add($"focus {field.FieldName}");
            return ValueTask.FromResult(true);
        }
    }

    /// <summary>
    /// A focus service whose every attempt waits for the test to answer it, as the shipped
    /// service's script round trip does: a field named in <see cref="Unreachable"/> misses when
    /// answered, and any other takes focus and becomes <see cref="Focused"/>.
    /// </summary>
    private sealed class HeldFocusService : IFormidableFocusService
    {
        private readonly List<(FieldIdentifier Field, TaskCompletionSource<bool> Answer)> _pending = [];

        public List<string> Log { get; } = [];

        public HashSet<string> Unreachable { get; } = [];

        public string? Focused { get; set; }

        public int Pending => _pending.Count;

        public ValueTask<bool> FocusAsync(FieldIdentifier field)
        {
            var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending.Add((field, answer));
            return new ValueTask<bool>(answer.Task);
        }

        public void Answer(string fieldName)
        {
            var index = _pending.FindIndex(p => p.Field.FieldName == fieldName);
            var (field, answer) = _pending[index];
            _pending.RemoveAt(index);
            if (Unreachable.Contains(field.FieldName))
            {
                Log.Add($"miss {field.FieldName}");
                answer.SetResult(false);
                return;
            }

            Focused = field.FieldName;
            Log.Add($"focus {field.FieldName}");
            answer.SetResult(true);
        }
    }

    /// <summary>A <c>FocusFallback</c> that takes as long as the test says: it waits to be released, then makes the field reachable and answers <see langword="true"/>.</summary>
    private sealed class HeldFallback(ScriptedFocusService focus)
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Calls { get; private set; }

        public async ValueTask<bool> RunAsync(FieldIdentifier field)
        {
            Calls++;
            await _release.Task;
            focus.Unreachable.Remove(field.FieldName);
            return true;
        }

        public void Release() => _release.SetResult();
    }

    private static void AddInput(
        RenderTreeBuilder builder, int region, object receiver, Expression<Func<string?>> accessor, Action<string?> assign)
    {
        builder.OpenRegion(region);
        builder.OpenComponent<FormidableInputText>(0);
        builder.AddComponentParameter(1, "Value", accessor.Compile()());
        builder.AddComponentParameter(2, "ValueChanged", EventCallback.Factory.Create(receiver, assign));
        builder.AddComponentParameter(3, "ValueExpression", accessor);
        builder.CloseComponent();
        builder.CloseRegion();
    }

    /// <summary>A form over <see cref="Pair"/> with a summary, as a region of whatever the test renders, so a second form can sit beside it.</summary>
    private void AddPairForm(
        RenderTreeBuilder builder,
        int region,
        Pair model,
        Func<FieldIdentifier, ValueTask<bool>>? formFallback,
        Func<FieldIdentifier, ValueTask<bool>>? summaryFallback,
        bool focusFirstErrorOnInvalidSubmit)
    {
        builder.OpenRegion(region);
        builder.OpenComponent<FormidableForm<Pair>>(0);
        builder.AddComponentParameter(1, "Model", model);
        builder.AddComponentParameter(
            2, "Validator", (IModelValidator<Pair>)new FluentValidationModelValidator<Pair>(new PairValidator()));
        builder.AddComponentParameter(3, "FocusFirstErrorOnInvalidSubmit", focusFirstErrorOnInvalidSubmit);
        if (formFallback is not null)
        {
            builder.AddComponentParameter(4, "FocusFallback", formFallback);
        }

        builder.AddComponentParameter(5, "ChildContent", (RenderFragment<FormidableFormContext>)(_ => inner =>
        {
            inner.OpenComponent<FormidableSummary>(0);
            if (summaryFallback is not null)
            {
                inner.AddComponentParameter(1, "FocusFallback", summaryFallback);
            }

            inner.CloseComponent();
            AddInput(inner, 2, this, () => model.First, v => model.First = v);
            AddInput(inner, 3, this, () => model.Second, v => model.Second = v);
            inner.AddMarkupContent(4, "<button type=\"submit\">Save</button>");
        }));
        builder.CloseComponent();
        builder.CloseRegion();
    }

    private static Task ClickEntryAsync<TComponent>(IRenderedComponent<TComponent> cut, string message)
        where TComponent : IComponent =>
        cut.InvokeAsync(() => cut.FindAll(".formidable-summary__link")
            .Single(entry => entry.TextContent.Trim() == message)
            .ClickAsync(new()));

    // The visitor's click on a summary entry is a newer move than the blocked submit's, whose
    // fallback is still making First reachable. Once that fallback answers, the submit's retry
    // would take the visitor back to First, away from the field they asked for.
    [Fact]
    public async Task A_summary_click_during_a_fallback_keeps_focus_on_the_clicked_field()
    {
        var focus = new ScriptedFocusService();
        focus.Unreachable.Add(nameof(Pair.First));
        Services.AddSingleton<IFormidableFocusService>(focus);
        Services.AddFormidableBlazor();
        var fallback = new HeldFallback(focus);
        var model = new Pair();
        var cut = Render(builder => AddPairForm(
            builder, 0, model, fallback.RunAsync, summaryFallback: null, focusFirstErrorOnInvalidSubmit: true));

        var submitting = cut.InvokeAsync(() => cut.Find("form").SubmitAsync());
        await cut.InvokeAsync(() => { });
        cut.WaitForState(() => fallback.Calls == 1);

        await ClickEntryAsync(cut, "Second required");
        Assert.Equal(nameof(Pair.Second), focus.Focused);

        fallback.Release();
        await submitting;
        await cut.InvokeAsync(() => { });

        Assert.Equal(nameof(Pair.Second), focus.Focused);
        Assert.Equal(["miss First", "focus Second"], focus.Log);

        await Services.DisposeAsync();
    }

    // Two clicks on one summary. The first names a field its fallback is still making reachable,
    // the second lands, and the first click's retry stands down when its fallback answers.
    [Fact]
    public async Task A_second_summary_click_wins_over_the_first_clicks_retry()
    {
        var focus = new ScriptedFocusService();
        focus.Unreachable.Add(nameof(Pair.First));
        Services.AddSingleton<IFormidableFocusService>(focus);
        Services.AddFormidableBlazor();
        var fallback = new HeldFallback(focus);
        var model = new Pair();
        var cut = Render(builder => AddPairForm(
            builder, 0, model, formFallback: null, fallback.RunAsync, focusFirstErrorOnInvalidSubmit: false));

        await cut.InvokeAsync(() => cut.Find("form").SubmitAsync());
        Assert.Empty(focus.Log);

        var firstClick = ClickEntryAsync(cut, "First required");
        await cut.InvokeAsync(() => { });
        cut.WaitForState(() => fallback.Calls == 1);

        await ClickEntryAsync(cut, "Second required");

        fallback.Release();
        await firstClick;
        await cut.InvokeAsync(() => { });

        Assert.Equal(nameof(Pair.Second), focus.Focused);
        Assert.Equal(["miss First", "focus Second"], focus.Log);

        await Services.DisposeAsync();
    }

    // The newer move need not belong to the same form. Two forms on one page share the page's
    // focus, so a click on the second form's summary wins over the first form's retry.
    [Fact]
    public async Task A_click_on_another_forms_summary_wins_over_this_forms_retry()
    {
        var focus = new ScriptedFocusService();
        focus.Unreachable.Add(nameof(Pair.First));
        Services.AddSingleton<IFormidableFocusService>(focus);
        Services.AddFormidableBlazor();
        var fallback = new HeldFallback(focus);
        var pair = new Pair();
        var solo = new Solo();
        var cut = Render(builder =>
        {
            AddPairForm(builder, 0, pair, fallback.RunAsync, summaryFallback: null, focusFirstErrorOnInvalidSubmit: true);

            builder.OpenRegion(1);
            builder.OpenComponent<FormidableForm<Solo>>(0);
            builder.AddComponentParameter(1, "Model", solo);
            builder.AddComponentParameter(
                2, "Validator", (IModelValidator<Solo>)new FluentValidationModelValidator<Solo>(new SoloValidator()));
            builder.AddComponentParameter(3, "ChildContent", (RenderFragment<FormidableFormContext>)(_ => inner =>
            {
                inner.OpenComponent<FormidableSummary>(0);
                inner.CloseComponent();
                AddInput(inner, 1, this, () => solo.Third, v => solo.Third = v);
            }));
            builder.CloseComponent();
            builder.CloseRegion();
        });

        // The second form shows its error first, so its summary has an entry to click.
        await cut.InvokeAsync(() => cut.FindAll("form")[1].SubmitAsync());
        focus.Log.Clear();

        var submitting = cut.InvokeAsync(() => cut.FindAll("form")[0].SubmitAsync());
        await cut.InvokeAsync(() => { });
        cut.WaitForState(() => fallback.Calls == 1);

        await ClickEntryAsync(cut, "Third required");

        fallback.Release();
        await submitting;
        await cut.InvokeAsync(() => { });

        Assert.Equal(nameof(Solo.Third), focus.Focused);
        Assert.Equal(["miss First", "focus Third"], focus.Log);

        await Services.DisposeAsync();
    }

    // The shipped service tells the script which call is the retry, with a third argument, so the
    // script can leave focus where the visitor put it while the fallback ran. The first attempt
    // carries the two ids alone.
    [Fact]
    public async Task The_retry_call_carries_the_retry_flag()
    {
        _shippedFocusService = true;
        _module.Setup<bool>("focusField", call => call.Arguments.Count == 2).SetResult(false);
        _module.Setup<bool>("focusField", call => call.Arguments.Count == 3).SetResult(true);
        var order = OrderFailingOnTwoFields();
        var fallbackFields = new List<FieldIdentifier>();
        var cut = RenderForm(
            order,
            focusFirstErrorOnInvalidSubmit: false,
            focusFallback: field =>
            {
                fallbackFields.Add(field);
                return ValueTask.FromResult(true);
            });

        await cut.InvokeAsync(() => cut.Instance.SubmitAsync());
        var landed = await cut.InvokeAsync(() => cut.Instance.FocusFirstErrorAsync());

        Assert.True(landed);
        Assert.Equal(FirstError(order), Assert.Single(fallbackFields));
        var calls = _module.Invocations["focusField"].Select(call => call.Arguments).ToList();
        Assert.Equal(2, calls.Count);
        var id = FormidableFieldId.For(FirstError(order));
        var scrollId = FormidableFieldId.MessagesFor(FirstError(order));
        Assert.Equal(new object?[] { id, scrollId }, calls[0]);
        Assert.Equal(new object?[] { id, scrollId, true }, calls[1]);

        await Services.DisposeAsync();
    }

    // A newer move that begins while the older move's first attempt is still out owns the
    // fallback. The older move's attempt then misses, and it stands down without calling the
    // fallback, so a fallback that moves focus itself cannot take the visitor away from the field
    // the newer move landed on. Called on the helper, with every attempt held, so the order is
    // the test's. Mutation that must break it: drop the counter check between the first attempt
    // and the fallback call, and the fallback runs and moves focus to the heading.
    [Fact]
    public async Task A_move_overtaken_during_its_first_attempt_calls_no_fallback()
    {
        var focus = new HeldFocusService();
        focus.Unreachable.Add(nameof(Pair.First));
        var model = new Pair();
        var fallbackCalls = 0;

        var older = FirstErrorFocus.TryFocusAsync(
            focus,
            new FieldIdentifier(model, nameof(Pair.First)),
            prepare: null,
            fallback: field =>
            {
                fallbackCalls++;
                focus.Focused = "heading";
                focus.Unreachable.Remove(field.FieldName);
                return ValueTask.FromResult(true);
            }).AsTask();
        var newer = FirstErrorFocus.TryFocusAsync(
            focus, new FieldIdentifier(model, nameof(Pair.Second)), prepare: null, fallback: null).AsTask();
        Assert.Equal(2, focus.Pending);

        focus.Answer(nameof(Pair.Second));
        Assert.Equal(FirstErrorFocus.FocusMoveResult.Landed, await newer);
        focus.Answer(nameof(Pair.First));

        Assert.Equal(FirstErrorFocus.FocusMoveResult.StoodDown, await older);
        Assert.Equal(0, fallbackCalls);
        Assert.Equal(nameof(Pair.Second), focus.Focused);
    }

    // Two clicks on the summary entry of a field that is out of reach, the second made while the
    // first click's attempt is still out: a double click on a slow circuit. Through the shipped
    // service, both attempts miss, and the fallback and its retry run once, for the newer click.
    // Mutation that must break it: drop the counter check between the first attempt and the
    // fallback call, and the fallback runs twice and the retry is sent twice.
    [Fact]
    public async Task A_double_click_on_an_unreachable_entry_runs_the_fallback_once()
    {
        Services.AddFormidableBlazor();
        var model = new Pair();
        var firstId = FormidableFieldId.For(new FieldIdentifier(model, nameof(Pair.First)));
        var firstAttempt = _module.Setup<bool>(
            "focusField", call => call.Arguments.Count == 2 && Equals(call.Arguments[0], firstId));
        _module.Setup<bool>("focusField", call => call.Arguments.Count == 3).SetResult(true);
        var fallbackCalls = 0;
        var cut = Render(builder => AddPairForm(
            builder,
            0,
            model,
            formFallback: null,
            summaryFallback: _ =>
            {
                fallbackCalls++;
                return ValueTask.FromResult(true);
            },
            focusFirstErrorOnInvalidSubmit: false));

        await cut.InvokeAsync(() => cut.Find("form").SubmitAsync());

        var firstClick = ClickEntryAsync(cut, "First required");
        await cut.InvokeAsync(() => { });
        var secondClick = ClickEntryAsync(cut, "First required");
        await cut.InvokeAsync(() => { });
        cut.WaitForState(() => FocusFieldCalls(arguments: 2) == 2);

        await cut.InvokeAsync(() => firstAttempt.SetResult(false));
        await firstClick;
        await secondClick;
        await cut.InvokeAsync(() => { });

        Assert.Equal(1, fallbackCalls);
        Assert.Equal(1, FocusFieldCalls(arguments: 3));

        await Services.DisposeAsync();
    }

    private int FocusFieldCalls(int arguments) =>
        _module.Invocations.Identifiers.Contains("focusField")
            ? _module.Invocations["focusField"].Count(call => call.Arguments.Count == arguments)
            : 0;

    // Moves are counted per focus service, and each circuit has its own, so a move through one
    // service never stands down a move through another: on Blazor Server, one visitor's click must
    // not cancel another visitor's retry. Called on the helper directly, so nothing but the two
    // services separates the moves.
    [Fact]
    public async Task A_move_through_another_focus_service_leaves_this_ones_retry_alone()
    {
        var mine = new ScriptedFocusService();
        mine.Unreachable.Add(nameof(Pair.First));
        var theirs = new ScriptedFocusService();
        var fallback = new HeldFallback(mine);
        var model = new Pair();

        var move = FirstErrorFocus.TryFocusAsync(
            mine, new FieldIdentifier(model, nameof(Pair.First)), prepare: null, fallback.RunAsync).AsTask();
        Assert.Equal(1, fallback.Calls);

        var other = await FirstErrorFocus.TryFocusAsync(
            theirs, new FieldIdentifier(new Pair(), nameof(Pair.Second)), prepare: null, fallback: null);
        Assert.Equal(FirstErrorFocus.FocusMoveResult.Landed, other);

        fallback.Release();

        Assert.Equal(FirstErrorFocus.FocusMoveResult.Landed, await move);
        Assert.Equal(["miss First", "focus First"], mine.Log);
    }

    // A consumer's own focus service knows nothing of the shipped service's retry flag, and keeps
    // the retry it has always had: a miss, the fallback, and one more FocusAsync that lands.
    [Fact]
    public async Task A_custom_focus_service_keeps_the_single_retry()
    {
        var focus = new ScriptedFocusService();
        focus.Unreachable.Add(nameof(Pair.First));
        Services.AddSingleton<IFormidableFocusService>(focus);
        Services.AddFormidableBlazor();
        var fallbackCalls = 0;
        var model = new Pair();
        var cut = Render(builder => AddPairForm(
            builder,
            0,
            model,
            field =>
            {
                fallbackCalls++;
                focus.Unreachable.Remove(field.FieldName);
                return ValueTask.FromResult(true);
            },
            summaryFallback: null,
            focusFirstErrorOnInvalidSubmit: false));
        var form = cut.FindComponent<FormidableForm<Pair>>().Instance;

        await cut.InvokeAsync(() => cut.Find("form").SubmitAsync());
        var landed = await cut.InvokeAsync(() => form.FocusFirstErrorAsync());

        Assert.True(landed);
        Assert.Equal(1, fallbackCalls);
        Assert.Equal(["miss First", "focus First"], focus.Log);

        await Services.DisposeAsync();
    }
}
