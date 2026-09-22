using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace Formidable.Blazor;

/// <summary>The root that attaches the engine to an <see cref="EditForm"/> the page already owns, for a page that keeps its own <c>&lt;form&gt;</c> and submit handler.</summary>
/// <typeparam name="TModel">The type of the cascaded <see cref="EditContext"/>'s model.</typeparam>
/// <remarks>
/// It renders no <c>&lt;form&gt;</c> and resolves no field order, so a summary beside it lists
/// issues in the engine's order rather than the page's. Replacing the cascaded
/// <see cref="EditContext"/> rebuilds the engine.
/// </remarks>
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

    /// <summary>The markup rendered inside the cascade, handed the <see cref="FormidableFormContext"/>; needs <c>Context="..."</c> on this element or on the <see cref="EditForm"/> around it.</summary>
    /// <remarks>
    /// With none, the component renders nothing, cascade included. An inline read of engine
    /// state refreshes when this component re-renders, not on every check; a live indicator
    /// subscribes to <see cref="IFormidableEngine.StateChanged"/> itself.
    /// </remarks>
    [Parameter]
    // Typed, and the Context= rename is always owed here because EditForm's own child content
    // also declares the implicit context name: measured on canonical Blazor, EditForm plus
    // Virtualize alone emit the same RZ9999, so the tax is the platform's own. What collides is
    // the declaration, not any use of it. The attach-mode sample puts the rename on this element.
    public RenderFragment<FormidableFormContext>? ChildContent { get; set; }

    /// <summary>Whether a submit blocked through <see cref="ValidateForSubmitAsync"/> moves focus to the first error, in the engine's order. Defaults to <see langword="true"/>.</summary>
    [Parameter]
    public bool FocusFirstErrorOnInvalidSubmit { get; set; } = true;

    /// <summary>Called once when a focus move misses; return <see langword="true"/> after making the field reachable and the move retries once, <see langword="false"/> to leave the miss.</summary>
    /// <remarks>
    /// The same delegate shape as <see cref="FormidableForm{TModel}.FocusFallback"/> and
    /// <see cref="FormidableSummary.FocusFallback"/>, so one callback serves all three; unset, a
    /// miss logs a diagnostic naming this parameter.
    /// </remarks>
    [Parameter]
    public Func<FieldIdentifier, ValueTask<bool>>? FocusFallback { get; set; }

    /// <summary>Awaited before either focus move this component makes, with the field about to be focused, so the page can make it reachable first.</summary>
    /// <remarks>
    /// Complete it when the page is ready to take focus: on a dialog's closed event, not on the
    /// state change that starts the close. It runs once per move, ahead of the first attempt, and
    /// only for a move that is about to happen. Applying server issues here moves no focus, so a
    /// throw surfaces from <see cref="ValidateForSubmitAsync"/> or <see cref="FocusFirstErrorAsync"/>.
    /// </remarks>
    [Parameter]
    // A Func rather than an EventCallback, for the reason FormidableForm's parameter of the same
    // name gives: invoking an EventCallback routes through IHandleEvent on the supplying
    // component, whose ComponentBase implementation calls StateHasChanged for it, and this hook
    // is awaited in the middle of a move, where a render belongs to what the page changed.
    public Func<FieldIdentifier, ValueTask>? PrepareFocus { get; set; }

    [Inject]
    private IServiceProvider Services { get; set; } = default!;

    /// <inheritdoc cref="FormidableForm{TModel}.Engine"/>
    public IFormidableEngine? Engine => _engine;

    /// <summary>Builds the engine over the cascaded <see cref="EditContext"/>'s model on the first parameter set, and rebuilds it when that <see cref="EditContext"/> is replaced.</summary>
    /// <exception cref="InvalidOperationException">No <see cref="EditContext"/> is cascaded, its model is not a <typeparamref name="TModel"/>, <see cref="Options"/> is a different instance than the engine was built with, the container resolves no <c>IModelIntrospector</c>, or, with <see cref="Validator"/> unset, it resolves no <see cref="IModelValidator{TModel}"/>.</exception>
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
            TearDownEngine();
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

    /// <summary>After each render, establishes the displaced-click guard once per engine, and reports once when the page offers it no element to scope to.</summary>
    /// <param name="firstRender">Whether this is the component's first render; not consulted, because the guard follows the engine and a replaced <see cref="EditContext"/> re-establishes it.</param>
    // The one place the guard has to hunt for a root, and so the one place its answer is worth
    // reading: a component that renders no element of its own has none to register, so the script
    // is offered the element carrying the model-level field id and, failing that, the nearest
    // <form> ancestor of a registered field. A page offering neither gets a diagnostic rather than
    // silence, because attach mode is the migration path, and a consumer who has to name a root
    // by hand to keep a working submit would simply keep the defect instead. ClickRecoveryGuard
    // owns everything up to that answer, on the same terms for both roots.
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

    /// <summary>This component's own script module, created on first use and released with it.</summary>
    /// <returns>The module, or <see langword="null"/> when the host resolves no <see cref="IJSRuntime"/>, which the guard treats as no browser to ask.</returns>
    private FormidableJsModule? ResolveJsModule() =>
        Services.GetService<IJSRuntime>() is { } jsRuntime
            ? _jsModule ??= new FormidableJsModule(jsRuntime)
            : null;

    /// <summary>Writes the diagnostic for a page that gives the click guard no element to scope to, naming the three ways to fix it, through <see cref="FormidableDiagnostics"/>.</summary>
    private void ReportNoClickRecoveryRoot()
    {
        const string message =
            "Formidable: no element could be found to scope the displaced-click guard to, so a click the " +
            "page moves out from under the pointer is lost. Put the model-level FormidableFieldId on the " +
            "EditForm this attaches to, or place a <form> element around a field this component " +
            "registers, or set FormidableOptions.ClickRecovery to None to ask for no guard at all.";

        FormidableDiagnostics.Warn(FormidableEngineFactory.ResolveLogger(Services), message);
    }

    /// <summary>Releases the guard this component registered and the module it was registered through, without awaiting the browser.</summary>
    // Worth doing on its own account, exactly as disconnecting the layout observer is under
    // FormidableForm: the script module outlives this component, and a guard left registered
    // scopes itself to an element no longer in the document. Disposal here is synchronous and the
    // release is not, so it rides a discarded task; an interop boundary that is already gone has
    // taken the guard with it anyway.
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

    /// <summary>Runs the submit and returns the outcome for the page's own <see cref="EditForm"/> handler to route; a blocked submit moves focus to the first error under <see cref="FocusFirstErrorOnInvalidSubmit"/>.</summary>
    /// <returns>The submit's outcome; blocked with an empty summary when a newer submit or load answered in its place, and blocked and empty when the cascaded <see cref="EditContext"/> was replaced, or this component was disposed, during the submit.</returns>
    /// <exception cref="InvalidOperationException">No engine has been built yet.</exception>
    /// <remarks>
    /// A replaced <see cref="EditContext"/> during the submit abandons it, and focus stays. Call it
    /// from the renderer's synchronization context.
    /// </remarks>
    public async Task<SubmitOutcome> ValidateForSubmitAsync()
    {
        var outcome = await RootSubmit.RunAsync(RequireEngine(), () => _engine);
        if (outcome is null)
        {
            // The engine that started this submit is gone, and its answer must not surface as if
            // it were current. Cancelling the abandoned check's token is what usually stops it
            // short, but a validator that does not honour the token can still run to completion,
            // so the empty blocked outcome is returned instead of whatever that check decided.
            return RootSubmit.Superseded;
        }

        if (!outcome.CanProceed && FocusFirstErrorOnInvalidSubmit)
        {
            await FocusFirstErrorAsync();
        }

        return outcome;
    }

    /// <summary>Moves focus to the first visible error, or to the first visible issue when no error shows, and reports whether an element took it.</summary>
    /// <returns><see langword="true"/> when an element took focus; <see langword="false"/> when nothing moved, whether the form shows no issue, no <see cref="IFormidableFocusService"/> is registered, or the field is out of reach.</returns>
    /// <exception cref="InvalidOperationException">No engine has been built yet.</exception>
    /// <remarks>
    /// The move a blocked submit makes, with <see cref="PrepareFocus"/> and
    /// <see cref="FocusFallback"/>, offered to a page that chose the moment: a page that opens a
    /// dialog instead sets <see cref="FocusFirstErrorOnInvalidSubmit"/> to <see langword="false"/>
    /// and calls this when the dialog closes. Call it from the renderer's synchronization context.
    /// </remarks>
    public async Task<bool> FocusFirstErrorAsync() =>
        await FirstErrorFocus.MoveAsync(Services, RequireEngine(), FocusFallback, PrepareFocus);

    /// <summary>Applies a server reply's issues through <see cref="IFormidableEngine.ApplyServerIssues(IEnumerable{ValidationIssue})"/> as the server's current answer; unlike <see cref="FormidableForm{TModel}.ApplyServerIssues(IEnumerable{ValidationIssue})"/>, it moves no focus.</summary>
    /// <param name="issues">The server's current issues; enumerated once.</param>
    /// <exception cref="ArgumentNullException"><paramref name="issues"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">No engine has been built yet.</exception>
    public void ApplyServerIssues(IEnumerable<ValidationIssue> issues) =>
        RequireEngine().ApplyServerIssues(issues);

    /// <summary>Applies a deserialized 400 body by flattening it with <see cref="FormidableValidationProblem.ToIssues"/>, exactly as <see cref="ApplyServerIssues(IEnumerable{ValidationIssue})"/> does.</summary>
    /// <param name="problem">The deserialized response body.</param>
    /// <exception cref="ArgumentNullException"><paramref name="problem"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">No engine has been built yet.</exception>
    public void ApplyServerIssues(FormidableValidationProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);
        RequireEngine().ApplyServerIssues(problem.ToIssues());
    }

    /// <summary>Says what the values already in the model have earned: a field holding a value shows what the submit profile says of it, valid or its message, and a field holding nothing stays silent.</summary>
    /// <param name="cancellationToken">Cancels the check; a cancelled call throws and discloses nothing.</param>
    /// <exception cref="InvalidOperationException">No engine has been built yet.</exception>
    /// <remarks>
    /// The contract is <see cref="IFormidableEngine.DiscloseLoadedValuesAsync"/>'s. Call it once
    /// after filling the model from a saved draft or a loaded record; unlike a blocked submit it
    /// moves no focus. Call it from the renderer's synchronization context.
    /// </remarks>
    // Takes a token where the submit method takes none, for the reason FormidableForm's own
    // overload gives: a submit is UI-event-driven, while this call is data-driven and the caller
    // that filled the model typically holds the token that cancels the fetch.
    public Task DiscloseLoadedValuesAsync(CancellationToken cancellationToken = default) =>
        RequireEngine().DiscloseLoadedValuesAsync(cancellationToken);

    /// <summary>Tells the engine at once that the rendered field set changed, ahead of the reconcile this component runs on its own shortly after the render that changed it.</summary>
    /// <remarks>
    /// Ordinary use never needs it. Call it to read <see cref="Engine"/> synchronously right after
    /// removing a field, from the renderer's synchronization context; with no engine, or no change
    /// after the last reconcile, it does nothing.
    /// </remarks>
    public void NotifyFieldSetChanged() => ReconcileIfChanged();

    /// <summary>Defers the reconcile past the render batch that changed a registration: through the current <see cref="SynchronizationContext"/>, or through the thread pool where none exists.</summary>
    // FieldRegistry.Changed fires synchronously from inside the render batch that changed a
    // registration, so reacting inline here, or through this component's own InvokeAsync (which
    // runs inline whenever the caller is already on the dispatcher, as this call site is), would
    // observe that batch's still-settling state. Two genuinely deferring paths follow, each the
    // standard one on its host: on Blazor Server (and in bUnit, which resolves the same dispatcher
    // shape) a real SynchronizationContext is always current here, and posting to it runs the
    // continuation only once every registration change in the batch has landed; on Blazor
    // WebAssembly's default single-threaded runtime no SynchronizationContext is ever installed,
    // so DeferReconcileAsync is the only branch that host ever takes. Either way the version gate
    // in ReconcileIfChanged collapses however many of these fire in one batch to one reconcile.
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

    /// <summary>Defers <see cref="ReconcileIfChanged"/> through the thread pool on a host with no <see cref="SynchronizationContext"/>, which is WebAssembly's default.</summary>
    // Task.Yield() queues the rest of this method through the thread pool; on a single-threaded
    // host that still means the continuation only runs once the call stack that queued it has
    // unwound all the way back to the browser's own event loop, which restores the same "after
    // the batch" guarantee the Post branch gives on a host that has a context to post through.
    // Fire-and-forget by design: ReconcileIfChanged invokes nothing that runs a consumer's own
    // code (no validator, no user delegate, only the registry's and the engine's bookkeeping), so
    // nothing reachable from here is expected to throw, the same premise the Post branch's own
    // equally unguarded call relies on; a genuine defect surfaces as an unobserved task exception
    // rather than being caught and silently discarded.
    private async Task DeferReconcileAsync()
    {
        await Task.Yield();
        ReconcileIfChanged();
    }

    /// <summary>Runs the engine's field-set reconcile once per registry version, and nothing once the engine is torn down.</summary>
    // Comparing against _lastReconciledFieldSetVersion before latching is what lets several
    // registry changes in one batch, each deferring its own continuation, coalesce to one
    // reconcile: whichever continuation runs first finds the version has moved and does the work,
    // and every later one for the same settled state finds nothing new and skips. The null guard
    // on _engine is also what keeps a continuation still pending when Dispose ran from reconciling
    // against the engine Dispose already tore down; TearDownEngine nulls the field for that.
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

    /// <summary>The engine, or an <see cref="InvalidOperationException"/> saying the call arrived before the component bound to its cascaded <see cref="EditContext"/>.</summary>
    /// <returns>The built engine.</returns>
    /// <exception cref="InvalidOperationException">No engine has been built yet.</exception>
    private FormidableEngine<TModel> RequireEngine() =>
        _engine ?? throw new InvalidOperationException(
            $"{nameof(FormidableValidator<TModel>)} has no engine yet — one is built when it first " +
            "binds to its cascaded EditContext, and this call arrived before that. Capture it with " +
            "@ref and call it from an event handler rather than from a lifecycle method that runs " +
            "ahead of the first render.");

    /// <summary>Renders the cascaded <see cref="FormidableFormContext"/> around <see cref="ChildContent"/>, or nothing at all when there is no engine or no content.</summary>
    /// <param name="builder">The render tree builder.</param>
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

    /// <summary>Tears down the engine and releases the click guard.</summary>
    public void Dispose()
    {
        // Recorded first, so an establish still awaiting its round trip finds this set rather
        // than registering a guard the release below has already gone past.
        _disposed = true;
        TearDownEngine();
        ReleaseClickRecovery();
    }

    /// <summary>Unsubscribes from the engine's registry, disposes the engine and clears the field; a no-op with no engine, and shared by the <see cref="EditContext"/> swap and disposal.</summary>
    private void TearDownEngine()
    {
        if (_engine is null)
        {
            return;
        }

        _engine.Registry.Changed -= OnFieldRegistryChanged;
        _engine.Dispose();
        _engine = null;
    }
}
