using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace Formidable.Blazor;

/// <summary>
/// Attaches the Formidable engine to an existing <see cref="EditForm"/>'s
/// <see cref="EditContext"/> — the incremental-adoption alternative to
/// <c>FormidableForm</c>. Cascades a <see cref="FormidableFormContext"/> to any child content.
/// </summary>
/// <typeparam name="TModel">The form model type (the EditContext's model).</typeparam>
public sealed class FormidableValidator<TModel> : ComponentBase, IDisposable
    where TModel : class
{
    private FormidableEngine<TModel>? _engine;
    private FormidableOptions? _boundOptions;
    private FormidableFormContext? _context;

    // Latches the Registry.Version last actually reconciled, mirroring FormidableForm's own
    // _renderedFieldSetVersion — reset to -1 whenever the engine is (re)built, so the first
    // signal after a fresh registry always finds something to do. Read and written only from the
    // renderer's synchronization context: the automatic path reaches it via a posted
    // continuation captured on that same context, and NotifyFieldSetChanged is documented to be
    // called from one of a consumer's own event handlers, never from a background thread.
    private int _lastReconciledFieldSetVersion = -1;

    // How many times ReconcileIfChanged has actually run the reconcile (as opposed to skipping
    // on the version gate) since the engine was built. Internal rather than removed once the
    // signal exists, because it is the only way a test can tell a batch of N registry changes
    // that coalesced into one reconcile apart from one that ran the reconcile N times and merely
    // produced the same final state either way.
    internal int ReconcileCount { get; private set; }

    // The displaced-click guard's half. The module is this component's own, on the same terms
    // FormidableForm holds one: whoever holds a FormidableJsModule disposes it, and this is the
    // only thing that knows when the root it registered stops being the current one. What the
    // guard itself holds is ClickRecoveryGuard's, shared with the other root.
    private FormidableJsModule? _jsModule;
    private readonly ClickRecoveryGuard _clickRecovery = new();

    // The element the guard is scoped to, cached alongside the engine it is derived from rather
    // than recomputed on every render — the same idea FormidableForm applies to its own copy.
    private string _modelLevelFieldId = string.Empty;

    // Set the moment Dispose begins, so the guard's own establish can tell a component on its way
    // out from one merely between renders. Nothing else here needs it: every other path already
    // reads _engine, which Dispose nulls for exactly that purpose.
    private bool _disposed;

    [CascadingParameter]
    private EditContext? CascadedEditContext { get; set; }

    /// <inheritdoc cref="FormidableForm{TModel}.Options"/>
    [Parameter]
    public FormidableOptions? Options { get; set; }

    /// <inheritdoc cref="FormidableForm{TModel}.Validator"/>
    [Parameter]
    public IModelValidator<TModel>? Validator { get; set; }

    /// <summary>
    /// Optional content that receives the cascaded form context — the same
    /// <see cref="FormidableFormContext"/> instance the cascade carries, so markup can reach the
    /// engine inline without capturing this component with <c>@ref</c>. Beside the engine it
    /// carries one move of this component's own,
    /// <see cref="FormidableFormContext.FocusFirstErrorAsync"/>, so a component nested inside
    /// the form can ask for the first-error move without a reference to reach it by. An inline
    /// READ of engine state refreshes when this component re-renders, not on every validation
    /// pass: ongoing state travels through <see cref="IFormidableEngine.StateChanged"/>,
    /// which observing components subscribe to individually, so a live indicator still needs
    /// its own subscription to that event. One consequence of the typed fragment is structural:
    /// this component always sits inside an <c>EditForm</c>, whose own child content also declares
    /// the implicit <c>context</c> name, so the Razor compiler asks for a <c>Context="..."</c>
    /// on one of the two, whether or not either body ever reads the parameter — the attach-mode
    /// sample puts the rename on this component.
    /// </summary>
    [Parameter]
    public RenderFragment<FormidableFormContext>? ChildContent { get; set; }

    /// <summary>
    /// On a submit blocked through <see cref="ValidateForSubmitAsync"/>, best-effort auto-focuses
    /// the field carrying the first error among the engine's visible issues via
    /// <see cref="IFormidableFocusService"/> — the first error rather than merely the first issue,
    /// since a field reported ahead of the failing one can carry nothing worse than an advisory,
    /// and landing there would bury the reason the submit blocked. The fallback to the first
    /// visible issue of any severity applies only when a blocked submit shows no error at all.
    /// Default <see langword="true"/>, matching <c>FormidableForm</c>'s parameter of the same name
    /// and shape. The service is resolved lazily and may be unregistered; a null service is
    /// silent. A focus miss — nothing carries the field's id yet, or what does will not take
    /// focus — retries once through <see cref="FocusFallback"/> when one is wired, and otherwise
    /// reports a diagnostic. Set
    /// <see langword="false"/> to choose focus yourself from the returned
    /// <see cref="SubmitOutcome"/>, and call <see cref="FocusFirstErrorAsync"/> for the same move
    /// once the page is ready for it. This decides only what
    /// <see cref="ValidateForSubmitAsync"/> does on its own: it never gates that call, which makes
    /// the move whenever it is asked to.
    /// </summary>
    /// <remarks>
    /// What "first" means here is not what it means under <c>FormidableForm</c>. A component that
    /// renders no <c>&lt;form&gt;</c> resolves no <see cref="IFormidableFieldOrderService"/>, so
    /// the engine reports its visible issues in channel order — the fault issue, then submit
    /// errors, then advisories, then the live channel — rather than in the document order of the
    /// page, and the field this lands on is the first error in that order.
    /// <see cref="FormidableSummary"/> beside it reads the same order and regroups by severity, so
    /// its first entry names the same field; what neither of them follows is where the fields
    /// actually sit on the page.
    /// </remarks>
    [Parameter]
    public bool FocusFirstErrorOnInvalidSubmit { get; set; } = true;

    /// <summary>
    /// Invoked once when the first error one of this component's own focus moves aimed at does
    /// not take focus: no element renders its id (a virtualized row outside the render window, or
    /// a control that renders none of the deterministic <see cref="FormidableFieldId"/> the focus
    /// service addresses a field by), or the element that does will not take focus, as one inside
    /// a collapsed section will not.
    /// Return <c>true</c> after making the element reachable (scrolling its container, expanding
    /// that section) and the focus is retried exactly once; return <c>false</c> to leave the miss
    /// as-is. Same delegate shape as <see cref="FormidableSummary.FocusFallback"/> and
    /// <c>FormidableForm</c>'s parameter of the same name — a page wiring more than one typically
    /// passes the same callback to each. When unset, a miss reports a diagnostic naming this
    /// parameter, since the visitor otherwise gets no signal at all that the field they need is
    /// out of reach.
    /// </summary>
    [Parameter]
    public Func<FieldIdentifier, ValueTask<bool>>? FocusFallback { get; set; }

    /// <summary>
    /// Invoked and awaited before focus is attempted, so the page can make the target reachable
    /// first: dismissing a modal that covers it, expanding a collapsed section, switching to the
    /// tab it sits on. Receives the field about to be focused. Distinct from
    /// <see cref="FocusFallback"/>, which runs only after an attempt has already missed: this runs
    /// whether or not the element is reachable, and the try-fallback-retry pipeline behind it is
    /// unchanged. Same delegate shape as <see cref="FormidableSummary.PrepareFocus"/> and
    /// <c>FormidableForm</c>'s parameter of the same name, so one page callback wires to all three.
    /// </summary>
    /// <remarks>
    /// The callback must complete when the page is ready to be focused, not when it has begun
    /// getting ready — a dialog is the case that makes the difference visible. Closing one runs a
    /// transition, removes an overlay, and hands focus back to whatever opened it. That last step
    /// is what takes a premature focus move straight back, leaving the visitor somewhere neither
    /// they nor the page chose; the transition and the overlay are why a target focused ahead of
    /// them is not yet one the visitor can use. So a dismissal callback completes on the dialog's
    /// own closed event, not on the state change that starts the close.
    /// <para>
    /// It is invoked only when a move is actually about to be made: a blocked submit whose move
    /// <see cref="FocusFirstErrorOnInvalidSubmit"/> called off, a host that registered no
    /// <see cref="IFormidableFocusService"/>, and a move that finds no visible issue to land on
    /// all skip it — a side effect as visible as closing a dialog must not fire for a move that
    /// never happens. Once per move, before the first attempt: a fallback's retry does not run it
    /// a second time. Two moves reach it — <see cref="ValidateForSubmitAsync"/>'s and the one a
    /// page asks for through <see cref="FocusFirstErrorAsync"/> — since applying server issues
    /// here never moves focus at all; a throw surfaces out of whichever of the two made the move,
    /// exactly as a throw from <see cref="FocusFallback"/> does.
    /// </para>
    /// <para>
    /// A <see cref="Func{T, TResult}"/> rather than an <see cref="EventCallback{TValue}"/>,
    /// because invoking one of those routes through <see cref="IHandleEvent"/> on the component
    /// that supplied the handler, and <see cref="ComponentBase"/>'s implementation calls
    /// <c>StateHasChanged</c> for it: once for a handler that completes synchronously, and a
    /// second time once an asynchronous one completes. This hook is awaited in the middle of a
    /// move — after the field is chosen, before its element is addressed — and a render of the
    /// page belongs to what the page changed, not to its having been asked to make the target
    /// reachable.
    /// </para>
    /// </remarks>
    [Parameter]
    public Func<FieldIdentifier, ValueTask>? PrepareFocus { get; set; }

    [Inject]
    private IServiceProvider Services { get; set; } = default!;

    /// <inheritdoc cref="FormidableForm{TModel}.Engine"/>
    public IFormidableEngine? Engine => _engine;

    /// <inheritdoc />
    protected override void OnParametersSet()
    {
        if (CascadedEditContext is null)
        {
            throw new InvalidOperationException(
                $"{nameof(FormidableValidator<TModel>)} must be placed inside an EditForm (no cascading EditContext found).");
        }

        if (CascadedEditContext.Model is not TModel model)
        {
            throw new InvalidOperationException(
                $"The EditForm model is '{(CascadedEditContext.Model is { } actual ? FriendlyTypeName.Of(actual.GetType()) : "null")}' but {nameof(FormidableValidator<TModel>)} expects '{FriendlyTypeName.Of(typeof(TModel))}'.");
        }

        if (_engine is not null && !ReferenceEquals(_engine.EditContext, CascadedEditContext))
        {
            _engine.Registry.Changed -= OnFieldRegistryChanged;
            _engine.Dispose();
            _engine = null;
        }

        // _context is rebuilt only when _engine is: it is the cascaded CascadingValue's Value
        // under an IsFixed cascade below, and IsFixed's whole premise is that Value does not
        // change on a render where nothing about the underlying engine actually did.
        if (_engine is null)
        {
            _boundOptions = Options;
            _engine = FormidableEngineFactory.Create(
                model,
                CascadedEditContext,
                Services,
                Validator,
                Options,
                renderDispatch: work => InvokeAsync(work));
            _context = new FormidableFormContext(_engine, FocusFirstErrorAsync);
            _modelLevelFieldId = FormidableFieldId.For(_engine.ModelLevelField);
            _lastReconciledFieldSetVersion = -1;
            _engine.Registry.Changed += OnFieldRegistryChanged;
        }
        else
        {
            FormidableEngineFactory.VerifyOptionsUnchanged(
                nameof(FormidableValidator<TModel>),
                _boundOptions,
                Options,
                "swap the EditForm's model alongside Options, so a new EditContext rebuilds the engine");
        }
    }

    /// <summary>
    /// Asks the library's own script to guard this form's buttons against a click the page
    /// displaces out from under the pointer — see <see cref="DisplacedClickRecovery"/> for what is
    /// recovered and <see cref="FormidableOptions.ClickRecovery"/> for turning it off. Attempted
    /// once per context, on the first render after the engine binds; the same bargain
    /// <c>FormidableForm</c> strikes, and for the same reason.
    /// </summary>
    /// <remarks>
    /// This is the one place the guard has to hunt for a root, and so the one place its answer is
    /// worth reading. A component that renders no element of its own has none to register, so two
    /// candidates are offered in order: the element carrying the model-level gate id, which a page
    /// rendering the all-suppressed gate's landing spot already has (see
    /// <see cref="FormidableFieldId"/>), and failing that the nearest <c>&lt;form&gt;</c> ancestor
    /// of a registered field — the same boundary <c>FormidableForm</c> would have rendered. A page
    /// offering neither gets no guard and a diagnostic saying so, rather than silence: attach mode
    /// is the migration path, and a consumer who has to name a root by hand to keep a working
    /// submit would simply keep the defect instead. <see cref="ClickRecoveryGuard"/> owns
    /// everything up to that answer, on the same terms for both roots.
    /// </remarks>
    /// <param name="firstRender">Not consulted. The context's own identity is the gate instead, so
    /// a cascaded <see cref="EditContext"/> swap re-registers the guard on the render that follows
    /// it rather than the guard belonging to the component's very first render alone.</param>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_engine is null)
        {
            return;
        }

        var registered = await _clickRecovery.EstablishAsync(
            _context,
            _modelLevelFieldId,
            _engine.Options,
            _engine.Registry,
            ResolveJsModule,
            () => _disposed);

        if (registered is false)
        {
            ReportNoClickRecoveryRoot();
        }
    }

    /// <summary>
    /// This component's own script module, created on first use and released with it. Null when
    /// the host resolves no <see cref="IJSRuntime"/> at all, which is what the guard treats as
    /// "there is no browser to ask".
    /// </summary>
    private FormidableJsModule? ResolveJsModule() =>
        Services.GetService<IJSRuntime>() is { } jsRuntime
            ? _jsModule ??= new FormidableJsModule(jsRuntime)
            : null;

    /// <summary>
    /// The one report a page offering the guard nothing to scope itself to gets: a Trace line for
    /// a debugger, and a logged warning when the host resolved an <see cref="ILoggerFactory"/> —
    /// the same dual channel <c>FormidableForm</c>'s unwired-<c>FocusFallback</c> miss already
    /// uses. It names both routes, because either one closes the gap in a line.
    /// </summary>
    private void ReportNoClickRecoveryRoot()
    {
        const string message =
            "Formidable: no element could be found to scope the displaced-click guard to, so a click the " +
            "page moves out from under the pointer is lost. Put the model-level FormidableFieldId on the " +
            "EditForm this attaches to, or place it around a field this component registers, or set " +
            "FormidableOptions.ClickRecovery to None to ask for no guard at all.";

        System.Diagnostics.Trace.WriteLine(message);
        ((ILoggerFactory?)Services.GetService(typeof(ILoggerFactory)))?
            .CreateLogger("Formidable").LogWarning(message);
    }

    /// <summary>
    /// Tears down the guard this component registered, and the module it was registered through.
    /// </summary>
    /// <remarks>
    /// Worth doing on its own account, exactly as disconnecting the layout observer is under
    /// <c>FormidableForm</c> — see <see cref="ClickRecoveryGuard.ReleaseAsync"/> for what an entry
    /// left behind costs a page. Disposal here is synchronous and the release is not, so it rides
    /// a discarded task; an interop boundary that is already gone has taken the guard with it
    /// anyway.
    /// </remarks>
    private void ReleaseClickRecovery()
    {
        var module = _jsModule;
        var releasing = _clickRecovery.Take();

        _jsModule = null;

        if (module is null)
        {
            return;
        }

        _ = ReleaseClickRecoveryAsync(module, releasing);
    }

    private static async Task ReleaseClickRecoveryAsync(FormidableJsModule module, string releasing)
    {
        try
        {
            await ClickRecoveryGuard.ReleaseAsync(module, releasing);
            await module.DisposeAsync();
        }
        catch (Exception exception) when (FormidableJsModule.IsInteropFailure(exception))
        {
            // A boundary that is gone, disconnected or never loaded has nothing left holding the
            // guard, and a component being torn down is no place to raise that as a failure.
        }
    }

    /// <summary>
    /// Runs the submit pipeline against this component's engine and hands back its
    /// <see cref="SubmitOutcome"/> untouched — the entry point a page's own
    /// <c>EditForm</c> submit handler calls in place of reaching through <see cref="Engine"/>.
    /// A blocked submit additionally moves focus to the first error, gated on
    /// <see cref="FocusFirstErrorOnInvalidSubmit"/>, preceded by <see cref="PrepareFocus"/> and
    /// recoverable through <see cref="FocusFallback"/>, by the same decision
    /// <c>FormidableForm</c>'s own submit makes.
    /// What it does NOT do is anything that belongs to owning the form element: the page keeps its
    /// own <c>EditForm</c>, its own handler, and its own routing of the outcome to whatever it
    /// shows next. Call from the renderer's synchronization context (a Blazor event handler or
    /// <c>InvokeAsync</c>) — it mutates validation state and triggers renders. If the cascaded
    /// <see cref="EditContext"/> is replaced while this call is still awaiting the pipeline, the
    /// engine that started is gone by the time the verdict lands: focus is not moved, and the
    /// blocked <see cref="SubmitOutcome"/> below is returned instead of whatever that pass
    /// actually decided. Cancelling the abandoned pass's token is what usually stops it short,
    /// but a validator that does not honour the token can still run to completion, and a dead
    /// engine's verdict — from a submit the swap already abandoned — must not surface as if it
    /// were current.
    /// </summary>
    /// <remarks>
    /// Focus parity is not order parity. This component renders no <c>&lt;form&gt;</c> and so
    /// resolves no <see cref="IFormidableFieldOrderService"/>: a summary inside a consumer's own
    /// <c>EditForm</c> lists issues in the engine's channel order rather than the document order
    /// of the page, and the first error this lands on is the first in that same order. Reading
    /// order is a separate boundary, and moving the page to <c>FormidableForm</c> is what closes
    /// it.
    /// </remarks>
    /// <returns>The engine's verdict for this submit, unchanged.</returns>
    public async Task<SubmitOutcome> ValidateForSubmitAsync()
    {
        var outcome = await RootSubmit.RunAsync(RequireEngine(), () => _engine);
        if (outcome is null)
        {
            return RootSubmit.Superseded;
        }

        if (!outcome.CanProceed && FocusFirstErrorOnInvalidSubmit)
        {
            await FocusFirstErrorAsync();
        }

        return outcome;
    }

    /// <summary>
    /// Moves focus to the first error among the engine's visible issues. This is the move
    /// <see cref="ValidateForSubmitAsync"/> makes under
    /// <see cref="FocusFirstErrorOnInvalidSubmit"/>, offered to a page that wants to choose the
    /// moment instead: that path calls this very method, so which field the visitor lands on,
    /// <see cref="PrepareFocus"/> being awaited ahead of the attempt, and
    /// <see cref="FocusFallback"/> recovering a miss are not merely alike here — they are the
    /// same code, and the only difference is who asked. Which also means it is not gated by
    /// <see cref="FocusFirstErrorOnInvalidSubmit"/>: that parameter decides what a blocked submit
    /// does on its own, and this call is the page deciding.
    /// </summary>
    /// <remarks>
    /// Attach mode needs nothing like the per-submit suppression <c>FormidableForm</c>'s
    /// invalid-submit handler has, because the page owns the submit call outright: set
    /// <see cref="FocusFirstErrorOnInvalidSubmit"/> to <see langword="false"/> and call this when
    /// it suits — once the dialog the page opened instead has closed, say — and that is the
    /// whole sequence. What it adds over reaching through <see cref="Engine"/> for an issue and
    /// focusing it by hand is <see cref="PrepareFocus"/> and <see cref="FocusFallback"/> threaded
    /// in. A dialog component that lives inside <see cref="ChildContent"/> rather than beside
    /// this one has no <c>@ref</c> to reach this by and does not need one:
    /// <see cref="FormidableFormContext.FocusFirstErrorAsync"/> on the cascaded context calls
    /// this method.
    /// <para>
    /// "First error" resolves exactly as the automatic move resolves it, which includes the case
    /// where there is no error: a form showing nothing worse than advisories lands on the first of
    /// those rather than on nothing. It is first in the engine's channel order rather than the
    /// document order of the page, for the reason
    /// <see cref="FocusFirstErrorOnInvalidSubmit"/> gives. Call from the renderer's
    /// synchronization context (a Blazor event handler or <c>InvokeAsync</c>).
    /// </para>
    /// </remarks>
    /// <returns>
    /// <see langword="true"/> when an element took focus, and <see langword="false"/> when nothing
    /// did — the engine reports no visible issue at all, no <see cref="IFormidableFocusService"/>
    /// is registered, or the chosen field's element could not be focused: no
    /// <see cref="FocusFallback"/> wired, a fallback that declined, or a retry that missed
    /// again. It reports the move, not the form: a form with no issue on screen and a form
    /// whose one error is out of reach both answer <see langword="false"/>, and a caller that
    /// needs to tell those apart reads
    /// <see cref="IFormidableEngine.GetVisibleIssues"/> through <see cref="Engine"/>. A
    /// false answer means this call moved nothing, so a page with nowhere else to send the
    /// visitor can ignore it.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// This component has no engine yet, because the call arrived before it bound to its cascaded
    /// <see cref="EditContext"/>.
    /// </exception>
    public async Task<bool> FocusFirstErrorAsync() =>
        await FirstErrorFocus.MoveAsync(Services, RequireEngine(), FocusFallback, PrepareFocus);

    /// <summary>
    /// Applies a server response's issues to this form's engine, forwarding
    /// <see cref="IFormidableEngine.ApplyServerIssues(IEnumerable{ValidationIssue})"/> and its
    /// contract whole: the payload is the server's current verdict and replaces what the previous
    /// call applied, each issue lands at the severity it carries, and applying any also sets
    /// <see cref="IFormidableEngine.HasSubmitted"/>, since the payload is treated as a submit
    /// result. A page holding the form with <c>@ref</c> has everything the round trip needs here,
    /// without reaching through <see cref="Engine"/> for it. Call from the renderer's
    /// synchronization context (a Blazor event handler or <c>InvokeAsync</c>) — it mutates
    /// validation state and triggers renders.
    /// </summary>
    /// <param name="issues">The server's current verdict. Enumerated exactly once.</param>
    public void ApplyServerIssues(IEnumerable<ValidationIssue> issues) =>
        RequireEngine().ApplyServerIssues(issues);

    /// <summary>
    /// Applies a deserialized validation ProblemDetails body — the shape an HTTP 400 from
    /// Formidable.AspNetCore arrives in — by flattening it with
    /// <see cref="FormidableValidationProblem.ToIssues"/>. Equivalent to the sequence overload in
    /// every respect, including the <see cref="IFormidableEngine.HasSubmitted"/> side effect;
    /// this is the whole client half of the round trip in one call.
    /// </summary>
    /// <param name="problem">The deserialized response body.</param>
    public void ApplyServerIssues(FormidableValidationProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);
        RequireEngine().ApplyServerIssues(problem.ToIssues());
    }

    /// <summary>
    /// Says what the values already in the model have earned, forwarding
    /// <see cref="IFormidableEngine.DiscloseLoadedValuesAsync"/> and its contract whole: a
    /// whole-model <see cref="FormidableOptions.SubmitProfile"/> pass, after which each field
    /// the rules pass is confirmed, each field failing something other than a presence rule
    /// discloses that failure, and each field that is merely unfilled stays silent. Call it once
    /// after filling the model from a saved draft or a loaded record, so the form opens saying
    /// what it already knows instead of looking pristine. A form that never calls it is
    /// unaffected in every respect. Unlike a blocked submit this moves no focus. Call from the
    /// renderer's synchronization context (a Blazor event handler or <c>InvokeAsync</c>) — it
    /// mutates validation state and triggers renders.
    /// </summary>
    /// <param name="cancellationToken">
    /// Cancels the pass under the engine's contract — a cancelled call throws and adopts
    /// nothing, detailed on <see cref="IFormidableEngine.DiscloseLoadedValuesAsync"/>'s own
    /// parameter. The submit methods take no token deliberately: a submit is UI-event-driven,
    /// and its event handler holds none to pass. This call is data-driven — the caller that
    /// filled the model typically holds the <see cref="CancellationTokenSource"/> it minted for
    /// the load, so a superseded record-open or a dispose mid-load cancels the disclosure
    /// through the same token that cancels the fetch.
    /// </param>
    public Task DiscloseLoadedValuesAsync(CancellationToken cancellationToken = default) =>
        RequireEngine().DiscloseLoadedValuesAsync(cancellationToken);

    /// <summary>
    /// Reconciles the engine's view of which fields are still on the page against the registry,
    /// pruning verdicts for fields that have since unregistered — the attach-mode counterpart of
    /// what <c>FormidableForm</c> does for itself every render by polling
    /// <c>Registry.Version</c>. Not required for ordinary use: this component already notices a
    /// rendered-field-set change on its own (see <see cref="OnFieldRegistryChanged"/>) and calls
    /// this same reconcile shortly after the render that caused it, with no consumer action
    /// needed. It is exposed as an override for a use case where that built-in signal proves
    /// insufficient — for example, reading <see cref="Engine"/> synchronously right after a
    /// mutation that also unregisters a field, rather than waiting for the automatic reconcile's
    /// posted continuation to run. Call it from the renderer's synchronization context (a Blazor
    /// event handler or <c>InvokeAsync</c>), the same as any other call that touches the engine.
    /// </summary>
    public void NotifyFieldSetChanged() => ReconcileIfChanged();

    /// <summary>
    /// <see cref="FieldRegistry.Changed"/>'s subscriber. Fires synchronously from inside whatever
    /// render batch changed a registration — reacting inline here, or through this component's
    /// own <c>InvokeAsync</c>, would observe that batch's transient, still-settling state (see
    /// <see cref="FieldRegistry.Changed"/>'s own remarks): <c>InvokeAsync</c> runs synchronously,
    /// inline, whenever the caller is already on the dispatcher, which is exactly this call site.
    /// Two genuinely deferring paths follow, each the standard one on a different host rather
    /// than one being a fallback for the other: on Blazor Server (and in bUnit, which resolves
    /// the same dispatcher shape), a real <see cref="SynchronizationContext"/> is always current
    /// here, and capturing it and posting to it directly is what defers — the continuation runs
    /// only once every registration change in the batch has landed. On Blazor WebAssembly's
    /// default single-threaded runtime, no <see cref="SynchronizationContext"/> is ever installed
    /// at all, on any thread, at any time — that branch is not a rare edge case there, it is the
    /// only branch this method ever takes, every single time. <see cref="DeferReconcileAsync"/>
    /// is what defers on that host instead. Either way, the version gate in
    /// <see cref="ReconcileIfChanged"/> collapses however many of these fire in one batch to a
    /// single reconcile.
    /// </summary>
    private void OnFieldRegistryChanged()
    {
        var context = SynchronizationContext.Current;
        if (context is null)
        {
            _ = DeferReconcileAsync();
            return;
        }

        context.Post(_ => ReconcileIfChanged(), null);
    }

    /// <summary>
    /// Defers <see cref="ReconcileIfChanged"/> past the current synchronous call stack when there
    /// is no <see cref="SynchronizationContext"/> to <c>Post</c> through — Blazor WebAssembly's
    /// default single-threaded runtime never installs one, on any thread, so this is that host's
    /// only way to defer past a render batch rather than observe it mid-diff. <c>Task.Yield()</c>
    /// queues the rest of this method through the thread pool; on a single-threaded host that
    /// still means the continuation only runs once the call stack that queued it has unwound all
    /// the way back to the browser's own event loop, which restores the same "after the batch"
    /// guarantee the <c>Post</c> branch gives on a host that has a context to post through.
    /// Fire-and-forget by design: <see cref="ReconcileIfChanged"/> invokes nothing that runs a
    /// consumer's own code — no validator, no user delegate, only this registry's and the
    /// engine's own internal bookkeeping — so nothing reachable from here is expected to throw,
    /// the same premise the <c>Post</c> branch's own equally unguarded call to the same method
    /// already relies on; a genuine defect surfaces as an unobserved task exception rather than
    /// being caught and silently discarded.
    /// </summary>
    private async Task DeferReconcileAsync()
    {
        await Task.Yield();
        ReconcileIfChanged();
    }

    /// <summary>
    /// The version-gated reconcile both deferred paths above and <see cref="NotifyFieldSetChanged"/>
    /// run. Comparing against <see cref="_lastReconciledFieldSetVersion"/> before latching and
    /// calling <see cref="FormidableEngine{TModel}.OnRenderedFieldsChanged"/> is what lets
    /// several registry changes in one batch — each deferring its own continuation — coalesce to
    /// one reconcile: whichever continuation runs first finds the version has moved and does the
    /// work, and every later one for the same settled state finds nothing new and skips. Guards
    /// first on <see cref="_engine"/> being null, which is also what keeps a continuation that
    /// was still pending when <see cref="Dispose"/> ran from reconciling against the engine
    /// Dispose already tore down — Dispose nulls the field for exactly this reason.
    /// </summary>
    private void ReconcileIfChanged()
    {
        if (_engine is null)
        {
            return;
        }

        var version = _engine.Registry.Version;
        if (version == _lastReconciledFieldSetVersion)
        {
            return;
        }

        _lastReconciledFieldSetVersion = version;
        ReconcileCount++;
        _engine.OnRenderedFieldsChanged();
    }

    /// <summary>
    /// The engine, or the reason there is not one yet. It is built on binding to the cascaded
    /// <c>EditContext</c>, so every entry point that runs the pipeline has to answer for a call
    /// that beats the first render rather than let it surface from inside the component as a null
    /// reference.
    /// </summary>
    private FormidableEngine<TModel> RequireEngine() =>
        _engine ?? throw new InvalidOperationException(
            $"{nameof(FormidableValidator<TModel>)} has no engine yet — one is built when it first " +
            "binds to its cascaded EditContext, and this call arrived before that. Capture it with " +
            "@ref and call it from an event handler rather than from a lifecycle method that runs " +
            "ahead of the first render.");

    /// <inheritdoc />
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        if (_context is null || ChildContent is null)
        {
            return;
        }

        builder.OpenComponent<CascadingValue<FormidableFormContext>>(0);
        builder.AddComponentParameter(1, "IsFixed", true);
        builder.AddComponentParameter(2, "Value", _context);
        builder.AddComponentParameter(3, "ChildContent", ChildContent(_context));
        builder.CloseComponent();
        // IsFixed: same reasoning as FormidableForm's identical cascade — a non-fixed
        // CascadingValue re-supplies every subscriber's parameters from a stale snapshot on every
        // re-render, which is what let a kit input's just-committed Value be overwritten with its
        // pre-keystroke value. Safe here for the same structural reason: the enclosing EditForm
        // (owned by the consumer, not this component) renders its subtree inside a region keyed on
        // its EditContext, so an EditContext swap tears down and rebuilds this component — and
        // everything cascaded from it — rather than leaving it in place to observe a replacement.
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // Recorded first, so an establish still awaiting its round trip finds this set rather
        // than registering a guard the release below has already gone past.
        _disposed = true;

        if (_engine is not null)
        {
            _engine.Registry.Changed -= OnFieldRegistryChanged;
            _engine.Dispose();
            _engine = null;
        }

        ReleaseClickRecovery();
    }
}
