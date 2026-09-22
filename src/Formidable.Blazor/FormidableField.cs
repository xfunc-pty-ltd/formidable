using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;

namespace Formidable.Blazor;

/// <summary>A renderless field for markup Formidable does not wrap: it registers the field and hands <see cref="ChildContent"/> a fresh <see cref="FormidableFieldContext"/> on every render.</summary>
/// <typeparam name="TValue">The accessor's type, inferred from <see cref="FormidableAccessorComponentBase{TValue}.For"/>: the field's own value type, or <c>object</c> where a shared component forwards an <c>Expression&lt;Func&lt;object&gt;&gt;</c>.</typeparam>
public sealed class FormidableField<TValue> : FormidableAccessorComponentBase<TValue>
{
    private FieldIdentifier _field;
    private string _elementId = string.Empty;

    /// <summary>Whether the field stays registered after this component is disposed, for rows a <c>Virtualize</c> container disposes while they remain in the form. Defaults to <see langword="false"/>.</summary>
    [Parameter]
    public bool KeepRegistered { get; set; }

    /// <summary>The markup to render, handed the field's current <see cref="FormidableFieldContext"/>. Required.</summary>
    [Parameter, EditorRequired]
    public RenderFragment<FormidableFieldContext> ChildContent { get; set; } = default!;

    /// <summary>Resolves the field <see cref="FormidableAccessorComponentBase{TValue}.For"/> names and its element id, and registers it with <paramref name="context"/>'s registry under <see cref="KeepRegistered"/>.</summary>
    /// <param name="context">The context being bound.</param>
    /// <returns>The registration the base releases on the next rebind or on disposal.</returns>
    protected override FieldRegistration? Register(FormidableFormContext context)
    {
        _field = ResolveField();
        _elementId = FormidableFieldId.For(_field);
        return context.Registry.Register(_field, KeepRegistered);
    }

    /// <summary>Renders <see cref="ChildContent"/> with a context built from the field's current state, class and issues; renders nothing before the first bind.</summary>
    /// <param name="builder">The render tree builder.</param>
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
