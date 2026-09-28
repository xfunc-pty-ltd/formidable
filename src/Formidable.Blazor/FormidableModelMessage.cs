using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;

namespace Formidable.Blazor;

/// <summary>Renders the form's model-level messages, such as the gate's explanation, a server issue with an empty path and, last, the <see cref="FormidableOptions.ValidationFaultMessage"/>, as the same always-present list <see cref="FormidableFieldMessage{TValue}"/> renders for a field.</summary>
/// <remarks>
/// Render it on a form with no <see cref="FormidableSummary"/>, so the gate's explanation has an
/// element to land on, with any <see cref="FormidableOptions.InlineMessageLive"/> on it.
/// </remarks>
public sealed class FormidableModelMessage : FormidableComponentBase
{
    private FieldIdentifier _field;
    private string _messagesElementId = string.Empty;

    /// <summary>Attributes splatted onto the list element ahead of the computed values, as <see cref="FormidableMessageBase{TValue}.AdditionalAttributes"/> describes: <c>class</c> merges, <c>id</c> is dropped, <c>aria-live</c> is the option's while set.</summary>
    [Parameter(CaptureUnmatchedValues = true)]
    public IReadOnlyDictionary<string, object>? AdditionalAttributes { get; set; }

    /// <summary>The content of each item, handed a <see cref="VisibleIssue"/> for the model-level field with its <see cref="VisibleIssue.DisplayName"/>; the item and its severity class stay the component's. Defaults to <see langword="null"/>, which renders the issue's <see cref="ValidationIssue.Message"/>.</summary>
    [Parameter]
    public RenderFragment<VisibleIssue>? ItemTemplate { get; set; }

    /// <summary>Resolves the model-level field from <paramref name="context"/>'s model, computes the list's id, and registers nothing.</summary>
    /// <param name="context">The context being bound.</param>
    /// <returns>Always <see langword="null"/>.</returns>
    // Registration is what lets the submit channel show a field's errors, and the engine
    // discloses the model-level field unconditionally: its element is the form's own, on the
    // page for as long as the form is.
    protected override FieldRegistration? Register(FormidableFormContext context)
    {
        _field = new FieldIdentifier(context.EditContext.Model, string.Empty);
        _messagesElementId = FormidableFieldId.MessagesFor(_field);
        return null;
    }

    /// <summary>Renders the list through <see cref="FormidableMessageList"/> with the model-level field's current issues, <see cref="ItemTemplate"/> and any <see cref="FormidableOptions.InlineMessageLive"/> the engine reports; renders nothing before the first bind.</summary>
    /// <param name="builder">The render tree builder.</param>
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        if (Context is null)
        {
            return;
        }

        FormidableMessageList.Render(builder, AdditionalAttributes, _messagesElementId, Context.Engine, _field, ItemTemplate);
    }
}
