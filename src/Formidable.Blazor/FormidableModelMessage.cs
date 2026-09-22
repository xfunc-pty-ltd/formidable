using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;

namespace Formidable.Blazor;

/// <summary>
/// Renders the form's model-level validation issues — the ones addressed to the form itself
/// rather than to any field — as the same persistent, accessible message list
/// <see cref="FormidableFieldMessage{TValue}"/> renders per field: the all-suppressed defensive
/// gate's explanation, model-level server-applied issues, and the incomplete-validation fault
/// issue, which <see cref="IFormValidationEngine.GetIssues"/> orders last. Parameterless, because
/// the field it speaks for is fixed: the model-level field (an empty
/// <see cref="FieldIdentifier.FieldName"/>), which no accessor expression can name. The list
/// element renders always — empty while the form has nothing to say — with the model-level
/// message id (<see cref="FormidableFieldId.MessagesFor(FieldIdentifier)"/>) and any configured
/// <see cref="FormidableOptions.InlineMessageLive"/> on it, so on a form that renders no
/// <see cref="FormidableSummary"/> the gate's explanation lands in a live region assistive
/// technology already knows about instead of nowhere. It registers nothing: the model-level
/// field's issues are always disclosed, because its element is the form's own, on the page for
/// as long as the form is.
/// </summary>
public sealed class FormidableModelMessage : FormidableComponentBase
{
    private FieldIdentifier _field;
    private string _messagesElementId = string.Empty;

    /// <summary>Additional attributes splatted onto the rendered list element.</summary>
    /// <remarks>
    /// The same policy as <see cref="FormidableMessageBase{TValue}.AdditionalAttributes"/>,
    /// because both render through one shared list implementation: a consumer-splatted
    /// <c>class</c> merges (splatted first, <c>formidable-message-list</c> after), a splatted
    /// <c>id</c> is ignored in favour of the model-level message id, and a configured
    /// <see cref="FormidableOptions.InlineMessageLive"/> wins a splatted <c>aria-live</c>.
    /// </remarks>
    [Parameter(CaptureUnmatchedValues = true)]
    public IReadOnlyDictionary<string, object>? AdditionalAttributes { get; set; }

    /// <summary>
    /// Resolves the model-level field from the context being bound and computes the list's id.
    /// Registers nothing: registration is the submit channel's client-side disclosure gate for
    /// fields, and the engine discloses the model-level field unconditionally — its element is
    /// the form's own, which is on the page for as long as the form is.
    /// </summary>
    /// <param name="context">The context now being bound.</param>
    /// <returns>Always null.</returns>
    protected override FieldRegistration? Register(FormidableFormContext context)
    {
        _field = new FieldIdentifier(context.EditContext.Model, string.Empty);
        _messagesElementId = FormidableFieldId.MessagesFor(_field);
        return null;
    }

    /// <inheritdoc />
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        if (Context is null)
        {
            return;
        }

        FormidableMessageList.Render(
            builder,
            AdditionalAttributes,
            _messagesElementId,
            (Context.Engine as IValidatingFieldReader)?.InlineMessageLive,
            Context.Engine.GetIssues(_field));
    }
}
