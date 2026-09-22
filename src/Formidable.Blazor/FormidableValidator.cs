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
    public void Dispose() => _engine?.Dispose();
}
