using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;

namespace Formidable.Blazor;

/// <summary>Renders <c>&lt;span class="formidable-required" aria-hidden="true"&gt;</c> around <see cref="FormidableOptions.RequiredIndicatorContent"/> while <see cref="FormidableOptions.ShowRequiredIndicators"/> is on and <see cref="IFormidableEngine.GetFieldRequirement"/> answers <see cref="FieldRequirement.Required"/> for the field; nothing otherwise.</summary>
/// <typeparam name="TValue">The accessor's type, inferred from <see cref="FormidableAccessorComponentBase{TValue}.For"/>: the field's own value type, or <c>object</c> where a shared component forwards an <c>Expression&lt;Func&lt;object&gt;&gt;</c>.</typeparam>
/// <remarks>
/// The marker is <c>aria-hidden</c>: the input's <c>aria-required</c> is what assistive
/// technology reads, and no <c>required</c> attribute is ever rendered.
/// <see cref="FormidableOptions.ShowRequiredIndicators"/> turns the marker off form-wide; where
/// it sits is where you place it in the markup, and how it looks is your stylesheet's, because
/// the library ships no styling. It registers nothing.
/// </remarks>
// The marker is derived from the validator's rules rather than declared on the markup, so a rule
// moving between profiles moves the marker with it and a form cannot drift out of step with what
// it enforces. aria-hidden because a glyph read aloud inside a label announces the field as
// "Title star", and an aria-hidden subtree leaves the label's accessible name untouched; the
// fact belongs on the input, where the kit's inputs and FormidableFieldContext.InputAttributes
// put it as aria-required. Nothing is drawn for ConditionallyRequired because whether a
// conditional presence rule applies cannot be decided without evaluating its condition against
// the model, which inspection does not do; drawing the mark would assert a demand the library
// cannot verify, and a validator whose presence rules are all conditional would mark every field.
public sealed class FormidableRequiredIndicator<TValue> : FormidableAccessorComponentBase<TValue>
{
    private FieldIdentifier _field;

    /// <summary><see langword="false"/>: what the rules require does not change with a check, so the marker follows the page's own renders; a changed <see cref="FormidableOptions.RequiredOverride"/> or <see cref="FormidableOptions.SubmitProfile"/> shows at the next one.</summary>
    protected override bool ObservesEngineState => false;

    /// <summary>Resolves the field and registers nothing: a marker is not an input, so the input beside it, or a <see cref="FormidableFieldAnchor{TValue}"/>, keeps the field registered.</summary>
    /// <param name="context">The context being bound.</param>
    /// <returns>Always <see langword="null"/>.</returns>
    protected override FieldRegistration? Register(FormidableFormContext context)
    {
        _field = ResolveField();
        return null;
    }

    /// <summary>Renders the marker while <see cref="FormidableOptions.ShowRequiredIndicators"/> is on and <see cref="IFormidableEngine.GetFieldRequirement"/> answers <see cref="FieldRequirement.Required"/>; otherwise nothing.</summary>
    /// <param name="builder">The render tree builder.</param>
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        if (Context is null)
        {
            return;
        }

        if (!Context.Engine.Options.ShowRequiredIndicators
            || Context.Engine.GetFieldRequirement(_field) != FieldRequirement.Required)
        {
            return;
        }

        builder.OpenElement(0, "span");
        builder.AddAttribute(1, "class", "formidable-required");
        builder.AddAttribute(2, "aria-hidden", "true");
        builder.AddContent(3, Context.Engine.Options.RequiredIndicatorContent);
        builder.CloseElement();
    }
}
