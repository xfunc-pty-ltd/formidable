using Bunit;
using Formidable.Blazor.Tests.Fixtures;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;

namespace Formidable.Blazor.Tests;

/// <summary>
/// The pre-focus hook, on every path that moves focus: a blocked submit under either root, the
/// focus a server apply makes, a summary entry's click, and the move a page asks for by calling
/// <c>FocusFirstErrorAsync</c> on either root. What each test here has to
/// discriminate is not that the callback ran but that it had FINISHED running when focus was
/// asked for — a hook invoked and not awaited, or invoked after the attempt, is the shape a page
/// dismissing a dialog cannot survive, and neither shows up in a call count.
/// </summary>
public class PrepareFocusTests : BunitContext
{
    private readonly RecordingFocusService _focus = new();

    public PrepareFocusTests()
    {
        Services.AddSingleton<FluentValidation.IValidator<EngineOrder>, EngineOrderValidator>();
        var module = JSInterop.SetupModule("./_content/Formidable.Blazor/formidable.js");
        module.Setup<IReadOnlyList<string>>("orderFields", _ => true).SetResult([]);
        module.SetupVoid("observeLayout", _ => true).SetVoidResult();
        module.SetupVoid("disconnectLayoutObserver", _ => true).SetVoidResult();
    }

    /// <summary>
    /// The container each render runs against, chosen rather than fixed because one test needs a
    /// host that resolves no focus service at all. Registration is deferred to the render helpers
    /// because bUnit builds the provider on first resolution, which a render is and a constructor
    /// is not.
    /// </summary>
    private void RegisterServices(bool withFocusService)
    {
        if (withFocusService)
        {
            // Ahead of AddFormidableBlazor, whose focus registration is a TryAdd, so this instance
            // is the one both roots and the summary resolve.
            Services.AddSingleton<IFormidableFocusService>(_focus);
            Services.AddFormidableBlazor();
            return;
        }

        // The core registrations alone, which is what leaves GetService<IFormidableFocusService>()
        // answering null: AddFormidableBlazor is where the focus service comes from.
        Services.AddFormidable();
    }

    /// <summary>
    /// The probe every ordering assertion below is built from: a hook that yields before it
    /// finishes, and a record of what it had finished by the time each focus request arrived. The
    /// yield is the discrimination — without it, a hook called and discarded would still have run
    /// to completion before the synchronous focus call, and the test would pass on an
    /// implementation that never awaited anything.
    /// </summary>
    private sealed class PreparationProbe
    {
        private bool _finished;

        public List<bool> FinishedAtFocus { get; } = [];

        public List<FieldIdentifier> Fields { get; } = [];

        public async ValueTask HookAsync(FieldIdentifier field)
        {
            Fields.Add(field);
            await Task.Yield();
            _finished = true;
        }

        public void Watch(FieldIdentifier field) => FinishedAtFocus.Add(_finished);
    }

    private PreparationProbe ArmProbe()
    {
        var probe = new PreparationProbe();
        _focus.OnFocus = probe.Watch;
        return probe;
    }

    private IRenderedComponent<FormidableForm<EngineOrder>> RenderForm(
        EngineOrder order,
        Func<FieldIdentifier, ValueTask>? prepareFocus = null,
        Func<FieldIdentifier, ValueTask<bool>>? focusFallback = null,
        bool withSummary = false,
        bool focusFirstErrorOnInvalidSubmit = true,
        bool withFocusService = true)
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
            if (prepareFocus is not null)
            {
                builder.AddComponentParameter(
                    3, nameof(FormidableForm<EngineOrder>.PrepareFocus), prepareFocus);
            }

            if (focusFallback is not null)
            {
                builder.AddComponentParameter(
                    4, nameof(FormidableForm<EngineOrder>.FocusFallback), focusFallback);
            }

            builder.AddComponentParameter(
                5,
                nameof(FormidableForm<EngineOrder>.FocusFirstErrorOnInvalidSubmit),
                focusFirstErrorOnInvalidSubmit);

