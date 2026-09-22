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

    /// <summary>Additional attributes splatted onto the rendered form element.</summary>
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
        builder.OpenComponent<CascadingValue<FormidableFormContext>>(0);
        builder.AddComponentParameter(1, "Value", _context);
        builder.AddComponentParameter(2, "ChildContent", (RenderFragment)(inner =>
        {
            inner.OpenComponent<EditForm>(0);
            inner.AddComponentParameter(1, nameof(EditForm.EditContext), _editContext);
            inner.AddComponentParameter(2, nameof(EditForm.OnSubmit), EventCallback.Factory.Create<EditContext>(this, _ => SubmitAsync()));
            if (AdditionalAttributes is not null)
            {
                inner.AddMultipleAttributes(3, AdditionalAttributes!);
            }
            inner.AddComponentParameter(4, nameof(EditForm.ChildContent), (RenderFragment<EditContext>)(_ => ChildContent ?? (_ => { })));
            inner.CloseComponent();
        }));
        builder.CloseComponent();
        // Not IsFixed: the context instance is replaced whenever Model is swapped (a new
        // engine/EditContext pair), and descendants must observe the replacement.
    }

    /// <inheritdoc />
    public void Dispose() => _engine?.Dispose();
}
