using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;

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
        if (_engine is not null)
        {
            _engine.Registry.Changed -= OnFieldRegistryChanged;
            _engine.Dispose();
            _engine = null;
        }
    }
}