            builder.AddComponentParameter(
                6,
                nameof(FormidableForm<EngineOrder>.ChildContent),
                (RenderFragment<FormidableFormContext>)(_ => inner =>
                {
                    if (withSummary)
                    {
                        inner.OpenComponent<FormidableSummary>(0);
                        if (prepareFocus is not null)
                        {
                            inner.AddComponentParameter(
                                1, nameof(FormidableSummary.PrepareFocus), prepareFocus);
                        }

                        inner.CloseComponent();
                    }

                    inner.AddMarkupContent(2, "<button type=\"submit\">Go</button>");
                }));
            builder.CloseComponent();
        });

        return container.FindComponent<FormidableForm<EngineOrder>>();
    }

    private IRenderedComponent<FormidableValidator<EngineOrder>> RenderAttached(
        EngineOrder order,
        Func<FieldIdentifier, ValueTask>? prepareFocus = null,
        bool focusFirstErrorOnInvalidSubmit = true)
    {
        RegisterServices(withFocusService: true);
        var container = Render(builder =>
        {
            builder.OpenComponent<EditForm>(0);
            builder.AddComponentParameter(1, nameof(EditForm.Model), order);
            builder.AddComponentParameter(2, nameof(EditForm.ChildContent), (RenderFragment<EditContext>)(_ => inner =>
            {
                inner.OpenComponent<FormidableValidator<EngineOrder>>(0);
                inner.AddComponentParameter(
                    1,
                    nameof(FormidableValidator<EngineOrder>.Options),
                    new FormidableOptions { DisclosureOverride = _ => true });
                if (prepareFocus is not null)
                {
                    inner.AddComponentParameter(
                        2, nameof(FormidableValidator<EngineOrder>.PrepareFocus), prepareFocus);
                }

                inner.AddComponentParameter(
                    3,
                    nameof(FormidableValidator<EngineOrder>.FocusFirstErrorOnInvalidSubmit),
                    focusFirstErrorOnInvalidSubmit);
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        });

        return container.FindComponent<FormidableValidator<EngineOrder>>();
    }

    // Description is filled so Customer is the only failure, which fixes the field the move lands
    // on and lets the hook's own argument be asserted rather than merely counted.
    private static EngineOrder OrderFailingOnCustomerAlone() => new() { Description = "ok" };

    [Fact]
    public async Task A_blocked_submits_focus_waits_for_the_hook_to_finish()
    {
        var order = OrderFailingOnCustomerAlone();
        var probe = ArmProbe();
        var cut = RenderForm(order, prepareFocus: probe.HookAsync);

        await cut.InvokeAsync(() => cut.Instance.SubmitAsync());

        var customer = new FieldIdentifier(order, nameof(EngineOrder.Customer));
        Assert.Equal(customer, Assert.Single(probe.Fields));
        Assert.Equal(customer, Assert.Single(_focus.Requests));
        Assert.True(Assert.Single(probe.FinishedAtFocus));

        await Services.DisposeAsync();
    }

    [Fact]
    public async Task The_focus_a_server_apply_makes_waits_for_the_hook_to_finish()
    {
        var order = new EngineOrder { Description = "ok", Customer = new EngineCustomer() };
        var probe = ArmProbe();
        var cut = RenderForm(order, prepareFocus: probe.HookAsync);

        await cut.InvokeAsync(() => cut.Instance.ApplyServerIssues(
            [new ValidationIssue(nameof(EngineOrder.Description), "server says no")]));

        // The apply's own focus is fire-and-forget from a synchronous method, so the assertion
        // polls rather than reading straight after the call.
        cut.WaitForAssertion(() => Assert.True(Assert.Single(probe.FinishedAtFocus)));
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));
        Assert.Equal(description, Assert.Single(probe.Fields));
        Assert.Equal(description, Assert.Single(_focus.Requests));

        await Services.DisposeAsync();
    }

    // The dialog sequence's far end. The page has closed whatever covered the form and asks for
    // the move itself, and the hook is still what runs first — the same threading, because it is
    // the same call the submit path makes.
    [Fact]
    public async Task A_page_asked_for_focus_waits_for_the_hook_to_finish()
    {
        var order = OrderFailingOnCustomerAlone();
        var probe = ArmProbe();
        var cut = RenderForm(
            order, prepareFocus: probe.HookAsync, focusFirstErrorOnInvalidSubmit: false);

        await cut.InvokeAsync(() => cut.Instance.SubmitAsync());
        Assert.Empty(probe.Fields);

        await cut.InvokeAsync(() => cut.Instance.FocusFirstErrorAsync());

        var customer = new FieldIdentifier(order, nameof(EngineOrder.Customer));
        Assert.Equal(customer, Assert.Single(probe.Fields));
        Assert.Equal(customer, Assert.Single(_focus.Requests));
        Assert.True(Assert.Single(probe.FinishedAtFocus));

        await Services.DisposeAsync();
    }

    [Fact]
    public async Task An_attached_page_asked_for_focus_waits_for_the_hook_to_finish()
    {
        var order = OrderFailingOnCustomerAlone();
        var probe = ArmProbe();
        var cut = RenderAttached(
            order, prepareFocus: probe.HookAsync, focusFirstErrorOnInvalidSubmit: false);

        await cut.InvokeAsync(() => cut.Instance.FocusFirstErrorAsync());

        // Nothing has been submitted, so the engine has no disclosed issue and the move finds
        // nothing to land on: the hook must not run for a move that never happens.
        Assert.Empty(probe.Fields);
        Assert.Empty(_focus.Requests);

        await cut.InvokeAsync(() => cut.Instance.ValidateForSubmitAsync());
        Assert.Empty(probe.Fields);

        await cut.InvokeAsync(() => cut.Instance.FocusFirstErrorAsync());

        var customer = new FieldIdentifier(order, nameof(EngineOrder.Customer));
        Assert.Equal(customer, Assert.Single(probe.Fields));
        Assert.Equal(customer, Assert.Single(_focus.Requests));
        Assert.True(Assert.Single(probe.FinishedAtFocus));

        await Services.DisposeAsync();
    }

    [Fact]
    public async Task An_attached_blocked_submits_focus_waits_for_the_hook_to_finish()
    {
        var order = OrderFailingOnCustomerAlone();
        var probe = ArmProbe();
        var cut = RenderAttached(order, prepareFocus: probe.HookAsync);

        await cut.InvokeAsync(() => cut.Instance.ValidateForSubmitAsync());

        var customer = new FieldIdentifier(order, nameof(EngineOrder.Customer));
        Assert.Equal(customer, Assert.Single(probe.Fields));
        Assert.Equal(customer, Assert.Single(_focus.Requests));
        Assert.True(Assert.Single(probe.FinishedAtFocus));

        await Services.DisposeAsync();
    }

    [Fact]
    public async Task A_summary_clicks_focus_waits_for_the_hook_to_finish()
    {
        var order = OrderFailingOnCustomerAlone();
        var probe = ArmProbe();
        var cut = RenderForm(
            order,
            prepareFocus: probe.HookAsync,
            withSummary: true,
            focusFirstErrorOnInvalidSubmit: false);

        await cut.InvokeAsync(() => cut.Instance.SubmitAsync());
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".formidable-summary__link")));
        Assert.Empty(_focus.Requests);

        await cut.Find(".formidable-summary__link").ClickAsync(new());

        var customer = new FieldIdentifier(order, nameof(EngineOrder.Customer));
        Assert.Equal(customer, Assert.Single(probe.Fields));
        Assert.Equal(customer, Assert.Single(_focus.Requests));
        Assert.True(Assert.Single(probe.FinishedAtFocus));

        await Services.DisposeAsync();
    }

    // The hook belongs to the move, not to each attempt within it: a page that dismissed a dialog
    // to make the field reachable must not be asked to dismiss it again for the retry.
    [Fact]
    public async Task A_recovered_miss_retries_the_focus_without_running_the_hook_again()
    {
        var order = OrderFailingOnCustomerAlone();
        var probe = ArmProbe();
        _focus.Lands = false;
        var cut = RenderForm(
            order,
            prepareFocus: probe.HookAsync,
            focusFallback: _ => ValueTask.FromResult(true));

        await cut.InvokeAsync(() => cut.Instance.SubmitAsync());

        Assert.Equal(2, _focus.Requests.Count);
        Assert.Single(probe.Fields);

        await Services.DisposeAsync();
    }

    // The null case, which has to stay indistinguishable from the pipeline before the seam existed.
    [Fact]
    public async Task A_blocked_submit_with_no_hook_wired_focuses_as_it_would_without_the_seam()
    {
        var order = OrderFailingOnCustomerAlone();
        var cut = RenderForm(order);

        var exception = await Record.ExceptionAsync(() => cut.InvokeAsync(() => cut.Instance.SubmitAsync()));

        Assert.Null(exception);
        Assert.Equal(
            new FieldIdentifier(order, nameof(EngineOrder.Customer)),
            Assert.Single(_focus.Requests));

        await Services.DisposeAsync();
    }

    [Fact]
    public async Task A_summary_click_with_no_hook_wired_focuses_as_it_would_without_the_seam()
    {
        var order = OrderFailingOnCustomerAlone();
        var cut = RenderForm(order, withSummary: true, focusFirstErrorOnInvalidSubmit: false);

        await cut.InvokeAsync(() => cut.Instance.SubmitAsync());
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".formidable-summary__link")));

        var exception = await Record.ExceptionAsync(
            () => cut.Find(".formidable-summary__link").ClickAsync(new()));

        Assert.Null(exception);
        Assert.Equal(
            new FieldIdentifier(order, nameof(EngineOrder.Customer)),
            Assert.Single(_focus.Requests));

        await Services.DisposeAsync();
    }

    // Exception handling is the path's, not the hook's: whatever a throwing FocusFallback does on
    // a given path, a throwing PrepareFocus does too, so a page that wires both parameters has one
    // rule to learn rather than a second. On the submit paths that means surfacing.
    [Fact]
    public async Task A_throwing_hook_surfaces_from_the_submit_that_ran_it()
    {
        var order = OrderFailingOnCustomerAlone();
        var cut = RenderForm(order, prepareFocus: _ => throw new InvalidOperationException("dialog"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => cut.InvokeAsync(() => cut.Instance.SubmitAsync()));

        Assert.Equal("dialog", exception.Message);
        Assert.Empty(_focus.Requests);

        await Services.DisposeAsync();
    }

    [Fact]
    public async Task A_throwing_hook_surfaces_from_the_attached_submit_that_ran_it()
    {
        var order = OrderFailingOnCustomerAlone();
        var cut = RenderAttached(order, prepareFocus: _ => throw new InvalidOperationException("dialog"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => cut.InvokeAsync(() => cut.Instance.ValidateForSubmitAsync()));

        Assert.Equal("dialog", exception.Message);
        Assert.Empty(_focus.Requests);

        await Services.DisposeAsync();
    }

    // The other side of that rule, and the documented asymmetry it inherits: ApplyServerIssues is
    // synchronous by contract, so its focus move is fire-and-forget and nothing thrown inside it
    // reaches the caller — a throwing FocusFallback has always behaved this way on this path, and
    // making the new hook behave differently would be a second rule to learn rather than none.
    [Fact]
    public async Task A_throwing_hook_reaches_no_caller_of_a_server_apply()
    {
        var order = new EngineOrder { Description = "ok", Customer = new EngineCustomer() };
        var cut = RenderForm(order, prepareFocus: _ => throw new InvalidOperationException("dialog"));

        var exception = await Record.ExceptionAsync(() => cut.InvokeAsync(() => cut.Instance.ApplyServerIssues(
            [new ValidationIssue(nameof(EngineOrder.Description), "server says no")])));

        Assert.Null(exception);
        Assert.Empty(_focus.Requests);
        Assert.True(cut.Instance.Engine!.HasSubmitted);

        await Services.DisposeAsync();
    }

    [Fact]
    public async Task A_throwing_hook_surfaces_from_the_summary_click_that_ran_it()
    {
        var order = OrderFailingOnCustomerAlone();
        // The form's own auto-focus is off, so the only hook that runs here is the summary's.
        var cut = RenderForm(
            order,
            prepareFocus: _ => throw new InvalidOperationException("dialog"),
            withSummary: true,
            focusFirstErrorOnInvalidSubmit: false);

        await cut.InvokeAsync(() => cut.Instance.SubmitAsync());
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".formidable-summary__link")));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => cut.Find(".formidable-summary__link").ClickAsync(new()));

        Assert.Equal("dialog", exception.Message);
        Assert.Empty(_focus.Requests);

        await Services.DisposeAsync();
    }

    // Nothing prepares for a move that is not about to happen: closing a dialog is too visible a
    // side effect to pay for a focus the form has been told not to make.
    [Fact]
    public async Task A_form_that_focuses_nothing_never_runs_the_hook()
    {
        var order = OrderFailingOnCustomerAlone();
        var probe = ArmProbe();
        var cut = RenderForm(
            order, prepareFocus: probe.HookAsync, focusFirstErrorOnInvalidSubmit: false);

        await cut.InvokeAsync(() => cut.Instance.SubmitAsync());

        Assert.Empty(probe.Fields);
        Assert.Empty(_focus.Requests);

        await Services.DisposeAsync();
    }

    // The same rule at the other place the move is abandoned. A host that never registered
    // IFormidableFocusService gets silence rather than a resolution failure, and silence has to
    // include the hook: a page that dismissed a dialog here would have dismissed it for a focus
    // move that was never going to be attempted. This is why the hook sits BELOW that resolution
    // rather than at the top of the move.
    [Fact]
    public async Task A_host_with_no_focus_service_never_runs_the_hook()
    {
        var order = OrderFailingOnCustomerAlone();
        var probe = new PreparationProbe();
        var cut = RenderForm(order, prepareFocus: probe.HookAsync, withFocusService: false);

        Assert.Null(Services.GetService<IFormidableFocusService>());
        await cut.InvokeAsync(() => cut.Instance.SubmitAsync());

        Assert.Empty(probe.Fields);

        await Services.DisposeAsync();
    }
}
