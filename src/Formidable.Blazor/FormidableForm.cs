using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace Formidable.Blazor;

/// <summary>The form root: it owns the <see cref="EditContext"/>, renders the <c>&lt;form&gt;</c>, runs the submit, and rebuilds itself when <see cref="Model"/> is a different instance.</summary>
/// <typeparam name="TModel">The type of the model the form edits.</typeparam>
/// <remarks>
/// The form needs an interactive render mode. A statically rendered page with none coming gets
/// a paragraph asking for one in the form's place, reported once to the host; while a Blazor Web
/// App prerenders, the form renders <c>inert</c> until interactivity arrives.
/// </remarks>
public sealed class FormidableForm<TModel> : ComponentBase, IDisposable
    where TModel : class
{
    private TModel? _boundModel;
    private FormidableOptions? _boundOptions;
    private FormidableEngine<TModel>? _engine;
    private FormidableFormContext? _context;
    private string _modelLevelFieldId = string.Empty;

    // Null until the first read answers it — see RefusesToRender for why the answer is kept rather
    // than re-read.
    private bool? _refusedRenderMode;

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
    // thing that knows when its own element stops existing. The reference is created once and
    // reused, since every observer this form establishes reports to it, and it is taken over a
    // receiver rather than over this component: the callback the script invokes has to be public,
    // and a component's public members are consumer surface.
    private FormidableJsModule? _jsModule;
    private DotNetObjectReference<LayoutObserverReceiver>? _layoutObserverReference;

    // Which form element the observer sits on: the context a rebuild replaces, and the element id
    // that rebuild renders. A rebuilt form draws a fresh <form> element — BuildRenderTree keys its
    // region on the context instance — so an observer left where the old one stood would watch a
    // node that is no longer in the document.
    private FormidableFormContext? _observedContext;
    private string _observedFormId = string.Empty;

    // The displaced-click guard's own half, held apart from the observer's because the two are
    // established on different terms — see ClickRecoveryGuard, which owns that half whole.
    private readonly ClickRecoveryGuard _clickRecovery = new();

    private bool _disposed;

    /// <summary>The model the form edits; a different instance rebuilds the <see cref="EditContext"/> and the engine. Required.</summary>
    [Parameter, EditorRequired]
    public TModel Model { get; set; } = default!;

    /// <summary>Raised when <see cref="ResetAsync(TModel?)"/> swaps in a new model, so <c>@bind-Model</c> keeps the parent's field in step; without it that call throws.</summary>
    [Parameter]
    public EventCallback<TModel> ModelChanged { get; set; }

    /// <summary>The validator the form validates through, in place of the <see cref="IModelValidator{TModel}"/> the container registers. Defaults to <see langword="null"/>, which resolves from the container.</summary>
    /// <remarks>
    /// It is taken whole: a wrapper implementing <see cref="IModelValidator{TModel}"/> alone
    /// loses the required indicator, <c>aria-required</c> and the confirmations
    /// <see cref="DiscloseLoadedValuesAsync"/> makes (a loss reported once as the engine is
    /// built) and the reuse of a rule's answer between checks (a loss reported nowhere). Derive a
    /// wrapper from <see cref="DelegatingModelValidator{TModel}"/>, which forwards
    /// <see cref="IRuleInspectingValidator{TModel}"/> and <see cref="IRuleLevelValidator{TModel}"/>.
    /// </remarks>
    [Parameter]
    public IModelValidator<TModel>? Validator { get; set; }

    /// <summary>The options the engine is built with, read once as it is built. Defaults to <see langword="null"/>: the app-wide instance <see cref="FormidableBlazorServiceCollectionExtensions.AddFormidableBlazor(IServiceCollection, Action{FormidableOptions})"/> registered, else a new <see cref="FormidableOptions"/>.</summary>
    [Parameter]
    public FormidableOptions? Options { get; set; }

    /// <summary>The form's markup, handed the cascaded <see cref="FormidableFormContext"/> as <c>context</c>, so inline code reaches the engine and <see cref="FormidableFormContext.FocusFirstErrorAsync"/> without an <c>@ref</c>.</summary>
    /// <remarks>
    /// Nesting another typed fragment that leaves its parameter name implicit (a
    /// <see cref="FormidableField{TValue}"/>, a <c>Virtualize</c>) makes the Razor compiler ask
    /// for <c>Context="..."</c> on one of the two, this form's own included. An inline read of
    /// engine state refreshes when the form re-renders, not on every check; a live indicator
    /// subscribes to <see cref="IFormidableEngine.StateChanged"/> itself.
    /// </remarks>
    [Parameter]
    // Typed, and the Context= rename it costs beside another typed fragment stands: measured on
    // canonical Blazor, EditForm plus Virtualize alone emit the same RZ9999, so the tax is the
    // platform's own and not one this component adds. What collides is the declaration, not any
    // use of it, so the rename is owed whether or not either body reads its parameter.
    public RenderFragment<FormidableFormContext>? ChildContent { get; set; }

    /// <summary>Raised when the submit succeeds, with the <see cref="SubmitOutcome"/>, whose advisories a passing submit can still carry; a parameterless handler binds too.</summary>
    [Parameter]
    // Typed, mirroring EditForm.OnValidSubmit's own typed precedent, so a handler that wants the
    // advisories reads them here rather than digging through Engine.
    public EventCallback<SubmitOutcome> OnValidSubmit { get; set; }

    /// <summary>Raised when the submit blocks, with a <see cref="FormidableInvalidSubmitContext"/> carrying the outcome and the way to suppress this submit's focus move; a parameterless handler binds too.</summary>
    [Parameter]
    public EventCallback<FormidableInvalidSubmitContext> OnInvalidSubmit { get; set; }

    /// <summary>Whether a blocked submit moves focus to the first error on the page, unless its <see cref="FormidableInvalidSubmitContext.SuppressFirstErrorFocus"/> was called, and whether a server reply applied with an error in it does. Defaults to <see langword="true"/>.</summary>
    [Parameter]
    public bool FocusFirstErrorOnInvalidSubmit { get; set; } = true;

    /// <summary>Called once when a focus move misses; return <see langword="true"/> after making the field reachable and the move retries once, <see langword="false"/> to leave the miss.</summary>
    /// <remarks>
    /// The same delegate shape as <see cref="FormidableSummary.FocusFallback"/>, so one callback
    /// serves both; unset, a miss logs a diagnostic naming this parameter.
    /// </remarks>
    [Parameter]
    public Func<FieldIdentifier, ValueTask<bool>>? FocusFallback { get; set; }

    /// <summary>Awaited before every focus move this form makes, with the field about to be focused, so the page can make it reachable first.</summary>
    /// <remarks>
    /// Complete it when the page is ready to take focus: on a dialog's closed event, not on the
    /// state change that starts the close. It runs once per move, ahead of the first attempt, and
    /// only for a move that is about to happen. A throw surfaces where the move was asked for,
    /// and from a server apply becomes an unobserved task exception.
    /// </remarks>
    [Parameter]
    // A Func rather than an EventCallback, because invoking one of those routes through
    // IHandleEvent on the component that supplied the handler, and ComponentBase's implementation
    // calls StateHasChanged for it: once for a handler that completes synchronously, and a second
    // time once an asynchronous one completes. This hook is awaited in the middle of a move
    // (after the field is chosen, before its element is addressed), and a render of the page
    // belongs to what the page changed, not to its having been asked to make the target reachable.
    public Func<FieldIdentifier, ValueTask>? PrepareFocus { get; set; }

    /// <summary>Attributes splatted onto the <c>&lt;form&gt;</c>; <c>id</c> and <c>tabindex</c> stay the form's own, <c>novalidate</c> can be splatted away, and <c>aria-describedby</c> merges, splatted ids first.</summary>
    /// <remarks>
    /// <c>novalidate="@false"</c> (the <see langword="bool"/>, not the string <c>"false"</c>)
    /// restores the browser's own constraint UI.
    /// </remarks>
    [Parameter(CaptureUnmatchedValues = true)]
    public IReadOnlyDictionary<string, object>? AdditionalAttributes { get; set; }

    [Inject]
    private IServiceProvider Services { get; set; } = default!;

    /// <summary>The engine, also cascaded through the <see cref="FormidableFormContext"/>; <see langword="null"/> until the engine is built.</summary>
    public IFormidableEngine? Engine => _engine;

    /// <summary>Builds the engine over <see cref="Model"/> on the first parameter set, rebuilds it for a different instance, and builds nothing on a static page with no render mode.</summary>
    /// <exception cref="InvalidOperationException"><see cref="Model"/> is <see langword="null"/>, <see cref="Options"/> is a different instance than the engine was built with, the container resolves no <c>IModelIntrospector</c>, or, with <see cref="Validator"/> unset, it resolves no <see cref="IModelValidator{TModel}"/>.</exception>
    protected override void OnParametersSet()
    {
        if (RefusesToRender())
        {
            return;
        }

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

    /// <summary>After each render, tells the engine when fields left the page, installs the displaced-click guard once per engine, and asks <see cref="IFormidableFieldOrderService"/> where the fields sit when the set or the layout moved.</summary>
    /// <param name="firstRender">Whether this is the component's first render; not consulted.</param>
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

        // After the reconcile above and before the ordering gate below, which is the only place
        // it can sit. It cannot go first: the reconcile is the engine's own truth and reaches it
        // synchronously on purpose (see the remark above), and putting an interop round trip in
        // front of it would let a pass started in that window file verdicts the deferred
        // reconcile then throws away, and would push the refresh it arms out by however long the
        // browser takes to answer — on the first render, when a page is at its busiest. It cannot
        // go after the gate either: the guard is no part of resolving a reading order, and a form
        // with no IFormidableFieldOrderService registered still has clicks to lose. Once
        // established it does nothing at all, so what every later render pays for it is one
        // reference comparison.
        await EstablishClickRecoveryAsync();

        await ResolveFieldOrderAsync(version);
    }

    /// <summary>Resolves the reading order when the registry version or a reported layout move changed, else leaves the order in force.</summary>
    /// <param name="version">The registry version this render observed.</param>
    private async Task ResolveFieldOrderAsync(int version)
    {
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
        var fields = _engine!.Registry.RegisteredFields.ToList();
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
        catch (Exception exception) when (FormidableJsModule.IsInteropFailure(exception))
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

    // What the browser's report does: record the move and provoke a render, which is where
    // OnAfterRenderAsync picks it up. The resolve needs a rendered page to measure, and this
    // arrives on a browser callback rather than on the renderer's own loop.
    private async Task OnLayoutMovedAsync()
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

    /// <summary>Establishes the displaced-click guard on the form element, once per context, scoped by the model-level field id the element carries.</summary>
    // ClickRecoveryGuard owns the sequence; what this root brings to it is the element to scope
    // the guard to. That is the <form> this form renders its own model-level id onto, and the
    // script takes a form as a root whatever that form currently holds, so the answer is never
    // consulted here: the only way this root comes up empty is an id something else on the page
    // claims first, which the registered fields the guard passes as fallback candidates then walk
    // up from. A form that is not rendered has no registered fields either, so there is nothing
    // to walk up from and no guard to install.
    //
    // Establishing once per context is the opposite bargain to the layout observer's below, and
    // deliberately so: the observer's own gate reopens only when a registration changes, so
    // retrying costs it nothing.
    private async Task EstablishClickRecoveryAsync() =>
        await _clickRecovery.EstablishAsync(
            _context,
            _modelLevelFieldId,
            _engine!.Options,
            _engine.Registry,
            ResolveJsModule,
            () => _disposed);

    /// <summary>The form's own script module, created on first use and released with the component.</summary>
    /// <returns>The module, or <see langword="null"/> when the host resolves no <see cref="IJSRuntime"/>, which the seams here treat as no browser to ask.</returns>
    private FormidableJsModule? ResolveJsModule() =>
        Services.GetService<IJSRuntime>() is { } jsRuntime
            ? _jsModule ??= new FormidableJsModule(jsRuntime)
            : null;

    /// <summary>Moves the browser-side layout observer onto the current context's form element; it does nothing while that context is the one observed.</summary>
    // Best-effort: a form with no observer re-resolves its order when a field registers or
    // unregisters and not when the page merely moves the existing ones, which is what every host
    // with no JavaScript already does.
    //
    // The observer cannot feed itself. It reports DOM changes, and resolving an order only reads
    // where elements sit (nothing here writes to the page), so the render a report provokes
    // either changes the DOM because the order genuinely changed, which the following resolve
    // finds settled, or changes nothing and produces no records to report.
    private async Task EstablishLayoutObserverAsync()
    {
        if (ReferenceEquals(_observedContext, _context))
        {
            return;
        }

        if (ResolveJsModule() is not { } module)
        {
            return;
        }

        _layoutObserverReference ??=
            DotNetObjectReference.Create(new LayoutObserverReceiver(OnLayoutMovedAsync));

        var observing = _observedFormId;
        _observedFormId = string.Empty;
        _observedContext = null;

        try
        {
            if (observing.Length > 0)
            {
                await module.InvokeVoidAsync("disconnectLayoutObserver", observing);
            }

            await module.InvokeVoidAsync(
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

    /// <summary>Runs the consumer's re-sort over the resolved order and appends any field it left out, dropping repeats and fields it was never handed.</summary>
    /// <param name="ordered">The resolved document order.</param>
    /// <param name="reorder">The consumer's <see cref="FormidableOptions.OrderIssues"/> delegate.</param>
    /// <returns>A permutation of <paramref name="ordered"/>, or <paramref name="ordered"/> itself when the delegate answers <see langword="null"/>.</returns>
    // Internal rather than private so the append guarantee is testable directly, without a
    // render. A delegate reorders; it does not decide what is reported, so a field missing from
    // its answer keeps its place at the end rather than losing its issues, and a field it was
    // never handed is dropped: otherwise a delegate padding its result out to the same length
    // with fields of its own would satisfy the "nothing missing" check while silently pushing
    // real fields out of the map.
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

    /// <summary>Disposes the current engine and builds a new engine, <c>EditContext</c> and <see cref="FormidableFormContext"/> over <paramref name="model"/>; the <see cref="Model"/> swap and <see cref="ResetAsync"/> share it.</summary>
    /// <param name="model">The model the new engine validates.</param>
    // A brand new context instance, not merely a new engine reference inside the old one: that is
    // what BuildRenderTree's region key relies on, because a new context instance is what turns the
    // swap into a fresh mount for every descendant, rather than leaving already-bound components
    // pointed at whatever the old context still refers to.
    private void RebuildEngine(TModel model)
    {
        _engine?.Dispose();
        _boundModel = model;
        _boundOptions = Options;
        var editContext = new EditContext(model);
        _engine = FormidableEngineFactory.Create(
            model,
            editContext,
            Services,
            Validator,
            Options,
            renderDispatch: work => InvokeAsync(work));
        _context = new FormidableFormContext(_engine, FocusFirstErrorAsync);
        _modelLevelFieldId = FormidableFieldId.For(_engine.ModelLevelField);

        // The new engine has its own registry, whose version starts over — and its own fields to
        // locate, since the swapped-in model's identifiers are not the old ones. Both trackers
        // reset, or the old registry's count could happen to match the new one's and the first
        // render after a swap would take itself for a render with nothing to do.
        _fieldOrderVersion = -1;
        _renderedFieldSetVersion = -1;
    }

    /// <summary>Returns the form to pristine by rebuilding the engine over the current model, or over <paramref name="newModel"/> when <c>@bind-Model</c> is bound.</summary>
    /// <param name="newModel">The model to edit instead, or <see langword="null"/> to keep the current instance.</param>
    /// <exception cref="InvalidOperationException">No engine has been built yet, or <paramref name="newModel"/> was supplied with no <see cref="ModelChanged"/> bound.</exception>
    /// <remarks>
    /// Everything the old engine held goes with it: touched and modified state, every message,
    /// <see cref="IFormidableEngine.HasSubmitted"/>, a whole-form re-check still waiting, and a
    /// <see cref="SubmitAsync"/> still awaiting its answer, which then fires no callback. Call it
    /// from the renderer's synchronization context.
    /// </remarks>
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

    /// <summary>Runs the submit and routes the outcome to <see cref="OnValidSubmit"/> or <see cref="OnInvalidSubmit"/>; the rendered <c>&lt;form&gt;</c>'s own submit runs the same thing.</summary>
    /// <returns>The submit's outcome; blocked with an empty summary when a newer submit or load answered in its place (<see cref="OnInvalidSubmit"/> still fires), and blocked and empty when <see cref="ResetAsync(TModel?)"/> or a different <see cref="Model"/> rebuilt the engine during the submit (neither callback fires).</returns>
    /// <exception cref="InvalidOperationException">No engine has been built yet.</exception>
    /// <remarks>
    /// A reset during the submit abandons it: neither callback fires, focus stays and nothing
    /// re-renders. Call it from the renderer's synchronization context.
    /// </remarks>
    public async Task<SubmitOutcome> SubmitAsync()
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

        if (outcome.CanProceed)
        {
            await OnValidSubmit.InvokeAsync(outcome);
        }
        else
        {
            // A fresh context per blocked submit, which is the whole of what keeps suppression
            // from latching: the handler's answer lives on the object it was handed, and the next
            // submit hands it a different one.
            var invalidSubmit = new FormidableInvalidSubmitContext(outcome);
            await OnInvalidSubmit.InvokeAsync(invalidSubmit);
            if (FocusFirstErrorOnInvalidSubmit && !invalidSubmit.FirstErrorFocusSuppressed)
            {
                await FocusFirstErrorAsync();
            }
        }

        StateHasChanged();
        return outcome;
    }

    /// <summary>Moves focus to the first visible error, or to the first visible issue when no error shows, and reports whether an element took it.</summary>
    /// <returns><see langword="true"/> when an element took focus; <see langword="false"/> when nothing moved, whether the form shows no issue, no <see cref="IFormidableFocusService"/> is registered, or the field is out of reach.</returns>
    /// <exception cref="InvalidOperationException">No engine has been built yet.</exception>
    /// <remarks>
    /// The move a blocked submit makes, with <see cref="PrepareFocus"/> and
    /// <see cref="FocusFallback"/>, offered to a page that chose the moment;
    /// <see cref="FocusFirstErrorOnInvalidSubmit"/> does not gate it. Call it from the renderer's
    /// synchronization context.
    /// </remarks>
    public async Task<bool> FocusFirstErrorAsync() =>
        await FirstErrorFocus.MoveAsync(Services, RequireEngine(), FocusFallback, PrepareFocus);

    /// <summary>Moves focus to the first error after a server apply that carried one, under <see cref="FocusFirstErrorOnInvalidSubmit"/>, without awaiting the move.</summary>
    /// <param name="appliedIssues">The issues the apply carried.</param>
    private void FocusAfterServerIssues(IReadOnlyCollection<ValidationIssue> appliedIssues)
    {
        // Gated on the payload carrying an error as well as on the option: a rejected round trip
        // is a blocked submit that arrived late, so it lands the visitor on the first error the
        // same way SubmitAsync would, but a clean or advisory-only apply rejected nothing, so it
        // must not steal focus onto some unrelated issue still visible from an earlier submit.
        if (!FocusFirstErrorOnInvalidSubmit
            || !appliedIssues.Any(issue => issue.Severity == ValidationSeverity.Error))
        {
            return;
        }

        // Fire-and-forget: focus is best-effort and must not make applying issues asynchronous,
        // as both ApplyServerIssues overloads are synchronous by contract. An interop failure
        // (FormidableJsModule.IsInteropFailure: a disconnected circuit as well as a thrown or
        // unreachable JS boundary) from the unawaited call is swallowed below rather than left to
        // become an unobserved task exception.
        _ = FocusQuietlyAsync();

        async Task FocusQuietlyAsync()
        {
            try
            {
                await FocusFirstErrorAsync();
            }
            catch (Exception exception) when (FormidableJsModule.IsInteropFailure(exception))
            {
            }
        }
    }

    /// <summary>Applies a server reply's issues as the server's current answer and, when the reply carries an error, moves focus to the first error on the page.</summary>
    /// <param name="issues">The server's current issues; enumerated once.</param>
    /// <exception cref="ArgumentNullException"><paramref name="issues"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">No engine has been built yet.</exception>
    /// <remarks>
    /// The apply is <see cref="IFormidableEngine.ApplyServerIssues(IEnumerable{ValidationIssue})"/>'s,
    /// which sets <see cref="IFormidableEngine.HasSubmitted"/> and moves no focus of its own;
    /// call that through <see cref="Engine"/> for a quiet apply. The move here is gated by
    /// <see cref="FocusFirstErrorOnInvalidSubmit"/>. Call this from the renderer's
    /// synchronization context.
    /// </remarks>
    public void ApplyServerIssues(IEnumerable<ValidationIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(issues);
        var issueList = issues as IReadOnlyList<ValidationIssue> ?? issues.ToList();
        RequireEngine().ApplyServerIssues(issueList);
        FocusAfterServerIssues(issueList);
    }

    /// <summary>Applies a deserialized 400 body by flattening it with <see cref="FormidableValidationProblem.ToIssues"/>, exactly as <see cref="ApplyServerIssues(IEnumerable{ValidationIssue})"/> does, focus move included.</summary>
    /// <param name="problem">The deserialized response body.</param>
    /// <exception cref="ArgumentNullException"><paramref name="problem"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">No engine has been built yet.</exception>
    public void ApplyServerIssues(FormidableValidationProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);
        var issues = problem.ToIssues();
        RequireEngine().ApplyServerIssues(issues);
        FocusAfterServerIssues(issues);
    }

    /// <summary>Says what the values already in the model have earned: a field holding a value shows what the submit profile says of it, valid or its message, and a field holding nothing stays silent.</summary>
    /// <param name="cancellationToken">Cancels the check; a cancelled call throws and discloses nothing.</param>
    /// <exception cref="InvalidOperationException">No engine has been built yet.</exception>
    /// <remarks>
    /// The contract is <see cref="IFormidableEngine.DiscloseLoadedValuesAsync"/>'s. Call it once
    /// after filling <see cref="Model"/> from a saved draft or a loaded record; unlike a blocked
    /// submit it moves no focus. Call it from the renderer's synchronization context.
    /// </remarks>
    // Takes a token where the submit methods take none: a submit is UI-event-driven and its
    // handler holds none to pass, while this call is data-driven, and the caller that filled the
    // model typically holds the CancellationTokenSource it minted for the load, so a replaced
    // record-open or a dispose mid-load cancels the disclosure through the token that cancels the
    // fetch.
    public Task DiscloseLoadedValuesAsync(CancellationToken cancellationToken = default) =>
        RequireEngine().DiscloseLoadedValuesAsync(cancellationToken);

    /// <summary>The engine, or an <see cref="InvalidOperationException"/> saying the call arrived before the first render built one.</summary>
    /// <returns>The built engine.</returns>
    /// <exception cref="InvalidOperationException">No engine has been built yet.</exception>
    private FormidableEngine<TModel> RequireEngine() =>
        _engine ?? throw new InvalidOperationException(
            $"{nameof(FormidableForm<TModel>)} has no engine yet — one is built when the form first " +
            "renders, and this call arrived before that. Capture the form with @ref and call it from " +
            "an event handler (OnValidSubmit, a button's onclick) rather than from a lifecycle " +
            "method that runs ahead of the first render.");

    /// <summary>Whether this form renders <see cref="RenderModeMessage"/> in place of a form, decided once and reported to the host once through <see cref="ReportRefusedRenderMode"/>.</summary>
    /// <returns><see langword="true"/> on a statically rendered page with no render mode coming.</returns>
    // The answer is kept, where the inert reading of the same pair is taken afresh every render,
    // because two callers act on this one and have to agree: the parameter set that stops without
    // building an engine, and the render that puts the message in the form's place. Re-reading
    // would let a render take the form branch with no engine, context or element id behind it,
    // and only a parameter set builds those. Stopping the parameter set is also what keeps the
    // message the page's own: building the engine resolves the validator, and a page missing that
    // registration would fail on it instead, naming the second thing wrong on a page whose first
    // one is this. Taking the answer once is what reports it to the host once, however often the
    // message renders.
    private bool RefusesToRender()
    {
        if (_refusedRenderMode is null)
        {
            _refusedRenderMode = Rendering is FormRendering.Refused;

            if (_refusedRenderMode.Value)
            {
                ReportRefusedRenderMode();
            }
        }

        return _refusedRenderMode.Value;
    }

    /// <summary>Writes <see cref="RenderModeMessage"/> to Trace and, when the host resolved an <c>ILoggerFactory</c>, as a warning, through <see cref="FormidableDiagnostics"/>.</summary>
    // The rendered paragraph reaches whoever is looking at the page and nobody else, and the
    // response carrying it is an ordinary 200, so a page misconfigured this way passes anything
    // watching statuses; the log line is what reaches the host. Both channels carry the message
    // itself, so a log and a page cannot come to describe one refusal differently.
    private void ReportRefusedRenderMode() =>
        FormidableDiagnostics.Warn(
            FormidableEngineFactory.ResolveLogger(Services),
            $"Formidable: {RenderModeMessage}");

    /// <summary>The message rendered in the form's place on a statically rendered page with no render mode.</summary>
    // Such a page renders a form perfectly and then answers its first submit with the platform's
    // own "the POST request does not specify which form is being submitted" 400, whose advice
    // (pass a FormName to EditForm) is not something any FormidableForm parameter can carry, so
    // this names the one instruction that does work instead. It renders rather than throws
    // because a throw from a render reaches the visitor as the host's own error response, which
    // takes the whole document with it: everything else the page holds goes too, where what is
    // wrong is one component that cannot do its job.
    private static string RenderModeMessage =>
        $"{nameof(FormidableForm<TModel>)} requires an interactive render mode: this page is " +
        "rendered statically and no interactivity is coming, so submitting the form would post " +
        "back to the server instead of running the validation pipeline. Add a render mode to " +
        "the page or component — @rendermode InteractiveServer, @rendermode " +
        "InteractiveWebAssembly or @rendermode InteractiveAuto — or host the form in a " +
        "standalone WebAssembly app, where every page is interactive already.";

    /// <summary>Whether the renderer says outright that it is not interactive; a renderer that cannot say counts as interactive.</summary>
    /// <returns><see langword="true"/> only when <c>RendererInfo.IsInteractive</c> reads <see langword="false"/>.</returns>
    private bool RendererDeclaresItselfStatic()
    {
        try
        {
            return !RendererInfo.IsInteractive;
        }
        catch (Exception)
        {
            // A renderer that declines to describe itself has said nothing, and nothing is not
            // proof of a dead end: bUnit's test renderer throws from RendererInfo unless a test
            // declares one, and rendering a form in a component test must not depend on having
            // declared it.
            return false;
        }
    }

    /// <summary>The three things this form can render, as <see cref="Rendering"/> decides them.</summary>
    private enum FormRendering
    {
        /// <summary>The whole form: the renderer says it is interactive, or declines to say.</summary>
        Whole,

        /// <summary>The form carrying <c>inert</c>: a render mode promises interactivity the renderer has not yet delivered, which is the prerender.</summary>
        // Decided afresh on every render, never latched: a latch would rest on a renderer never
        // revising what it says about itself, where being wrong leaves a form inert for good.
        Inert,

        /// <summary>No form: the renderer is static and no render mode promises otherwise, so <see cref="RenderModeMessage"/> renders instead and <see cref="RefusesToRender"/> keeps the answer.</summary>
        Refused,
    }

    /// <summary>What this form renders, from the renderer's interactivity and the assigned render mode read together.</summary>
    // Read together, and in one place, because neither decides alone: the prerender of a
    // component about to become interactive reports exactly the same non-interactive renderer as
    // a page that will never have one, and only the assigned render mode tells those apart. One
    // reading is also what puts the render mode where a test can reach it: split across two
    // predicates it is a conjunct in each, and the render that would discriminate one of those
    // conjuncts is the render the other predicate answers for.
    private FormRendering Rendering => RendererDeclaresItselfStatic()
        ? (AssignedRenderMode is null ? FormRendering.Refused : FormRendering.Inert)
        : FormRendering.Whole;

    /// <summary>Renders <see cref="RenderModeMessage"/> where the form is refused, else the cascaded <see cref="FormidableFormContext"/> around an <c>EditForm</c> over the engine's <see cref="EditContext"/>.</summary>
    /// <param name="builder">The render tree builder.</param>
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        if (RefusesToRender())
        {
            // The message alone, where the form would have been: the page around it paints as it
            // always did, and the one component that cannot do its job says so in its own place.
            // ChildContent stays unrendered with it, because a kit component in there reads the
            // cascaded context this form never built and asks to be placed inside a form when it
            // finds none — rendering the body would trade a message naming the fix for one naming
            // the wrong problem. What a body without those leaves is a form's contents with no
            // form: labels and a submit button with nothing behind them, beside a message saying
            // as much. AdditionalAttributes belong to the form element, and there is none here.
            builder.OpenElement(0, "p");
            builder.AddAttribute(1, "class", "formidable-render-mode-message");
            builder.AddContent(2, RenderModeMessage);
            builder.CloseElement();
            return;
        }

        // Keys the cascade below on _context's own identity (the same idiom EditForm itself uses
        // for EditContext) — see the trailing comment for why this region exists.
        builder.OpenRegion(_context!.GetHashCode());
        builder.OpenComponent<CascadingValue<FormidableFormContext>>(0);
        builder.AddComponentParameter(1, "IsFixed", true);
        builder.AddComponentParameter(2, "Value", _context);
        builder.AddComponentParameter(3, "ChildContent", (RenderFragment)(inner =>
        {
            inner.OpenComponent<EditForm>(0);
            inner.AddComponentParameter(1, nameof(EditForm.EditContext), _engine!.EditContext);
            inner.AddComponentParameter(2, nameof(EditForm.OnSubmit), EventCallback.Factory.Create<EditContext>(this, _ => SubmitAsync()));
            // Rendered BEFORE the splat, so a consumer can splat it away (novalidate="@false").
            // The default is deliberate: without it, any native constraint attribute inside the
            // form — a splatted required/pattern/min/max, a type="email" — has the browser block
            // the submit and front its own bubble before OnSubmit ever fires, so the message the
            // visitor sees stops being FluentValidation's. novalidate switches off only that
            // interactive check: :invalid still matches, ValidityState is still computed, and
            // checkValidity()/reportValidity() still work when called.
            inner.AddAttribute(3, "novalidate", true);
            inner.AddMultipleAttributes(4, AdditionalAttributes!);
            // Rendered after the splat: id and tabindex win the duplicate-attribute race outright,
            // because the all-suppressed gate's summary entry addresses the form by this id (see
            // FormidableFieldId), and a consumer-supplied id or tabindex would break that the same
            // way a consumer-supplied input id would — see FormidableInputBase<TValue>'s identical
            // policy. aria-describedby, added in this same position, does not win outright — see
            // ComputeModelLevelAriaDescribedBy for why it merges instead.
            inner.AddAttribute(5, "id", _modelLevelFieldId);
            inner.AddAttribute(6, "tabindex", "-1");
            inner.AddAttribute(7, "aria-describedby", ComputeModelLevelAriaDescribedBy());
            if (Rendering is FormRendering.Inert)
            {
                // Also after the splat, and for the same reason id is: what it prevents is the
                // library's to prevent. Rendered under a condition rather than as a plain false,
                // though, because an explicitly false attribute added after a splat removes the
                // splatted one of the same name — and a page's own inert, for its own reasons, is
                // no business of this form's outside the window.
                inner.AddAttribute(8, "inert", true);
            }

            inner.AddComponentParameter(9, nameof(EditForm.ChildContent), (RenderFragment<EditContext>)(_ => ChildContent?.Invoke(_context!) ?? (_ => { })));
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

    /// <summary>The form's <c>aria-describedby</c>: any splatted value first, then the model-level message list's id, merged through <see cref="FormidableCss.CombineSplatted"/> as a kit input merges its own.</summary>
    /// <returns>The merged attribute value.</returns>
    // The computed id is rendered unconditionally here, where a kit input renders one only while
    // its own field has issues, so the splatted ids are the only ones describing anything while
    // FormidableModelMessage is absent or standing empty; that is what puts them first. Appending
    // keeps a consumer's own hint where the consumer put it rather than reshuffling it once the
    // model-level list has something to say.
    private string ComputeModelLevelAriaDescribedBy() =>
        FormidableCss.CombineSplatted(AdditionalAttributes, "aria-describedby", FormidableFieldId.MessagesFor(_modelLevelFieldId));

    /// <summary>Disposes the engine and releases what the form put in the browser.</summary>
    public void Dispose()
    {
        // Recorded first: a move reported between here and the observer actually going away would
        // otherwise ask a component that no longer exists to render.
        _disposed = true;
        _engine?.Dispose();
        ReleaseScriptResources();
    }

    /// <summary>Releases the layout observer, the reference it reports through and the click guard's registered root, without awaiting the browser.</summary>
    // Worth doing on its own account, whichever of the two is in play, because the script module
    // they live in outlives this component: a module is loaded once per document and stays. An
    // observer left connected goes on watching a detached form and goes on holding the reference
    // that reaches back into a component nobody else can see; a guard left registered scopes
    // itself to an element no longer in the document, and the document-level listeners it
    // refcounts stay installed over a page with nothing left to guard. Disposal here is
    // synchronous and neither release is, so the reference is let go once the round trip still
    // naming it has returned rather than while it is in flight. An interop boundary that is
    // already gone releases it just the same, and has taken both with it anyway.
    private void ReleaseScriptResources()
    {
        var module = _jsModule;
        var reference = _layoutObserverReference;
        var observing = _observedFormId;
        var releasing = _clickRecovery.Take();

        _jsModule = null;
        _layoutObserverReference = null;
        _observedFormId = string.Empty;
        _observedContext = null;

        if (module is null)
        {
            reference?.Dispose();
            return;
        }

        _ = ReleaseScriptResourcesAsync(module, reference, observing, releasing);
    }

    private static async Task ReleaseScriptResourcesAsync(
        FormidableJsModule module,
        DotNetObjectReference<LayoutObserverReceiver>? reference,
        string observing,
        string releasing)
    {
        try
        {
            if (observing.Length > 0)
            {
                await module.InvokeVoidAsync("disconnectLayoutObserver", observing);
            }

            await ClickRecoveryGuard.ReleaseAsync(module, releasing);
            await module.DisposeAsync();
        }
        catch (Exception exception) when (FormidableJsModule.IsInteropFailure(exception))
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
