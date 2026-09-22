using Bunit;
using Formidable.Blazor.Tests.Fixtures;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;

namespace Formidable.Blazor.Tests;

/// <summary>
/// The sequence a page takes over a blocked submit with: the handler says it has answered the
/// block itself, and the page asks for the first-error move later, when whatever it put in front
/// of the form is out of the way again. Two claims run through everything here. The move a page
/// asks for is the move a blocked submit makes — same target, same seams, one implementation
/// behind both — and the handler's word covers the submit it was given and no other.
/// </summary>
public class FocusSequenceTests : BunitContext
{
    private readonly RecordingFocusService _focus = new();

    public FocusSequenceTests()
    {
        Services.AddSingleton<FluentValidation.IValidator<EngineOrder>, EngineOrderValidator>();
        var module = JSInterop.SetupModule("./_content/Formidable.Blazor/formidable.js");
        module.Setup<IReadOnlyList<string>>("orderFields", _ => true).SetResult([]);
        module.SetupVoid("observeLayout", _ => true).SetVoidResult();
        module.SetupVoid("disconnectLayoutObserver", _ => true).SetVoidResult();
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
            // instance is the one both roots resolve.
            Services.AddSingleton<IFormidableFocusService>(_focus);
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
        Func<FormidableInvalidSubmitContext, Task>? onInvalidSubmitAsync = null)
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

            builder.AddComponentParameter(
                6,
                nameof(FormidableForm<EngineOrder>.ChildContent),
                (RenderFragment<FormidableFormContext>)(_ => inner =>
                    inner.AddMarkupContent(0, "<button type=\"submit\">Go</button>")));
            builder.CloseComponent();
        });

        return container.FindComponent<FormidableForm<EngineOrder>>();
    }

    private IRenderedComponent<FormidableValidator<EngineOrder>> RenderAttached(EngineOrder order)
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
}
