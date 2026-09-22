using Formidable.Introspection;
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

        _engine ??= new FormValidationEngine<TModel>(
            model,
            CascadedEditContext,
            Validator
                ?? (IModelValidator<TModel>?)Services.GetService(typeof(IModelValidator<TModel>))
                ?? throw new InvalidOperationException($"No IModelValidator<{FriendlyTypeName.Of(typeof(TModel))}> is registered — call services.AddFormidable() and register the FluentValidation validator."),
            (IModelIntrospector?)Services.GetService(typeof(IModelIntrospector))
                ?? throw new InvalidOperationException("No IModelIntrospector is registered — call services.AddFormidable()."),
            Options ?? new FormidableOptions(),
            renderDispatch: work => InvokeAsync(work));
        _context = new FormidableFormContext(_engine);
    }

    /// <inheritdoc />
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        if (_context is null || ChildContent is null)
        {
            return;
        }

        builder.OpenComponent<CascadingValue<FormidableFormContext>>(0);
        builder.AddComponentParameter(1, "Value", _context);
        builder.AddComponentParameter(2, "ChildContent", ChildContent);
        builder.CloseComponent();
        // Not IsFixed: the context instance is replaced if the enclosing EditForm swaps its
        // EditContext (model change), and descendants must observe the replacement.
    }

    /// <inheritdoc />
    public void Dispose() => _engine?.Dispose();
}
