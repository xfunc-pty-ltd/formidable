using Formidable.Introspection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;

namespace Formidable.Blazor;

/// <summary>
/// The primary Formidable root component. Owns the <see cref="EditContext"/> — swapping the
/// <see cref="Model"/> parameter (e.g. a draft load) rebuilds the context and engine, so
/// consumers never manage EditContext lifecycles. Submit runs the engine pipeline and routes
/// to <see cref="OnValidSubmit"/> / <see cref="OnInvalidSubmit"/>.
/// </summary>
/// <typeparam name="TModel">The form model type.</typeparam>
public sealed class FormidableForm<TModel> : ComponentBase, IDisposable
    where TModel : class
{
    private TModel? _boundModel;
    private EditContext? _editContext;
    private FormValidationEngine<TModel>? _engine;
    private FormidableFormContext? _context;

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
        ArgumentNullException.ThrowIfNull(Model);

        if (!ReferenceEquals(_boundModel, Model))
        {
            _engine?.Dispose();
            _boundModel = Model;
            _editContext = new EditContext(Model);
            _engine = new FormValidationEngine<TModel>(
                Model,
                _editContext,
                Validator
                    ?? (IModelValidator<TModel>?)Services.GetService(typeof(IModelValidator<TModel>))
                    ?? throw new InvalidOperationException(
                        $"No IModelValidator<{FriendlyTypeName.Of(typeof(TModel))}> is registered — call services.AddFormidable() and register the FluentValidation validator."),
                (IModelIntrospector?)Services.GetService(typeof(IModelIntrospector))
                    ?? throw new InvalidOperationException("No IModelIntrospector is registered — call services.AddFormidable()."),
                Options ?? new FormidableOptions(),
                renderDispatch: work => InvokeAsync(work));
            _context = new FormidableFormContext(_engine);
        }
    }

    /// <summary>
    /// Runs the submit pipeline programmatically. Call from the renderer's synchronization
    /// context (a Blazor event handler or <c>InvokeAsync</c>) — it triggers renders.
    /// </summary>
    public async Task<SubmitOutcome> SubmitAsync()
    {
        var outcome = await _engine!.ValidateForSubmitAsync();
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
            inner.AddAttribute(4, "id", FormidableFieldId.For(new FieldIdentifier(Model, string.Empty)));
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
