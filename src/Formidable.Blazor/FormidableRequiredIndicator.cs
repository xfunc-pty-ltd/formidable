using System.Linq.Expressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;

namespace Formidable.Blazor;

/// <summary>
/// Marks a field the submit profile demands a value for. Renders
/// <c>&lt;span class="formidable-required" aria-hidden="true"&gt;</c> around
/// <see cref="FormidableOptions.RequiredIndicatorContent"/> while
/// <see cref="IFormidableEngine.GetFieldRequirement"/> answers
/// <see cref="FieldRequirement.Required"/> for <see cref="For"/>, and nothing at all otherwise.
/// While <see cref="FormidableOptions.ShowRequiredIndicators"/> is off, the component renders
/// nothing for any field, whatever the rules demand.
/// Place it wherever the marker belongs — inside the field's <c>&lt;label&gt;</c>, after the
/// label text, is the shape most samples use.
/// </summary>
/// <remarks>
/// The marker is derived from the validator's rules rather than declared on the markup, so a
/// rule moving between profiles moves the marker with it and a form cannot drift out of step
/// with what it enforces. What it draws is a class and its configured content: the library ships
/// no styling, so a stylesheet keying on <c>formidable-required</c> decides how it looks.
/// <para>
/// It is <c>aria-hidden</c> deliberately, and it is not the accessible half of this feature. A
/// glyph read aloud inside a label announces the field as "Title star"; the fact belongs on the
/// input, where the kit's own inputs and
/// <see cref="FormidableFieldContext.InputAttributes"/> put it as <c>aria-required="true"</c>.
/// That also means the marker sits inside a <c>&lt;label&gt;</c> without disturbing the
/// accessible name computed from it.
/// </para>
/// <para>
/// Nothing is drawn for <see cref="FieldRequirement.ConditionallyRequired"/> — a presence rule
/// the profile selects but reaches only through a condition. Whether that demand applies cannot
/// be decided without evaluating the condition against the model, which inspection does not do,
/// so drawing the same mark would assert a demand the library cannot verify, and a validator
/// whose presence rules are all conditional would mark every field on the form. A page that
/// wants to say something there reads
/// <see cref="FormidableFieldContext.Requirement"/> from a <see cref="FormidableField{TValue}"/>
/// and renders its own markup, or declares the field outright with
/// <see cref="FormidableOptions.RequiredOverride"/>.
/// </para>
/// <para>
/// Registers nothing: a marker is not an input, so what keeps the field registered for disclosure
/// is the validated input beside it (or a <see cref="FormidableFieldAnchor{TValue}"/>), exactly
/// as it is for <see cref="FormidableFieldMessage{TValue}"/>. <see cref="For"/> is (re-)read
/// whenever the cascaded <see cref="FormidableFormContext"/> is a new instance — including the
/// first render and again after a host such as <c>FormidableForm</c>/<c>FormidableValidator</c>
/// swaps its model and rebuilds its engine and registry.
/// </para>
/// </remarks>
/// <typeparam name="TValue">
/// The accessor's type, inferred from <see cref="For"/>: the field's own value type, or
/// <c>object</c> where a shared component forwards an
/// <c>Expression&lt;Func&lt;object&gt;&gt;</c>.
/// </typeparam>
public sealed class FormidableRequiredIndicator<TValue> : FormidableComponentBase
{
    private FieldIdentifier _field;

    /// <summary>Accessor for the field to mark, e.g. <c>() => Model.Description</c>.</summary>
    [Parameter, EditorRequired]
    public Expression<Func<TValue>> For { get; set; } = default!;

    /// <summary>
    /// False: requiredness is a property of the rules, not of what the current values are doing,
    /// so no validation pass can change what this renders. A change to
    /// <see cref="FormidableOptions.RequiredOverride"/> or to
    /// <see cref="FormidableOptions.SubmitProfile"/> is picked up on the page's next render
    /// rather than on the next pass.
    /// </summary>
    protected override bool ObservesEngineState => false;

    /// <inheritdoc />
    private protected override FieldIdentifier ResolveField() =>
        FieldIdentifier.Create(FieldAccessor.RequireFor(For, GetType()));

    /// <inheritdoc />
    protected override FieldRegistration? Register(FormidableFormContext context)
    {
        _field = ResolveField();
        return null;
    }

    /// <inheritdoc />
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
