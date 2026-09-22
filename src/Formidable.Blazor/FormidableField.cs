using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;

namespace Formidable.Blazor;

/// <summary>
/// Renderless field component: hands any UI library a fresh <see cref="FormidableFieldContext"/>
/// on every render via <see cref="ChildContent"/> (state, issues, computed CSS class, aria ids),
/// and registers with the form's <see cref="FieldRegistry"/> so automatic disclosure stays
/// truthful for whatever markup <see cref="ChildContent"/> renders. <see cref="FormidableAccessorComponentBase{TValue}.For"/> is
/// (re-)read whenever the cascaded <see cref="FormidableFormContext"/> is a new instance —
/// including the first render and again after a host such as <c>FormidableForm</c>/
/// <c>FormidableValidator</c> swaps its model and rebuilds its engine and registry — so the
/// registration and the engine subscription always target the currently-active context.
/// </summary>
/// <typeparam name="TValue">
/// The accessor's type, inferred from <see cref="FormidableAccessorComponentBase{TValue}.For"/>: the field's own value type, or
/// <c>object</c> where a shared component forwards an
/// <c>Expression&lt;Func&lt;object&gt;&gt;</c>.
/// </typeparam>
public sealed class FormidableField<TValue> : FormidableAccessorComponentBase<TValue>
{
    private FieldIdentifier _field;
    private string _elementId = string.Empty;

    /// <summary>Keeps the field registered after disposal — for virtualized containers.</summary>
    [Parameter]
    public bool KeepRegistered { get; set; }

    /// <summary>Renders with the field's current <see cref="FormidableFieldContext"/>.</summary>
    [Parameter, EditorRequired]
    public RenderFragment<FormidableFieldContext> ChildContent { get; set; } = default!;

    /// <inheritdoc />
    protected override FieldRegistration? Register(FormidableFormContext context)
    {
        _field = ResolveField();
        _elementId = FormidableFieldId.For(_field);
        return context.Registry.Register(_field, KeepRegistered);
    }

    /// <inheritdoc />
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        if (Context is null)
        {
            return;
        }

        var engine = Context.Engine;
        var state = engine.GetFieldState(_field);
        var context = new FormidableFieldContext(
            engine,
            _field,
            _elementId,
            state,
            FormidableCss.Compute(state, engine.Options.CssClasses),
            engine.GetIssues(_field));

        builder.AddContent(0, ChildContent(context));
    }
}
