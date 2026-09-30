using System.Runtime.InteropServices;
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

    /// <summary>The content of each item, handed a <see cref="VisibleIssue"/> for this list's field with its <see cref="VisibleIssue.DisplayName"/>; the item and its severity class stay the component's. Defaults to <see langword="null"/>, which renders the issue's <see cref="ValidationIssue.Message"/>.</summary>
    [Parameter]
    public RenderFragment<VisibleIssue>? ItemTemplate { get; set; }

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

    /// <summary>Renders the list through <see cref="FormidableMessageList"/> with the field's current issues, <see cref="ItemTemplate"/> and any <see cref="FormidableOptions.InlineMessageLive"/> the engine reports; renders nothing before the first bind.</summary>
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

    /// <summary>Renders the list: the splat, then the id, the merged class and the engine's <see cref="FormidableOptions.InlineMessageLive"/> as <c>aria-live</c> where set, then one <c>li</c> per issue the engine holds for <paramref name="field"/>, classed by severity, holding the message or the template's content.</summary>
    /// <param name="builder">The render tree builder.</param>
    /// <param name="additionalAttributes">The consumer's splatted attributes, or <see langword="null"/>.</param>
    /// <param name="listElementId">The id the list carries, as <see cref="FormidableFieldId.MessagesFor(FieldIdentifier)"/> derives it.</param>
    /// <param name="engine">The form's engine, read at this render for the live-message setting, the field's issues in the order it lists them, and <see cref="FormidableOptions.ModelLevelDisplayName"/> for an issue that names no field of its own.</param>
    /// <param name="field">The field the list speaks for, handed to <paramref name="itemTemplate"/> with each issue.</param>
    /// <param name="itemTemplate">The content of each item, or <see langword="null"/> to render the issue's message.</param>
    // With a template, each item keeps the element, the class and the sequence slot the message
    // takes without one, so turning a template on or off changes only what the item holds.
    internal static void Render(
        RenderTreeBuilder builder,
        IReadOnlyDictionary<string, object>? additionalAttributes,
        string listElementId,
        IFormidableEngine engine,
        FieldIdentifier field,
        RenderFragment<VisibleIssue>? itemTemplate)
    {
        var live = (engine as IValidatingFieldReader)?.InlineMessageLive;
        var issues = engine.GetIssues(field);
        var modelLevelDisplayName = engine.Options.ModelLevelDisplayName;

        builder.OpenElement(0, "ul");
        builder.AddMultipleAttributes(1, additionalAttributes!);
        builder.AddAttribute(2, "id", listElementId);
        builder.AddAttribute(3, "class", FormidableCss.CombineClassNames(additionalAttributes, "formidable-message-list"));
        if (live is not null)
        {
            builder.AddAttribute(4, "aria-live", live);
        }

        // Each item is keyed, so a message keeps its own item, and the template instance inside
        // it, when one above it clears. Unkeyed, siblings match by position: the message that
        // moved up would take over the cleared one's item, template state included, and a list
        // with InlineMessageLive set would announce it again for saying what it already said.
        // The key is the summary's: the issue's IssueKey paired with its ordinal among the items
        // carrying an equal key, counted on that same key, so two issues alike but for their
        // state stay two keys. A list of one issue cannot collide with itself and counts nothing.
        //
        // The items render inside a region of their own, and each numbers its frames from zero
        // (what a Razor @foreach compiles to). A key decides which old item a new one matches,
        // but the frames inside it still match by sequence number, so under a running counter a
        // matched item's content would be rebuilt inside an item that kept its key. The region
        // also keeps the items on the same numbers whether or not aria-live is set.
        var occurrences = issues.Count > 1 ? new Dictionary<IssueKey, int>(issues.Count) : null;

        builder.OpenRegion(5);

        foreach (var issue in issues)
        {
            var key = IssueKey.Of(field, issue);
            var occurrence = 0;
            if (occurrences is not null)
            {
                ref var count = ref CollectionsMarshal.GetValueRefOrAddDefault(occurrences, key, out _);
                occurrence = count++;
            }

            var itemSequence = 0;
            builder.OpenElement(itemSequence++, "li");
            builder.SetKey((key, occurrence));
            builder.AddAttribute(
                itemSequence++,
                "class",
                FormidableCss.SelectBySeverity(issue.Severity, ErrorItemClass, WarningItemClass, InfoItemClass));
            if (itemTemplate is null)
            {
                builder.AddContent(itemSequence, issue.Message);
            }
            else
            {
                builder.AddContent(itemSequence, itemTemplate, IssueDisplayName.Named(field, issue, modelLevelDisplayName));
            }

            builder.CloseElement();
        }

        builder.CloseRegion();

        builder.CloseElement();
    }
}

/// <summary>Renders a field's current issues, any severity, as a list that is always present; it registers nothing, so the field's submit errors show where something else registers it, such as the input beside it.</summary>
/// <typeparam name="TValue">The accessor's type, inferred from <see cref="FormidableAccessorComponentBase{TValue}.For"/>: the field's own value type, or <c>object</c> where a shared component forwards an <c>Expression&lt;Func&lt;object&gt;&gt;</c>.</typeparam>
public sealed class FormidableFieldMessage<TValue> : FormidableMessageBase<TValue>
{
}
