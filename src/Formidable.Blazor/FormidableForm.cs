using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;

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

    /// <summary>The form model. A reference change rebuilds the EditContext and engine.</summary>
    [Parameter, EditorRequired]
    public TModel Model { get; set; } = default!;

    /// <summary>Validator override; resolved from DI when omitted.</summary>
    [Parameter]
    public IModelValidator<TModel>? Validator { get; set; }

    /// <summary>Engine options; defaults apply when omitted.</summary>
    [Parameter]
    public FormidableOptions? Options { get; set; }

    /// <summary>Form content. A <see cref="FormidableFormContext"/> is cascaded to it.</summary>
    [Parameter]
    public RenderFragment? ChildContent { get; set; }

    /// <summary>Invoked when the submit pipeline passes.</summary>
    [Parameter]
    public EventCallback OnValidSubmit { get; set; }

    /// <summary>Invoked with the outcome when the submit pipeline blocks.</summary>
    [Parameter]
    public EventCallback<SubmitOutcome> OnInvalidSubmit { get; set; }

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
            _engine?.Dispose();
            _boundModel = Model;
            _boundOptions = Options;
            _editContext = new EditContext(Model);
            _engine = FormidableEngineFactory.Create(
                Model,
                _editContext,
                Services,
                Validator,
                Options,
                renderDispatch: work => InvokeAsync(work));
            _context = new FormidableFormContext(_engine);
            _modelLevelFieldId = FormidableFieldId.For(_engine.ModelLevelField);
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
    /// Runs the submit pipeline programmatically. Call from the renderer's synchronization
    /// context (a Blazor event handler or <c>InvokeAsync</c>) — it triggers renders.
    /// </summary>
    public async Task<SubmitOutcome> SubmitAsync()
    {
        var outcome = await RequireEngine().ValidateForSubmitAsync();
        if (outcome.CanProceed)
        {
            await OnValidSubmit.InvokeAsync();
        }
        else
        {
            await OnInvalidSubmit.InvokeAsync(outcome);
        }

        StateHasChanged();
        return outcome;
    }

    /// <summary>
    /// Applies a server response's issues to this form's engine, forwarding
    /// <see cref="IFormValidationEngine.ApplyServerIssues(IEnumerable{ValidationIssue})"/> and its
    /// contract whole: the payload is the server's current verdict and replaces what the previous
    /// call applied, only error-severity issues reach fields, and applying any also sets
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
    public void Dispose() => _engine?.Dispose();
}
