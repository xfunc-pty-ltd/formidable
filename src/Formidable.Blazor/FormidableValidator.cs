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
    private FormValidationEngine<TModel>? _engine;
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
    // only thing that knows when the root it registered stops being the current one. The context
    // is what an EditContext swap replaces, and the id is the key the guard was registered under.
    private FormidableJsModule? _jsModule;
    private FormidableFormContext? _clickRecoveryContext;
    private string _clickRecoveryRootId = string.Empty;

    // Set the moment Dispose begins, so the guard's own establish can tell a component on its way
    // out from one merely between renders. Nothing else here needs it: every other path already
    // reads _engine, which Dispose nulls for exactly that purpose.
    private bool _disposed;

    [CascadingParameter]
    private EditContext? CascadedEditContext { get; set; }

    /// <summary>Engine options; defaults apply when omitted.</summary>
    [Parameter]
    public FormidableOptions? Options { get; set; }

    /// <summary>Validator override; resolved from DI when omitted.</summary>
    [Parameter]
    public IModelValidator<TModel>? Validator { get; set; }

    /// <summary>Optional content that receives the cascaded form context.</summary>
    [Parameter]
    public RenderFragment? ChildContent { get; set; }

    /// <summary>
    /// On a submit blocked through <see cref="ValidateForSubmitAsync"/>, best-effort auto-focuses
    /// the field carrying the first error among the engine's visible issues via
    /// <see cref="IFormidableFocusService"/> — the first error rather than merely the first issue,
    /// since a field reported ahead of the failing one can carry nothing worse than an advisory,
    /// and landing there would bury the reason the submit blocked. The fallback to the first
    /// visible issue of any severity applies only when a blocked submit shows no error at all.
    /// Default <see langword="true"/>, matching <c>FormidableForm</c>'s parameter of the same name
    /// and shape. The service is resolved lazily and may be unregistered; a null service is
    /// silent. A focus miss (e.g. no element carries the field's id yet) retries once through
    /// <see cref="FocusFallback"/> when one is wired, and otherwise reports a diagnostic. Set
    /// <see langword="false"/> to choose focus yourself from the returned
    /// <see cref="SubmitOutcome"/>.
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
    /// Invoked once when a blocked submit's auto-focused first error has no rendered element to
    /// focus (e.g. a virtualized row outside the render window, or a control that renders none of
    /// the deterministic <see cref="FormidableFieldId"/> the focus service addresses a field by).
    /// Return <c>true</c> after making the element renderable (scrolling its container, expanding
    /// a section) and the focus is retried exactly once; return <c>false</c> to leave the miss
    /// as-is. Same delegate shape as <see cref="FormidableSummary.FocusFallback"/> and
    /// <c>FormidableForm</c>'s parameter of the same name — a page wiring more than one typically
    /// passes the same callback to each. When unset, a miss reports a diagnostic naming this
    /// parameter, since a blocked submit's visitor otherwise gets no signal at all that the field
    /// they need is out of reach.
    /// </summary>
    [Parameter]
    public Func<FieldIdentifier, ValueTask<bool>>? FocusFallback { get; set; }

    [Inject]
    private IServiceProvider Services { get; set; } = default!;

    /// <summary>The engine view (also cascaded via the form context).</summary>
    public IFormValidationEngine? Engine => _engine;

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
            _context = new FormidableFormContext(_engine);
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
    /// This is the one place the guard has to hunt for a root. A component that renders no element
    /// of its own has none to register, so two candidates are offered in order: the element
    /// carrying the model-level gate id, which a page rendering the all-suppressed gate's landing
    /// spot already has (see <see cref="FormidableFieldId"/>), and failing that the nearest
    /// <c>&lt;form&gt;</c> ancestor of a registered field — the same boundary
    /// <c>FormidableForm</c> would have rendered. A page offering neither gets no guard and a
    /// diagnostic saying so, rather than silence: attach mode is the migration path, and a
    /// consumer who has to name a root by hand to keep a working submit would simply keep the
    /// defect instead.
    /// </remarks>
    /// <param name="firstRender">Not consulted. The context's own identity is the gate instead, so
    /// a cascaded <see cref="EditContext"/> swap re-registers the guard on the render that follows
    /// it rather than the guard belonging to the component's very first render alone.</param>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_engine is null || ReferenceEquals(_clickRecoveryContext, _context))
        {
            return;
        }

        _clickRecoveryContext = _context;

        // Read before anything is awaited: the gate id is derived from the model, and a cascaded
        // EditContext swap rebuilds the engine over a different one, so the guard the previous
        // context installed has to be released by the key it was registered under rather than by
        // the one about to replace it.
        var releasing = _clickRecoveryRootId;
        _clickRecoveryRootId = string.Empty;

        var jsRuntime = Services.GetService<IJSRuntime>();
        if (jsRuntime is null)
        {
            return;
        }

        _jsModule ??= new FormidableJsModule(jsRuntime);

        var gateId = FormidableFieldId.For(_engine.ModelLevelField);

        bool registered;
        try
        {
            if (releasing.Length > 0)
            {
                await _jsModule.InvokeVoidAsync("releaseClickRecovery", releasing);
            }

            if (_engine.Options.ClickRecovery != DisplacedClickRecovery.Buttons)
            {
                return;
            }

            // Mirrors FormidableForm's own guard, for the same reason: this component's release
            // has already run and found the key below still empty, so anything registered after
            // it is an entry nothing will ever take back.
            if (_disposed)
            {
                return;
            }

            // The fallback candidates, built only once the option has asked for a guard: every
            // field something has registered, for the script to walk up from to a <form>.
            var fieldIds = _engine.Registry.RevealedFields.Select(FormidableFieldId.For).ToArray();
            registered = await _jsModule.InvokeAsync<bool>("registerClickRecovery", gateId, fieldIds);
        }
        catch
        {
            // No script, no guard — the same wide-open catch FormidableForm's own establish uses,
            // and for the same reason: behind this call is the library's own script reached
            // through the library's own module, with no consumer code in it to preserve a bug for.
            return;
        }

        if (_disposed)
        {
            // Torn down while the round trip above was in flight. Neither half of what follows is
            // worth doing: there is nothing left to undo the registration with, and a diagnostic
            // about a component already off the page names a problem nobody can act on.
            return;
        }

        if (!registered)
        {
            ReportNoClickRecoveryRoot();
            return;
        }

        _clickRecoveryRootId = gateId;
    }

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
    /// <c>FormidableForm</c>: the script module outlives this component — a module is loaded once
    /// per document and stays — so an entry left behind would scope the guard to an element that
    /// is no longer in the document, and the document-level listeners it refcounts would stay
    /// installed over a page with nothing left to guard. Disposal here is synchronous and the
    /// release is not, so it rides a discarded task; an interop boundary that is already gone has
    /// taken the guard with it anyway.
    /// </remarks>
    private void ReleaseClickRecovery()
    {
        var module = _jsModule;
        var releasing = _clickRecoveryRootId;

        _jsModule = null;
        _clickRecoveryRootId = string.Empty;
        _clickRecoveryContext = null;

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
            if (releasing.Length > 0)
            {
                await module.InvokeVoidAsync("releaseClickRecovery", releasing);
            }

            await module.DisposeAsync();
        }
        catch (Exception exception) when (
            exception is JSException or JSDisconnectedException or ObjectDisposedException
                or OperationCanceledException)
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
    /// <see cref="FocusFirstErrorOnInvalidSubmit"/> and recoverable through
    /// <see cref="FocusFallback"/>, by the same decision <c>FormidableForm</c>'s own submit makes.
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
        var engine = RequireEngine();
        var outcome = await engine.ValidateForSubmitAsync();

        if (!ReferenceEquals(_engine, engine))
        {
            // The engine this pass ran against has been replaced or disposed — a cascaded
            // EditContext swap, or this component leaving the page. Its verdict, even a passing
            // one if its validator outran cancellation, belongs to a submit that is no longer the
            // current one, and moving focus from it would take the visitor to a field on a model
            // nothing is editing any more. Mirrors FormidableForm.SubmitAsync's identical guard.
            return new SubmitOutcome(false, ValidationReport.Empty, []);
        }

        if (!outcome.CanProceed && FocusFirstErrorOnInvalidSubmit)
        {
            await FirstErrorFocus.MoveAsync(Services, engine, FocusFallback);
        }

        return outcome;
    }

    /// <summary>
    /// Applies a server response's issues to this form's engine, forwarding
    /// <see cref="IFormValidationEngine.ApplyServerIssues(IEnumerable{ValidationIssue})"/> and its
    /// contract whole: the payload is the server's current verdict and replaces what the previous
    /// call applied, each issue lands at the severity it carries, and applying any also sets
    /// <see cref="IFormValidationEngine.HasSubmitted"/>, since the payload is treated as a submit
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
    /// every respect, including the <see cref="IFormValidationEngine.HasSubmitted"/> side effect;
    /// this is the whole client half of the round trip in one call.
    /// </summary>
    /// <param name="problem">The deserialized response body.</param>
    public void ApplyServerIssues(FormidableValidationProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);
        RequireEngine().ApplyServerIssues(problem.ToIssues());
    }

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
    /// calling <see cref="FormValidationEngine{TModel}.OnRenderedFieldsChanged"/> is what lets
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
    private FormValidationEngine<TModel> RequireEngine() =>
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
        builder.AddComponentParameter(3, "ChildContent", ChildContent);
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
