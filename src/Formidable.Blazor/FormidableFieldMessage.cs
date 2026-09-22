using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;

namespace Formidable.Blazor;

/// <summary>The base of the two field-scoped message components: it renders a field's issues, any severity, as a list that is always present, empty when there are none.</summary>
/// <typeparam name="TValue">The accessor's type, inferred from <see cref="FormidableAccessorComponentBase{TValue}.For"/>: the field's own value type, or <c>object</c> where a shared component forwards an <c>Expression&lt;Func&lt;object&gt;&gt;</c>.</typeparam>
/// <remarks>Not an extension point (the constructor is not accessible outside the assembly).</remarks>
// The list renders always, empty when the field has no issues, so a consumer's CSS can
// transition its opening and closing, and so a configured InlineMessageLive sits on an element
// that persists across renders rather than one that enters alongside the text it announces.
// Public only because a public component cannot inherit a less accessible base.
public abstract class FormidableMessageBase<TValue> : FormidableAccessorComponentBase<TValue>
{
    private FieldIdentifier _field;
    private string _messagesElementId = string.Empty;

    private protected FormidableMessageBase()
    {
    }

    /// <summary>Attributes splatted onto the list element ahead of the computed values: <c>class</c> merges (the splatted value first), <c>id</c> is dropped, and <c>aria-live</c> is <see cref="FormidableOptions.InlineMessageLive"/>'s while that is set.</summary>
    [Parameter(CaptureUnmatchedValues = true)]
    public IReadOnlyDictionary<string, object>? AdditionalAttributes { get; set; }

    /// <summary>Registers <paramref name="field"/> with <paramref name="context"/>'s registry, or returns <see langword="null"/>; the base registers nothing, and <see cref="FormidableCollectionMessage{TValue}"/> overrides it to register its collection-level path.</summary>
    /// <param name="context">The context being bound.</param>
    /// <param name="field">The field these messages speak for.</param>
    /// <returns>The registration the base releases on the next rebind or on disposal, or <see langword="null"/>.</returns>
    private protected virtual FieldRegistration? RegisterField(FormidableFormContext context, FieldIdentifier field) => null;

    /// <summary>Resolves the field <see cref="FormidableAccessorComponentBase{TValue}.For"/> names, computes the list's id, and hands registration to <see cref="RegisterField"/>.</summary>
    /// <param name="context">The context being bound.</param>
    /// <returns>Whatever <see cref="RegisterField"/> returns.</returns>
    protected sealed override FieldRegistration? Register(FormidableFormContext context)
    {
        _field = ResolveField();
        _messagesElementId = FormidableFieldId.MessagesFor(_field);
        return RegisterField(context, _field);
    }

    /// <summary>Renders the list through <see cref="FormidableMessageList"/> with the field's current issues and any <see cref="FormidableOptions.InlineMessageLive"/> the engine reports; renders nothing before the first bind.</summary>
    /// <param name="builder">The render tree builder.</param>
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

/// <summary>The one renderer of the kit's message list, shared by <see cref="FormidableMessageBase{TValue}"/> and <see cref="FormidableModelMessage"/>: an always-present <c>ul</c> carrying the messages id, the structural classes and one severity-classed item per issue.</summary>
// One place for the persistent-list contract (the always-rendered ul, the id that is the
// aria-describedby target, the structural classes, the severity-classed items) to be right. The
// splat enters first and the computed values after, so last-write-wins hands the computed id,
// the merged class and any configured aria-live the duplicate-attribute race.
internal static class FormidableMessageList
{
    private const string ErrorItemClass = "formidable-message formidable-message--error";
    private const string WarningItemClass = "formidable-message formidable-message--warning";
    private const string InfoItemClass = "formidable-message formidable-message--info";

    /// <summary>Renders the list: the splat, then the id, the merged class and <paramref name="live"/> as <c>aria-live</c> where set, then one <c>li</c> per issue classed by severity.</summary>
    /// <param name="builder">The render tree builder.</param>
    /// <param name="additionalAttributes">The consumer's splatted attributes, or <see langword="null"/>.</param>
    /// <param name="listElementId">The id the list carries, as <see cref="FormidableFieldId.MessagesFor(FieldIdentifier)"/> derives it.</param>
    /// <param name="live">The <c>aria-live</c> value, or <see langword="null"/> to render none.</param>
    /// <param name="issues">The issues to list, in the order given.</param>
    internal static void Render(
        RenderTreeBuilder builder,
        IReadOnlyDictionary<string, object>? additionalAttributes,
        string listElementId,
        string? live,
        IReadOnlyList<ValidationIssue> issues)
    {
        var sequence = 0;
        builder.OpenElement(sequence++, "ul");
        builder.AddMultipleAttributes(sequence++, additionalAttributes!);
        builder.AddAttribute(sequence++, "id", listElementId);
        builder.AddAttribute(sequence++, "class", FormidableCss.CombineClassNames(additionalAttributes, "formidable-message-list"));
        if (live is not null)
        {
            builder.AddAttribute(sequence++, "aria-live", live);
        }

        foreach (var issue in issues)
        {
            builder.OpenElement(sequence++, "li");
            builder.AddAttribute(
                sequence++,
                "class",
                FormidableCss.SelectBySeverity(issue.Severity, ErrorItemClass, WarningItemClass, InfoItemClass));
            builder.AddContent(sequence++, issue.Message);
            builder.CloseElement();
        }

        builder.CloseElement();
    }
}

/// <summary>Renders a field's current issues, any severity, as a list that is always present; it registers nothing, so the field's submit errors show where something else registers it, such as the input beside it.</summary>
/// <typeparam name="TValue">The accessor's type, inferred from <see cref="FormidableAccessorComponentBase{TValue}.For"/>: the field's own value type, or <c>object</c> where a shared component forwards an <c>Expression&lt;Func&lt;object&gt;&gt;</c>.</typeparam>
public sealed class FormidableFieldMessage<TValue> : FormidableMessageBase<TValue>
{
}
