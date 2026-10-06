using Bunit;
using Formidable.Blazor.Tests.Fixtures;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;

namespace Formidable.Blazor.Tests;

/// <summary>
/// What each root answers once it is disposed. <see cref="FormidableForm{TModel}.Engine"/> keeps
/// the engine it last built, disposed; <see cref="FormidableValidator{TModel}.Engine"/> reads
/// <see langword="null"/>. On either root, a call that needs the engine does nothing and throws
/// nothing once the root is disposed, and a submit still awaiting its answer fires no callback.
/// </summary>
public class RootDisposalTests : BunitContext
{
    public RootDisposalTests()
    {
        Services.AddFormidableBlazor();
        Services.AddSingleton<FluentValidation.IValidator<EngineOrder>, EngineOrderValidator>();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    // A pin of the documented answer: the form keeps the reference to the engine it disposed.
    // Mutation: clear the field in FormidableForm.Dispose, and Engine reads null.
    [Fact]
    public async Task FormidableForm_Engine_after_Dispose_is_the_disposed_engine()
    {
        var order = new EngineOrder();
        var form = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<EngineOrder>>(0);
            builder.AddComponentParameter(1, nameof(FormidableForm<EngineOrder>.Model), order);
            builder.AddComponentParameter(
                2,
                nameof(FormidableForm<EngineOrder>.ChildContent),
                (RenderFragment<FormidableFormContext>)(_ => _ => { }));
            builder.CloseComponent();
        }).FindComponent<FormidableForm<EngineOrder>>().Instance;
        var engine = form.Engine;
        Assert.NotNull(engine);
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));

        await DisposeComponentsAsync();

        Assert.Same(engine, form.Engine);

        // Disposed: the engine no longer hears its edit context, so a change marks nothing.
        engine.EditContext.NotifyFieldChanged(description);
        Assert.False(engine.GetFieldState(description).IsTouched);

        await Services.DisposeAsync();
    }

    // A pin of the documented answer. Mutation: keep the field in TearDownEngine, and Engine
    // reads the disposed engine.
    [Fact]
    public async Task FormidableValidator_Engine_after_Dispose_is_null()
    {
        var validator = RenderAttached(new EngineOrder());
        Assert.NotNull(validator.Engine);

        await DisposeComponentsAsync();

        Assert.Null(validator.Engine);

        await Services.DisposeAsync();
    }

    // The late-call contract: once the component is disposed, every member that needs the
    // engine does nothing and throws nothing, so a server reply that arrives after the component
    // has gone needs no guard on the page, and a submit returns the blocked, empty outcome. Each
    // call is asserted against that contract rather than against a message saying the component
    // was disposed: it completes, and nothing is thrown.
    // Mutation: drop the disposed return from any one member, and that call throws the message
    // for a call made before the first render.
    [Fact]
    public async Task A_FormidableValidator_call_after_Dispose_does_nothing()
    {
        var validator = RenderAttached(new EngineOrder());

        await DisposeComponentsAsync();

        Assert.Same(RootSubmit.Superseded, await validator.ValidateForSubmitAsync());
        Assert.False(await validator.FocusFirstErrorAsync());
        Assert.Null(Record.Exception(() => validator.ApplyServerIssues(
            [new ValidationIssue(nameof(EngineOrder.Description), "late")])));
        Assert.Null(Record.Exception(() => validator.ApplyServerIssues(new FormidableValidationProblem())));
        Assert.Null(await Record.ExceptionAsync(() => validator.DiscloseLoadedValuesAsync()));
        Assert.Null(validator.Engine);

        await Services.DisposeAsync();
    }

    // A pin: before the first build the message is still the one naming the call's timing.
    // Mutation: drop a call before the first build as if it were late, and the submit returns
    // the blocked outcome instead of throwing.
    [Fact]
    public async Task A_FormidableValidator_call_before_its_first_build_still_says_no_engine_yet()
    {
        var validator = new FormidableValidator<EngineOrder>();

        var submit = await Assert.ThrowsAsync<InvalidOperationException>(() => validator.ValidateForSubmitAsync());

        Assert.Contains("has no engine yet", submit.Message);
        Assert.Contains("@ref", submit.Message);
        Assert.DoesNotContain("disposed", submit.Message);
    }

    // Mirrors the validator's test of the same name, under the same late-call contract and
    // asserted against it the same way. The form keeps the engine it disposed, so this also
    // reads that nothing reaches it: a late reply writes nothing into its store, a late reset
    // builds no engine, and a late load marks nothing. The form runs a submit before it goes,
    // which is what makes a late submit or load that reaches the disposed engine throw
    // ObjectDisposedException.
    // Mutation: drop the disposed return from any one member, and that member's line fails: the
    // late submit throws ObjectDisposedException, the reset builds a new engine, the focus move
    // asks the focus service for a field, a reply lands in the disposed engine's store, and the
    // load throws ObjectDisposedException.
    [Fact]
    public async Task A_FormidableForm_call_after_Dispose_does_nothing()
    {
        var focus = new RecordingFocusService();
        Services.AddSingleton<IFormidableFocusService>(focus);
        var order = new EngineOrder { Description = "Loaded" };
        FormidableFormContext? context = null;
        var form = RenderForm(order, cascaded => context = cascaded);
        var engine = form.Engine!;
        await Renderer.Dispatcher.InvokeAsync(() => form.SubmitAsync());
        focus.Requests.Clear(); // that blocked submit's own focus move

        await DisposeComponentsAsync();

        // Each late call arrives on the renderer's dispatcher, as a reply's continuation does.
        var dispatcher = Renderer.Dispatcher;
        Assert.Same(RootSubmit.Superseded, await dispatcher.InvokeAsync(() => form.SubmitAsync()));
        await dispatcher.InvokeAsync(() => form.ResetAsync());
        Assert.Same(engine, form.Engine);
        Assert.False(await dispatcher.InvokeAsync(() => form.FocusFirstErrorAsync()));
        await dispatcher.InvokeAsync(() => form.ApplyServerIssues(
            [new ValidationIssue(nameof(EngineOrder.Description), "late")]));
        await dispatcher.InvokeAsync(() => form.ApplyServerIssues(
            new FormidableValidationProblem { Errors = { [nameof(EngineOrder.Customer)] = ["late too"] } }));
        Assert.DoesNotContain(
            engine.EditContext.GetValidationMessages(),
            message => message.StartsWith("late", StringComparison.Ordinal));
        await dispatcher.InvokeAsync(() => form.DiscloseLoadedValuesAsync());
        Assert.False(engine.GetFieldState(new FieldIdentifier(order, nameof(EngineOrder.Description))).IsTouched);

        // The cascaded context's focus move is the form's own, so a late call through it too.
        Assert.False(await dispatcher.InvokeAsync(() => context!.FocusFirstErrorAsync()));
        Assert.Empty(focus.Requests);

        await Services.DisposeAsync();
    }

    // A pin: before the first build the message is still the one naming the call's timing.
    // Mutation: drop a call before the first build as if it were late, and the submit returns
    // the blocked outcome instead of throwing.
    [Fact]
    public async Task A_FormidableForm_call_before_its_first_build_still_says_no_engine_yet()
    {
        var form = new FormidableForm<EngineOrder>();

        var submit = await Assert.ThrowsAsync<InvalidOperationException>(() => form.SubmitAsync());

        Assert.Contains("has no engine yet", submit.Message);
        Assert.Contains("@ref", submit.Message);
        Assert.DoesNotContain("disposed", submit.Message);
    }

    // The rule ignores the token the disposal cancels and answers after the form has gone. A
    // passing answer would run OnValidSubmit for that form and a failing one OnInvalidSubmit.
    // Mutation: hand RootSubmit the engine whatever the form's state, and OnInvalidSubmit fires
    // for the engine's own blocked outcome, which comes back in place of the empty one.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_submit_landing_after_the_form_is_disposed_fires_no_callback(bool shouldPass)
    {
        var validator = new CancellationIgnoringValidator { ShouldPass = shouldPass };
        var validSeen = false;
        var invalidSeen = false;
        var form = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<EngineOrder>>(0);
            builder.AddComponentParameter(1, nameof(FormidableForm<EngineOrder>.Model), new EngineOrder());
            builder.AddComponentParameter(
                2,
                nameof(FormidableForm<EngineOrder>.Validator),
                new FluentValidationModelValidator<EngineOrder>(validator));
            builder.AddComponentParameter(
                3,
                nameof(FormidableForm<EngineOrder>.OnValidSubmit),
                EventCallback.Factory.Create<SubmitOutcome>(this, () => validSeen = true));
            builder.AddComponentParameter(
                4,
                nameof(FormidableForm<EngineOrder>.OnInvalidSubmit),
                EventCallback.Factory.Create<FormidableInvalidSubmitContext>(this, () => invalidSeen = true));
            builder.AddComponentParameter(
                5,
                nameof(FormidableForm<EngineOrder>.ChildContent),
                (RenderFragment<FormidableFormContext>)(_ => _ => { }));
            builder.CloseComponent();
        }).FindComponent<FormidableForm<EngineOrder>>();

        SubmitOutcome? outcome = null;
        var submitting = form.InvokeAsync(async () => outcome = await form.Instance.SubmitAsync());
        form.WaitForAssertion(() => Assert.True(validator.Started >= 1));

        await DisposeComponentsAsync();
        validator.Gate.SetResult(); // answers despite the cancelled token
        await submitting;

        Assert.False(validSeen);
        Assert.False(invalidSeen);
        Assert.Same(RootSubmit.Superseded, outcome);
        Assert.False(outcome!.CanProceed);
        Assert.Empty(outcome.Report.Issues);
        Assert.Empty(outcome.VisibleErrorSummary);

        await Services.DisposeAsync();
    }

    // A handler that removes the form from the page disposes it while SubmitAsync awaits the
    // handler. What follows the handler then meets a disposed form, and nothing of it may throw
    // out of the rendered form's own submit handler: the blocked row's focus move does nothing,
    // as every call on a disposed form does, and both rows' render request does nothing.
    // Mutation: let FocusFirstErrorAsync act on a disposed form, and the blocked row's focus
    // service is asked for a field after the form has gone. The passing row is a pin of the
    // render request: no code here decides it.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_submit_whose_callback_disposes_the_form_completes_without_throwing(bool passes)
    {
        var focus = new RecordingFocusService();
        Services.AddSingleton<IFormidableFocusService>(focus);
        var order = passes ? new EngineOrder { Description = "Ok", Customer = new EngineCustomer() } : new EngineOrder();
        var host = Render<DismissingHost>(parameters => parameters.Add(p => p.Model, order));
        var form = host.FindComponent<FormidableForm<EngineOrder>>().Instance;

        SubmitOutcome? outcome = null;
        var thrown = await Record.ExceptionAsync(
            () => host.InvokeAsync(async () => outcome = await form.SubmitAsync()));

        Assert.Null(thrown);
        Assert.True(host.Instance.Dismissed);
        Assert.Empty(host.FindComponents<FormidableForm<EngineOrder>>());
        Assert.NotNull(outcome);
        Assert.Equal(passes, outcome!.CanProceed);
        Assert.Empty(focus.Requests);

        await Services.DisposeAsync();
    }

    /// <summary>A page holding a form whose <c>OnValidSubmit</c> and <c>OnInvalidSubmit</c> each remove it from the page: the host re-renders without the form, which disposes it.</summary>
    private sealed class DismissingHost : ComponentBase
    {
        [Parameter]
        public EngineOrder Model { get; set; } = default!;

        public bool Dismissed { get; private set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            if (Dismissed)
            {
                return;
            }

            builder.OpenComponent<FormidableForm<EngineOrder>>(0);
            builder.AddComponentParameter(1, nameof(FormidableForm<EngineOrder>.Model), Model);
            builder.AddComponentParameter(
                2,
                nameof(FormidableForm<EngineOrder>.OnInvalidSubmit),
                EventCallback.Factory.Create<FormidableInvalidSubmitContext>(this, () => Dismissed = true));
            builder.AddComponentParameter(
                3,
                nameof(FormidableForm<EngineOrder>.OnValidSubmit),
                EventCallback.Factory.Create<SubmitOutcome>(this, () => Dismissed = true));
            builder.AddComponentParameter(
                4,
                nameof(FormidableForm<EngineOrder>.ChildContent),
                (RenderFragment<FormidableFormContext>)(_ => _ => { }));
            builder.CloseComponent();
        }
    }

    private FormidableForm<EngineOrder> RenderForm(EngineOrder order, Action<FormidableFormContext>? seeContext = null) =>
        Render(builder =>
        {
            builder.OpenComponent<FormidableForm<EngineOrder>>(0);
            builder.AddComponentParameter(1, nameof(FormidableForm<EngineOrder>.Model), order);
            builder.AddComponentParameter(
                2,
                nameof(FormidableForm<EngineOrder>.ChildContent),
                (RenderFragment<FormidableFormContext>)(context =>
                {
                    seeContext?.Invoke(context);
                    return _ => { };
                }));
            builder.CloseComponent();
        }).FindComponent<FormidableForm<EngineOrder>>().Instance;

    private FormidableValidator<EngineOrder> RenderAttached(EngineOrder order) =>
        Render(builder =>
        {
            builder.OpenComponent<EditForm>(0);
            builder.AddComponentParameter(1, nameof(EditForm.Model), order);
            builder.AddComponentParameter(2, nameof(EditForm.ChildContent), (RenderFragment<EditContext>)(_ => inner =>
            {
                inner.OpenComponent<FormidableValidator<EngineOrder>>(0);
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        }).FindComponent<FormidableValidator<EngineOrder>>().Instance;
}
