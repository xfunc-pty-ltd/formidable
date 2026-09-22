using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace Formidable.Blazor;

/// <summary>
/// The primary Formidable root component. Owns the <see cref="EditContext"/> — swapping the
/// <see cref="Model"/> parameter (e.g. a draft load) rebuilds the context and engine, so
/// consumers never manage EditContext lifecycles. Submit runs the engine pipeline and routes
/// to <see cref="OnValidSubmit"/> / <see cref="OnInvalidSubmit"/>. The form needs an interactive
/// render mode: on a page rendered statically with no interactivity coming, it throws rather than
/// render a form whose submit cannot run.
/// </summary>
/// <typeparam name="TModel">The form model type.</typeparam>
public sealed class FormidableForm<TModel> : ComponentBase, IDisposable
    where TModel : class
{
    private TModel? _boundModel;
    private FormidableOptions? _boundOptions;
    private EditContext? _editContext;
    private FormValidationEngine<TModel>? _engine;
    private FormidableFormContext? _context;
    private string _modelLevelFieldId = string.Empty;
    private bool _renderModeChecked;
    private int _fieldOrderVersion = -1;

    // Tracked apart from _fieldOrderVersion even though both watch the same counter: which fields
    // are on the page and where on it they sit are independent questions, answered by different
    // things (the engine itself; a JS round trip that may not be available at all), and a form
    // that cannot answer the second must still answer the first.
    private int _renderedFieldSetVersion = -1;

    // Distinct from _fieldOrderVersion, which gates whether a resolve is started at all:
    // _resolveStamp arbitrates between resolves that are already in flight together. Latching a
    // new value right before every await keeps the arbitration a plain integer comparison rather
    // than one that has to reason about which of two answers is "newer" — the last resolve to start
    // is, by construction, the last value assigned here, and it is the only one whose result an
    // earlier resolve returning afterward can no longer match.
    private int _resolveStamp;

    // The one thing none of the counters above can carry. A keyed reorder MOVES rendered elements
    // without registering or unregistering anything, so the registry's version — which answers
    // which fields exist, not where they are — does not move either. Only the browser sees it
    // happen, so the browser is what says so, and this is what the render that follows reads.
    private bool _layoutMoved;

    // The observer's own half. The module is this component's rather than a shared service's:
    // FormidableJsModule is written so whoever holds one disposes it, and the form is the only
    // thing that knows when its own element stops existing. The reference into this component is
    // created once and reused, since every observer this form establishes reports to it.
    private FormidableJsModule? _jsModule;
    private DotNetObjectReference<FormidableForm<TModel>>? _layoutObserverReference;

    // Which form element the observer sits on: the context a rebuild replaces, and the element id
    // that rebuild renders. A rebuilt form draws a fresh <form> element — BuildRenderTree keys its
    // region on the context instance — so an observer left where the old one stood would watch a
    // node that is no longer in the document.
    private FormidableFormContext? _observedContext;
    private string _observedFormId = string.Empty;

    private bool _disposed;

    /// <summary>The form model. A reference change rebuilds the EditContext and engine.</summary>
    [Parameter, EditorRequired]
    public TModel Model { get; set; } = default!;

    /// <summary>
    /// Notified when <see cref="ResetAsync(TModel?)"/> is called with a new model — the platform's
    /// own two-way-binding shape, so <c>@bind-Model="_order"</c> works. Binding it is what makes a
    /// programmatic swap durable: <see cref="Model"/> is a <c>[Parameter]</c>, and Blazor re-supplies
    /// a component's parameters from whatever the PARENT still holds on every one of the parent's
    /// own renders — not just this component's — so a swap this component makes to its own copy of
    /// <see cref="Model"/> is silently overwritten the next time anything up there re-renders,
    /// unless the parent's own field was updated too. This callback is that update.
    /// </summary>
    [Parameter]
    public EventCallback<TModel> ModelChanged { get; set; }

    /// <summary>Validator override; resolved from DI when omitted.</summary>
    [Parameter]
    public IModelValidator<TModel>? Validator { get; set; }

    /// <summary>Engine options; defaults apply when omitted.</summary>
    [Parameter]
    public FormidableOptions? Options { get; set; }

    /// <summary>Form content. A <see cref="FormidableFormContext"/> is cascaded to it.</summary>
    [Parameter]
    public RenderFragment? ChildContent { get; set; }

    /// <summary>
    /// Invoked when the submit pipeline passes. A passing submit can still carry advisories, so
    /// the valid branch hands its handler the same <see cref="SubmitOutcome"/> the invalid branch
    /// always received — mirroring <c>EditForm.OnValidSubmit</c>'s own typed precedent — rather
    /// than leave a handler that wants them digging through <see cref="Engine"/> instead. A
    /// parameterless handler still binds: Blazor's own <see cref="EventCallback{TValue}"/>
    /// conversion accepts an <see cref="Action"/> or <see cref="Func{TResult}"/> wherever a typed
    /// callback is declared, exactly as it does for <c>EditForm.OnValidSubmit</c> today.
    /// </summary>
    [Parameter]
    public EventCallback<SubmitOutcome> OnValidSubmit { get; set; }

    /// <summary>Invoked with the outcome when the submit pipeline blocks.</summary>
    [Parameter]
    public EventCallback<SubmitOutcome> OnInvalidSubmit { get; set; }

    /// <summary>
    /// On a blocked submit, best-effort auto-focuses the field carrying the first error among the
    /// form's visible issues via <see cref="IFormidableFocusService"/>, immediately after
    /// <see cref="OnInvalidSubmit"/> runs — the first error rather than merely the first issue,
    /// since a field above the failing one can carry nothing worse than an advisory, and landing
    /// there would bury the reason the submit blocked. First means topmost: issue order follows the
    /// page itself once <see cref="IFormidableFieldOrderService"/> has resolved it. The fallback to
    /// the first visible issue of any severity applies only when a blocked submit shows no error at
    /// all. This also gates the focus move
    /// <see cref="ApplyServerIssues(IEnumerable{ValidationIssue})"/> makes when the payload it
    /// applies carries an error. Default <see langword="true"/>. The service is resolved lazily and
    /// may be unregistered; a null service is silent. A focus miss (e.g. no element carries the
    /// field's id yet) retries once through <see cref="FocusFallback"/> when one is wired, and
    /// otherwise reports a diagnostic instead of the summary's silent default: a blocked submit's
    /// visitor has nowhere else to land, where <see cref="FormidableSummary"/>'s own click just
    /// leaves the click without effect. Set <see langword="false"/> to choose focus yourself, e.g.
    /// from <see cref="OnInvalidSubmit"/>.
    /// </summary>
    [Parameter]
    public bool FocusFirstErrorOnInvalidSubmit { get; set; } = true;

    /// <summary>
    /// Invoked once when a blocked submit's auto-focused first error has no rendered element to
    /// focus (e.g. a virtualized row outside the render window). Return <c>true</c> after making
    /// the element renderable (scrolling its container, expanding a section) and the focus is
    /// retried exactly once; return <c>false</c> to leave the miss as-is. Same delegate shape as
    /// <see cref="FormidableSummary.FocusFallback"/> — a page wiring both typically passes the same
    /// callback to each. When unset, a miss reports a diagnostic instead of the summary's silent
    /// default: a form's blocked submit has nowhere else for the visitor to land, where the
    /// summary's own click just leaves the click without effect.
    /// </summary>
    [Parameter]
    public Func<FieldIdentifier, ValueTask<bool>>? FocusFallback { get; set; }

    /// <summary>
    /// Additional attributes splatted onto the rendered form element. A consumer-supplied
    /// <c>id</c> or <c>tabindex</c> is ignored: the rendered <c>id</c> is always the deterministic
    /// <see cref="FormidableFieldId"/> for the model-level field, and <c>tabindex="-1"</c> keeps it
    /// focusable for the all-suppressed gate's summary entry — the same override policy the kit's
    /// inputs apply to their own <c>id</c>.
    /// </summary>
    [Parameter(CaptureUnmatchedValues = true)]
    public IReadOnlyDictionary<string, object>? AdditionalAttributes { get; set; }

    [Inject]
    private IServiceProvider Services { get; set; } = default!;

    /// <summary>The engine view (also cascaded via the form context).</summary>
    public IFormValidationEngine? Engine => _engine;

    /// <inheritdoc />
    protected override void OnParametersSet()
    {
        VerifyInteractiveRenderMode();

        if (Model is null)
        {
            throw new InvalidOperationException(
                $"{nameof(FormidableForm<TModel>)} requires a Model parameter (none was supplied) — set Model " +
                "to the object being edited, e.g. <FormidableForm Model=\"_order\">.");
        }

        if (!ReferenceEquals(_boundModel, Model))
        {
            RebuildEngine(Model);
        }
        else
        {
            FormidableEngineFactory.VerifyOptionsUnchanged(
                nameof(FormidableForm<TModel>),
                _boundOptions,
                Options,
                "swap the Model parameter alongside Options to rebuild the engine");
        }
    }

    /// <summary>
    /// Reports a moved rendered field set to the engine, and resolves where the form's fields
    /// actually sit on the page.
    /// The first is what keeps a submitted form from reporting verdicts about fields that are no
    /// longer on it: the registry is the only record that they left, and it raises no event, so
    /// this form's own next render is where the engine hears about it — see
    /// <see cref="FormValidationEngine{TModel}.OnRenderedFieldsChanged"/> for what it does with
    /// that. Its reach is this component's render, exactly as the ordering resolve's is: a
    /// nested component re-rendering on state of its own moves the registry without bringing the
    /// form here, so the move is picked up by whichever of the form's renders comes next. It
    /// happens ahead of everything below and on a tracker of its own, since a form with no
    /// <see cref="IFormidableFieldOrderService"/> to consult still has fields that can leave.
    /// The second hands the engine a reading order,
    /// so a blocked submit reports its issues — and focuses the first error among them — in the
    /// order a visitor reads the form, rather than the order the validator declares its rules.
    /// The request is not only the registered fields: the model-level field the form's own
    /// <c>&lt;form&gt;</c> element carries rides along too, so a gate or fault issue sorts among
    /// the rest instead of always trailing them.
    /// The browser is the only source for this, so it is answered by
    /// <see cref="IFormidableFieldOrderService"/> after the render that produced the elements.
    /// The registry's version is most of what decides whether there is anything to re-resolve: it
    /// moves only when a field registers or unregisters, so every later render stops at that check.
    /// It cannot be all of it, because it answers which fields exist and not where they are — a
    /// keyed reorder moves rendered elements without a single registration changing. What sees that
    /// happen is a browser-side observer over the form's own subtree, which reports it through
    /// <see cref="NotifyLayoutMoved"/>, and a render following one re-resolves on a version that
    /// has not moved. The observer is a private signal between this component and the library's own
    /// script, not part of <see cref="IFormidableFieldOrderService"/>: an implementation of that
    /// interface answers a question and is never asked to notice anything.
    /// A resolve that cannot be had — no service registered, or an interop boundary that is
    /// gone, disconnected or never loaded — leaves the engine on whatever order it already had
    /// (validator order, until a resolve has landed) rather than failing a render, and a failed one
    /// clears the version guard so the next render tries again. That tolerance is deliberately
    /// wider than the focus service's, which swallows nothing: an ordering pass runs on every
    /// render rather than on a click, so an exception escaping here would take the form down
    /// instead of costing one focus move. It does not extend to a consumer's own
    /// <see cref="IFormidableFieldOrderService"/> throwing something else, which surfaces.
    /// </summary>
    /// <param name="firstRender">Whether this is the component's first render.</param>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_engine is null)
        {
            return;
        }

        var version = _engine.Registry.Version;

        if (version != _renderedFieldSetVersion)
        {
            // Ahead of both gates below, and on its own tracker. A field leaving the page is the
            // engine's business whether or not the page can also say where the remaining ones
            // sit, so sharing a gate with the ordering resolve would leave staleness unhandled on
            // every form with no IFormidableFieldOrderService registered. Nothing else announces
            // the move: removing a collection row mutates the model without raising a field
            // change, and the render that follows would otherwise redraw the verdict about a row
            // that is gone.
            //
            // Recorded only once the call has returned: a throw leaves the tracker behind the
            // registry so the next render tries again, the same retry the ordering path below
            // reaches by clearing its own guard.
            _engine.OnRenderedFieldsChanged();
            _renderedFieldSetVersion = version;
        }

        if (version == _fieldOrderVersion && !_layoutMoved)
        {
            return;
        }

        var orderService = Services.GetService<IFormidableFieldOrderService>();
        if (orderService is null)
        {
            return;
        }

        // Cleared before anything below is awaited, for the same reason the version is latched
        // before the resolve starts: a move reported while this pass is in flight describes a page
        // the answer already being awaited cannot account for, so it has to survive as a set flag
        // for the next render to act on rather than be wiped by this pass finishing.
        _layoutMoved = false;
        _fieldOrderVersion = version;

        await EstablishLayoutObserverAsync();

        // The model-level field's id rides on this form's own <form> element, which contains
        // every field in it — so document order puts it first, which is where a verdict about
        // the form as a whole belongs. It is asked about like any other: an element that is not
        // there is simply not in the answer. The form never puts it in the registry itself (the
        // id is written straight onto the <form> element, not through FieldRegistry.Register),
        // but nothing stops a consumer's own field component from targeting the same identifier,
        // so the check is against the request being built, not the registry: asked about twice, it
        // would take whichever of its two positions the answer listed last rather than the one the
        // page puts it in. Adding it is also what leaves the request never empty, whatever the
        // registry holds.
        var fields = _engine.Registry.RevealedFields.ToList();
        if (!fields.Contains(_engine.ModelLevelField))
        {
            fields.Add(_engine.ModelLevelField);
        }

        // Latched before the await starts, same as the version above it: whichever resolve reaches
        // this line last is the one whose stamp survives, so a resolve that returns to find its own
        // stamp no longer current knows — without needing to compare maps or timestamps — that a
        // resolve started after it already had its answer applied.
        var resolveStamp = ++_resolveStamp;

        IReadOnlyList<FieldIdentifier>? ordered;
        try
        {
            ordered = await orderService.OrderAsync(fields);
        }
        catch (Exception exception) when (IsInteropFailure(exception))
        {
            // Ordering is presentation: an interop boundary that is gone, disconnected, or never
            // loaded costs the page the reading order it would have had, and nothing else. Keeping
            // the order already in force keeps the form working, where letting this escape a
            // lifecycle method would take the whole component down with it. Only the interop
            // family is caught — a consumer implementation failing on its own terms is a bug of
            // theirs to see, not one for this to hide.
            _fieldOrderVersion = -1;
            return;
        }

        if (ordered is null)
        {
            // No order could be resolved at all, which is not the same answer as a page that
            // placed none of these fields — so it is retried rather than taken as an order.
            _fieldOrderVersion = -1;
            return;
        }

        if (resolveStamp != _resolveStamp)
        {
            // A resolve that started after this one already ran to completion and applied its own
            // map while this one was still in flight — this answer is real, just outdated, and
            // installing it now would overwrite a newer map with an older one, permanently, since
            // nothing would be left to retry it. Discarded rather than applied; the newer map stays
            // in force.
            return;
        }

        if (_engine.Options.OrderIssues is { } reorder)
        {
            ordered = ApplyOrderDelegate(ordered, reorder);
        }

        var order = new Dictionary<FieldIdentifier, int>();
        for (var i = 0; i < ordered.Count; i++)
        {
            order[ordered[i]] = i;
        }

        // A render follows only if this answer differs from the order already in force — the engine
        // decides that, and stays silent when it does not. It has to be the engine's call rather
        // than this method's: a resolve lands after the render that produced the elements it
        // measured, so a changed order has nothing else to arrive on. A move that registers nothing
        // raises no pass, no edit and no server apply for it to ride.
        _engine.SetFieldOrder(order);
    }

    /// <summary>
    /// Interop callback for the library's own script: the browser reporting that the form's
    /// rendered elements moved, so the reading order resolved for them no longer describes the
    /// page. Public only because <see cref="JSInvokableAttribute"/> requires it — it is not part of
    /// the consumer-facing surface and nothing outside the library has any reason to call it.
    /// </summary>
    /// <remarks>
    /// All it does is record the move and provoke a render, which is where
    /// <see cref="OnAfterRenderAsync"/> picks it up: the resolve needs a rendered page to measure,
    /// and this is called from a browser callback rather than the renderer's own loop.
    /// </remarks>
    /// <returns>A task completing once the render this provokes has been dispatched.</returns>
    [JSInvokable]
    public async Task NotifyLayoutMoved()
    {
        if (_disposed)
        {
            return;
        }

        _layoutMoved = true;

        try
        {
            await InvokeAsync(StateHasChanged);
        }
        catch (Exception exception) when (
            exception is ObjectDisposedException or OperationCanceledException)
        {
            // The check above covers a form already torn down when the browser called; it cannot
            // cover one torn down between that check and this dispatch, which is a race no check
            // can close and only a catch can answer. Awaited rather than discarded so the failure
            // is answered here instead of surfacing later as an unobserved task exception, and
            // there is nothing to answer it with: a form on its way out has no reading order left
            // to re-resolve.
        }
    }

    /// <summary>
    /// Puts the browser-side layout observer on the form element the current context renders,
    /// moving it off whichever element it stood on before. Cheap to call on every ordering pass:
    /// it does nothing at all while the context it was established for is still the one rendering.
    /// Best-effort throughout: a form with no observer re-resolves its order when a field
    /// registers or unregisters and not when the page merely moves the existing ones, which is
    /// what every host with no JavaScript already does.
    /// </summary>
    /// <remarks>
    /// The observer cannot feed itself. It reports DOM changes, and resolving an order only reads
    /// where elements sit — nothing here writes to the page — so the render a report provokes
    /// either changes the DOM because the order genuinely changed, which the following pass finds
    /// settled, or changes nothing and produces no records to report.
    /// </remarks>
    private async Task EstablishLayoutObserverAsync()
    {
        if (ReferenceEquals(_observedContext, _context))
        {
            return;
        }

        var jsRuntime = Services.GetService<IJSRuntime>();
        if (jsRuntime is null)
        {
            return;
        }

        _jsModule ??= new FormidableJsModule(jsRuntime);
        _layoutObserverReference ??= DotNetObjectReference.Create(this);

        var observing = _observedFormId;
        _observedFormId = string.Empty;
        _observedContext = null;

        try
        {
            if (observing.Length > 0)
            {
                await _jsModule.InvokeVoidAsync("disconnectLayoutObserver", observing);
            }

            await _jsModule.InvokeVoidAsync(
                "observeLayout", _modelLevelFieldId, _layoutObserverReference);
        }
        catch
        {
            // Prerender, attach mode, a host carrying no script at all, a test double standing in
            // for the module: with no browser to watch the layout, none is watched, and the
            // registry's version goes back to being the only thing that re-resolves an order —
            // the documented fallback, costing the page re-ordering when the DOM moves under it
            // and nothing else. That is the same bargain the ordering resolve's own catch strikes,
            // and it is why this one is written wide open where that one names the interop family
            // exactly: behind this call is the library's own script, reached through the library's
            // own module, with no consumer code anywhere in it. There is no implementation bug to
            // preserve for someone to see, so letting anything at all out of a lifecycle method
            // here would take a working form down over a feature it can do without.
            //
            // Nothing is recorded as observed, so the next render to reach this method establishes
            // the observer again. That is not the same as a retry: this method is reached only
            // once the gate above has opened, and with no observer the only thing that opens it is
            // a registration change. A form whose registered set never moves stays unobserved
            // after a failure here — which is exactly the JS-less fallback, arrived at from a
            // different direction. The version guard is deliberately not cleared to force the
            // matter: on a host where this can never succeed, clearing it would buy a re-resolve
            // on every single render, forever, for an observer that is never going to establish.
            return;
        }

        _observedFormId = _modelLevelFieldId;
        _observedContext = _context;
    }

    /// <summary>
    /// Whether an exception from the order service is the interop boundary failing rather than the
    /// implementation behind it: the JS side throwing or the runtime being unreachable
    /// (<see cref="JSException"/>, <see cref="JSDisconnectedException"/>), the runtime already
    /// disposed, or the call cancelled — an interop timeout on a server circuit arrives as the
    /// last of those.
    /// </summary>
    private static bool IsInteropFailure(Exception exception) =>
        exception is JSException or JSDisconnectedException or ObjectDisposedException
            or OperationCanceledException;

    /// <summary>
    /// Runs the consumer's re-sort over the resolved document order, then appends anything it left
    /// out, in the order it was given them. A delegate reorders; it does not decide what is
    /// reported, so a field missing from its answer keeps its place at the end rather than losing
    /// its issues. Internal rather than private so the append guarantee is testable directly,
    /// without a render.
    /// </summary>
    /// <remarks>
    /// A delegate's answer can only ever be a permutation of what it was handed, never a
    /// substitute for it: a field repeated in <paramref name="reorder"/>'s result keeps only its
    /// first position, and a field that was never in <paramref name="ordered"/> is dropped from
    /// the answer rather than kept — otherwise a delegate padding its result out to the same
    /// length with fields of its own would satisfy the "nothing missing" check while silently
    /// pushing real fields out of the map.
    /// </remarks>
    internal static IReadOnlyList<FieldIdentifier> ApplyOrderDelegate(
        IReadOnlyList<FieldIdentifier> ordered,
        Func<IReadOnlyList<FieldIdentifier>, IReadOnlyList<FieldIdentifier>> reorder)
    {
        var reordered = reorder(ordered);
        if (reordered is null)
        {
            return ordered;
        }

        var candidates = new HashSet<FieldIdentifier>(ordered);
        var seen = new HashSet<FieldIdentifier>();
        var result = new List<FieldIdentifier>(ordered.Count);
        foreach (var field in reordered)
        {
            if (candidates.Contains(field) && seen.Add(field))
            {
                result.Add(field);
            }
        }

        if (seen.Count < ordered.Count)
        {
            foreach (var field in ordered)
            {
                if (seen.Add(field))
                {
                    result.Add(field);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Disposes the current engine, if any, and builds a fresh engine and <c>EditContext</c> over
    /// <paramref name="model"/> — including a brand new <see cref="FormidableFormContext"/>
    /// instance, not merely a new engine reference inside the old one. That is what
    /// <see cref="BuildRenderTree"/>'s region key relies on: a new context instance is what turns
    /// the swap into a fresh mount for every descendant, rather than leaving already-bound
    /// components pointed at whatever the old context still refers to. Shared by the
    /// parameter-driven Model swap in <see cref="OnParametersSet"/> and by <see cref="ResetAsync"/>.
    /// </summary>
    private void RebuildEngine(TModel model)
    {
        _engine?.Dispose();
        _boundModel = model;
        _boundOptions = Options;
        _editContext = new EditContext(model);
        _engine = FormidableEngineFactory.Create(
            model,
            _editContext,
            Services,
            Validator,
            Options,
            renderDispatch: work => InvokeAsync(work));
        _context = new FormidableFormContext(_engine);
        _modelLevelFieldId = FormidableFieldId.For(_engine.ModelLevelField);

        // The new engine has its own registry, whose version starts over — and its own fields to
        // locate, since the swapped-in model's identifiers are not the old ones. Both trackers
        // reset, or the old registry's count could happen to match the new one's and the first
        // render after a swap would take itself for a render with nothing to do.
        _fieldOrderVersion = -1;
        _renderedFieldSetVersion = -1;
    }

    /// <summary>
    /// Returns the form to pristine. Omitted <paramref name="newModel"/>: rebuilds the engine and
    /// <c>EditContext</c> over the SAME model instance currently bound, without waiting for a
    /// parameter-driven swap to do it. Touched/modified state, the message store, the advisory
    /// buckets and <see cref="IFormValidationEngine.HasSubmitted"/> all clear, and any pending
    /// refresh is cancelled — none of it survives the engine it belonged to. An in-flight
    /// <see cref="SubmitAsync"/> is abandoned along with it: its callbacks never fire once the
    /// engine that started it is gone (see <see cref="SubmitAsync"/>'s own remarks).
    /// Supplied: swaps to the new instance — but doing that from inside this component is not
    /// enough on its own to make the swap stick, because <see cref="Model"/> is a
    /// <c>[Parameter]</c> and Blazor re-supplies it from whatever the PARENT still holds on every
    /// one of the parent's own renders. This call also invokes <see cref="ModelChanged"/>, so a
    /// parent bound with <c>@bind-Model</c> updates its own field before that can happen — which is
    /// why supplying <paramref name="newModel"/> with no <see cref="ModelChanged"/> delegate bound
    /// throws instead of swapping to a state the very next unrelated render could silently revert.
    /// Call from the renderer's synchronization context (a Blazor event handler or
    /// <c>InvokeAsync</c>) — it triggers renders.
    /// </summary>
    /// <param name="newModel">
    /// The model to bind instead — requires <see cref="ModelChanged"/> to be bound — or null to
    /// rebuild over the current one.
    /// </param>
    public async Task ResetAsync(TModel? newModel = null)
    {
        RequireEngine();

        if (newModel is null)
        {
            RebuildEngine(Model);
            StateHasChanged();
            return;
        }

        if (!ModelChanged.HasDelegate)
        {
            throw new InvalidOperationException(
                $"{nameof(FormidableForm<TModel>)}.{nameof(ResetAsync)} was called with a new model, but no " +
                $"{nameof(ModelChanged)} delegate is bound — a swap this component makes to its own copy of " +
                $"{nameof(Model)} cannot survive the parent's next render on its own: Blazor re-supplies the " +
                $"parent's own Model value on every one of THAT component's renders, not just this one, silently " +
                "reverting the swap the next time anything up there re-renders. Bind with " +
                "<FormidableForm @bind-Model=\"_order\"> (this enables ModelChanged automatically), or swap the " +
                "Model parameter from the parent instead of calling ResetAsync with a new model.");
        }

        Model = newModel;
        RebuildEngine(newModel);
        StateHasChanged();
        await ModelChanged.InvokeAsync(newModel);
    }

    /// <summary>
    /// Runs the submit pipeline programmatically. Call from the renderer's synchronization
    /// context (a Blazor event handler or <c>InvokeAsync</c>) — it triggers renders. If
    /// <see cref="ResetAsync(TModel?)"/> rebuilds the engine while this call is still awaiting the
    /// pipeline, the engine that started is gone by the time the verdict lands: neither
    /// <see cref="OnValidSubmit"/> nor <see cref="OnInvalidSubmit"/> fires, focus is not moved, and
    /// no render is triggered — a dead engine's verdict, from a submit the reset already abandoned,
    /// must not surface as if it were current. That includes the return value: cancelling the
    /// abandoned pass's token is what usually stops it short, but a validator that does not honour
    /// the token can still run to completion, so the blocked <see cref="SubmitOutcome"/> below is
    /// returned instead of whatever that pass actually decided.
    /// </summary>
    public async Task<SubmitOutcome> SubmitAsync()
    {
        var engine = RequireEngine();
        var outcome = await engine.ValidateForSubmitAsync();

        if (!ReferenceEquals(_engine, engine))
        {
            // The dead engine's own verdict — even a passing one, if its validator outran
            // cancellation — must not surface as current; mirrors FormValidationEngine's own
            // precedent for a superseded pass with nothing to report.
            return new SubmitOutcome(false, ValidationReport.Empty, []);
        }

        if (outcome.CanProceed)
        {
            await OnValidSubmit.InvokeAsync(outcome);
        }
        else
        {
            await OnInvalidSubmit.InvokeAsync(outcome);
            if (FocusFirstErrorOnInvalidSubmit)
            {
                await FocusFirstErrorAsync();
            }
        }

        StateHasChanged();
        return outcome;
    }

    /// <summary>
    /// Best-effort: a consumer who never registered <see cref="IFormidableFocusService"/> (or whose
    /// form has, unusually, no visible issue to focus right after a blocked submit) gets silence
    /// rather than an exception here — the same tolerance <see cref="FormidableSummary"/>'s own
    /// click-to-focus applies.
    /// </summary>
    /// <remarks>
    /// The first ERROR in document order, not merely the first issue: a field high on the page can
    /// carry an advisory while the thing actually blocking the submit sits below it, and taking the
    /// visitor to the advisory would both bury the reason and disagree with
    /// <see cref="FormidableSummary"/>, which regroups by severity and so leads with the error
    /// regardless. The fallback to the first visible issue covers the one way a blocked submit
    /// reaches this call with no error to find: superseded by a second submit before its own
    /// verdict landed, it reports blocked without writing one, leaving whatever preceded it on
    /// screen. Everything else that blocks is error-severity — the all-suppressed gate's
    /// form-level issue and the incomplete-validation fault issue included. A miss on the element
    /// itself (the field has no rendered element yet, e.g. a virtualized row outside the render
    /// window) is handled the same way <see cref="FormidableSummary.FocusFallback"/> handles a
    /// click miss: try, fall back once when <see cref="FocusFallback"/> is wired, retry once. With
    /// no fallback wired, the miss reports a diagnostic instead — see
    /// <see cref="ReportFocusFallbackMiss"/> — since a blocked submit's visitor otherwise gets no
    /// signal at all that the field they need is off-screen.
    /// </remarks>
    private async Task FocusFirstErrorAsync()
    {
        var focusService = Services.GetService<IFormidableFocusService>();
        if (focusService is null)
        {
            return;
        }

        var issues = RequireEngine().GetVisibleIssues();
        var firstIssue = issues.FirstOrDefault(v => v.Issue.Severity == ValidationSeverity.Error)
            ?? issues.FirstOrDefault();
        if (firstIssue is null)
        {
            return;
        }

        if (await focusService.FocusAsync(firstIssue.Field))
        {
            return;
        }

        if (FocusFallback is null)
        {
            ReportFocusFallbackMiss(firstIssue.Issue);
            return;
        }

        if (!await FocusFallback(firstIssue.Field))
        {
            return;
        }

        await focusService.FocusAsync(firstIssue.Field);
    }

    /// <summary>
    /// The one report a focus miss with no <see cref="FocusFallback"/> to retry through gets: a
    /// Trace line for a debugger, and a logged warning when the host resolved an
    /// <see cref="ILoggerFactory"/> — mirrors <c>FormValidationEngine.ReportSuppressed</c>'s dual
    /// channel, minus the options-callback channel that has no analogue here. Names
    /// <see cref="FocusFallback"/> so a consumer's console points straight at the seam that would
    /// close the gap.
    /// </summary>
    /// <param name="issue">The unfocusable first error.</param>
    private void ReportFocusFallbackMiss(ValidationIssue issue)
    {
        System.Diagnostics.Trace.WriteLine(
            $"Formidable: the blocked submit's first error at '{issue.Path}' has no rendered element to " +
            $"focus, and no {nameof(FocusFallback)} is wired to make it renderable.");
        ((ILoggerFactory?)Services.GetService(typeof(ILoggerFactory)))?.CreateLogger("Formidable").LogWarning(
            "Formidable: the blocked submit's first error at '{Path}' has no rendered element to focus, " +
            "and no {Parameter} is wired to make it renderable.",
            issue.Path, nameof(FocusFallback));
    }

    /// <summary>
    /// Best-effort focus after a server-applied verdict, gated on
    /// <see cref="FocusFirstErrorOnInvalidSubmit"/> AND on <paramref name="appliedIssues"/>
    /// actually carrying an error: a rejected round trip is a blocked submit that arrived late,
    /// so it lands the user on the first error the same way <see cref="SubmitAsync"/>
    /// would — but a clean or advisory-only apply rejected nothing, so it must not steal focus
    /// onto some unrelated issue still visible from an earlier submit. Fire-and-forget on
    /// purpose — both <c>ApplyServerIssues</c> overloads stay synchronous, so focus cannot make
    /// applying issues asynchronous — and an interop failure (see <see cref="IsInteropFailure"/>,
    /// which covers a disconnected circuit as well as a thrown or unreachable JS boundary) from
    /// that unawaited call is swallowed here rather than left to become an unobserved task
    /// exception.
    /// </summary>
    /// <param name="appliedIssues">The issues this apply just carried.</param>
    private void FocusAfterServerIssues(IReadOnlyCollection<ValidationIssue> appliedIssues)
    {
        if (!FocusFirstErrorOnInvalidSubmit
            || !appliedIssues.Any(issue => issue.Severity == ValidationSeverity.Error))
        {
            return;
        }

        // Fire-and-forget: focus is best-effort and must not make applying issues asynchronous.
        _ = FocusQuietlyAsync();

        async Task FocusQuietlyAsync()
        {
            try
            {
                await FocusFirstErrorAsync();
            }
            catch (Exception exception) when (IsInteropFailure(exception))
            {
            }
        }
    }

    /// <summary>
    /// Applies a server response's issues to this form's engine, forwarding
    /// <see cref="IFormValidationEngine.ApplyServerIssues(IEnumerable{ValidationIssue})"/> and its
    /// contract whole: the payload is the server's current verdict and replaces what the previous
    /// call applied, each issue lands at the severity it carries, and applying any also sets
    /// <see cref="IFormValidationEngine.HasSubmitted"/>, since the payload is treated as a submit
    /// result. A rejected round trip is a blocked submit that arrived late: when
    /// <see cref="FocusFirstErrorOnInvalidSubmit"/> is <see langword="true"/> and this apply
    /// carries at least one error, applying also focuses the first error on the page —
    /// not necessarily the one just applied — the same way a blocked client submit does. A clean
    /// or advisory-only apply (an accepted resubmission, say) moves nothing: nothing about THIS
    /// apply was rejected, even if an earlier one left something else on the page still visible.
    /// Call <see cref="IFormValidationEngine.ApplyServerIssues(IEnumerable{ValidationIssue})"/>
    /// via <see cref="Engine"/> instead for a background apply that must stay quiet — the
    /// engine-level method never moves focus. A page holding the form with <c>@ref</c> has
    /// everything the round trip needs here, without reaching through <see cref="Engine"/> for
    /// it. Call from the renderer's synchronization context (a Blazor event handler or
    /// <c>InvokeAsync</c>) — it mutates validation state and triggers renders.
    /// </summary>
    /// <param name="issues">The server's current verdict. Enumerated exactly once.</param>
    public void ApplyServerIssues(IEnumerable<ValidationIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(issues);
        var issueList = issues as IReadOnlyList<ValidationIssue> ?? issues.ToList();
        RequireEngine().ApplyServerIssues(issueList);
        FocusAfterServerIssues(issueList);
    }

    /// <summary>
    /// Applies a deserialized validation ProblemDetails body — the shape an HTTP 400 from
    /// Formidable.AspNetCore arrives in — by flattening it with
    /// <see cref="FormidableValidationProblem.ToIssues"/>. Equivalent to the sequence overload in
    /// every respect, including the <see cref="IFormValidationEngine.HasSubmitted"/> side effect
    /// and the error-gated focus after applying — call
    /// <see cref="IFormValidationEngine.ApplyServerIssues(IEnumerable{ValidationIssue})"/> via
    /// <see cref="Engine"/> instead for a quiet background apply; this is the whole client half of
    /// the round trip in one call.
    /// </summary>
    /// <param name="problem">The deserialized response body.</param>
    public void ApplyServerIssues(FormidableValidationProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);
        var issues = problem.ToIssues();
        RequireEngine().ApplyServerIssues(issues);
        FocusAfterServerIssues(issues);
    }

    /// <summary>
    /// The engine, or the reason there is not one yet. It is built on the form's first parameter
    /// set, so every entry point that runs the pipeline has to answer for a call that beats the
    /// first render rather than let it surface from inside the component as a null reference.
    /// </summary>
    private FormValidationEngine<TModel> RequireEngine() =>
        _engine ?? throw new InvalidOperationException(
            $"{nameof(FormidableForm<TModel>)} has no engine yet — one is built when the form first " +
            "renders, and this call arrived before that. Capture the form with @ref and call it from " +
            "an event handler (OnValidSubmit, a button's onclick) rather than from a lifecycle " +
            "method that runs ahead of the first render.");

    /// <summary>
    /// Refuses to render on a page that is statically rendered with no interactivity coming. Such a
    /// page renders the form perfectly and then answers its first submit with the platform's own
    /// "the POST request does not specify which form is being submitted" 400 — whose advice, pass a
    /// FormName to EditForm, is not something any FormidableForm parameter can carry.
    /// </summary>
    private void VerifyInteractiveRenderMode()
    {
        if (_renderModeChecked)
        {
            return;
        }

        _renderModeChecked = true;

        // An assigned render mode means interactivity is coming, including on the static PRERENDER
        // pass of an interactive component — which reports exactly the same non-interactive
        // renderer as the dead end below. That is why neither signal decides this alone.
        if (AssignedRenderMode is null && RendererDeclaresItselfStatic())
        {
            throw new InvalidOperationException(
                $"{nameof(FormidableForm<TModel>)} requires an interactive render mode: this page is " +
                "rendered statically and no interactivity is coming, so submitting the form would post " +
                "back to the server instead of running the validation pipeline. Add a render mode to " +
                "the page or component — @rendermode InteractiveServer or @rendermode " +
                "InteractiveWebAssembly — or host the form in a standalone WebAssembly app, where " +
                "every page is interactive already.");
        }
    }

    /// <summary>
    /// True only when the renderer says outright that it is not interactive. A renderer that
    /// declines to describe itself has said nothing, and nothing is not proof of a dead end: bUnit's
    /// test renderer throws from <c>RendererInfo</c> unless a test declares one, and rendering a
    /// form in a component test must not depend on having declared it.
    /// </summary>
    private bool RendererDeclaresItselfStatic()
    {
        try
        {
            return !RendererInfo.IsInteractive;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <inheritdoc />
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        // Keys the cascade below on _context's own identity (the same idiom EditForm itself uses
        // for EditContext) — see the trailing comment for why this region exists.
        builder.OpenRegion(_context!.GetHashCode());
        builder.OpenComponent<CascadingValue<FormidableFormContext>>(0);
        builder.AddComponentParameter(1, "IsFixed", true);
        builder.AddComponentParameter(2, "Value", _context);
        builder.AddComponentParameter(3, "ChildContent", (RenderFragment)(inner =>
        {
            inner.OpenComponent<EditForm>(0);
            inner.AddComponentParameter(1, nameof(EditForm.EditContext), _editContext);
            inner.AddComponentParameter(2, nameof(EditForm.OnSubmit), EventCallback.Factory.Create<EditContext>(this, _ => SubmitAsync()));
            if (AdditionalAttributes is not null)
            {
                inner.AddMultipleAttributes(3, AdditionalAttributes!);
            }
            // Rendered after the splat, so they win the duplicate-attribute race: the all-suppressed
            // gate's summary entry addresses the form by this id (see FormidableFieldId), and a
            // consumer-supplied id or tabindex would break that the same way a consumer-supplied
            // input id would — see FormidableInputBase<TValue>'s identical policy.
            inner.AddAttribute(4, "id", _modelLevelFieldId);
            inner.AddAttribute(5, "tabindex", "-1");
            inner.AddComponentParameter(6, nameof(EditForm.ChildContent), (RenderFragment<EditContext>)(_ => ChildContent ?? (_ => { })));
            inner.CloseComponent();
        }));
        builder.CloseComponent();
        builder.CloseRegion();
        // IsFixed: a non-fixed CascadingValue re-supplies every subscriber's parameters from a
        // snapshot of the parent's PREVIOUS render on every re-render of this component, even
        // though FormidableFormContext is the same instance — that stale re-supply is what let a
        // kit input's own just-committed Value be overwritten with the value it held a moment
        // earlier (a caret jump in text, a wiped segment in a date input). Losing that
        // notification costs nothing an input needs: ongoing state (touched, validating, errors)
        // never travels through the cascade at all, fixed or not — it travels through
        // Engine.StateChanged, which every kit input subscribes to directly (see
        // FormContextBinding). The cascade's only remaining job is handing a descendant ITS OWN
        // reference to the context once, at mount. The region above turns a Model swap into
        // exactly that kind of mount for every descendant: it keys this cascade on _context's own
        // identity, so a swap destroys this component — not merely what renders below it — and a
        // fresh instance takes its place, whose subscribers are therefore all newly mounted and
        // read the swapped-in Value on their own first render, with no notification to miss.
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // Recorded first: a move reported between here and the observer actually going away would
        // otherwise ask a component that no longer exists to render.
        _disposed = true;
        _engine?.Dispose();
        ReleaseLayoutObserver();
    }

    /// <summary>
    /// Tears down the browser-side observer and the reference it reports through.
    /// </summary>
    /// <remarks>
    /// Disconnecting is worth doing on its own account: the script module the observer lives in
    /// outlives this component — a module is loaded once per document and stays — so an observer
    /// left connected goes on watching a detached form and goes on holding the reference that
    /// reaches back into a component nobody else can see. Disposal here is synchronous and the
    /// disconnect is not, so the reference is released once the round trip still naming it has
    /// returned rather than while it is in flight; an interop boundary that is already gone
    /// releases it just the same, and has taken the observer with it anyway.
    /// </remarks>
    private void ReleaseLayoutObserver()
    {
        var module = _jsModule;
        var reference = _layoutObserverReference;
        var observing = _observedFormId;

        _jsModule = null;
        _layoutObserverReference = null;
        _observedFormId = string.Empty;
        _observedContext = null;

        if (module is null)
        {
            reference?.Dispose();
            return;
        }

        _ = DisconnectLayoutObserverAsync(module, reference, observing);
    }

    private static async Task DisconnectLayoutObserverAsync(
        FormidableJsModule module,
        DotNetObjectReference<FormidableForm<TModel>>? reference,
        string observing)
    {
        try
        {
            if (observing.Length > 0)
            {
                await module.InvokeVoidAsync("disconnectLayoutObserver", observing);
            }

            await module.DisposeAsync();
        }
        catch (Exception exception) when (IsInteropFailure(exception))
        {
            // A boundary that is gone, disconnected or never loaded has nothing left holding the
            // observer, and a component being torn down is no place to raise that as a failure.
        }
        finally
        {
            reference?.Dispose();
        }
    }
}
