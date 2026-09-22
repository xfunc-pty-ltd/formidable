using System.Linq.Expressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;

namespace Formidable.Blazor;

/// <summary>
/// Shared lifecycle and rendering for field-level and collection-level message components:
/// resolves <see cref="For"/> to a <see cref="FieldIdentifier"/>, subscribes to the cascaded
/// engine's <see cref="IFormValidationEngine.StateChanged"/> so a validation pass re-renders the
/// list, and renders an accessible message list from <see cref="IFormValidationEngine.GetIssues"/>.
/// The list element renders always — empty when the field currently has no issues — so a
/// consumer's CSS can transition its opening and closing, and so a configured
/// <see cref="FormidableOptions.InlineMessageRole"/> sits on an element that persists across
/// renders rather than one that enters alongside the text it announces. This type is public only
/// because a public component cannot inherit a less accessible base; it is not an extension
/// point, and unlike
/// <see cref="FormidableInputBase{TValue}"/> it is not meant to be one. The constructor is not
/// accessible outside this assembly, so <see cref="FormidableFieldMessage{TValue}"/> and
/// <see cref="FormidableCollectionMessage{TValue}"/> are the only two shapes; whether the field
/// is also registered with the field registry is the one thing they differ on (see
/// <see cref="RegisterField"/>). <see cref="For"/> is (re-)read whenever
/// the cascaded <see cref="FormidableFormContext"/> is a new instance — including the first
/// render and again after a host such as <c>FormidableForm</c>/<c>FormidableValidator</c> swaps
/// its model and rebuilds its engine and registry — so any registration and the engine
/// subscription always target the currently-active context.
/// </summary>
/// <typeparam name="TValue">The field's value type (inferred from <see cref="For"/>).</typeparam>
public abstract class FormidableMessageBase<TValue> : FormidableComponentBase
{
    private FieldIdentifier _field;
    private string _messagesElementId = string.Empty;

    private protected FormidableMessageBase()
    {
    }

    /// <summary>Accessor for the field whose messages are rendered, e.g. <c>() => Model.Description</c>.</summary>
    [Parameter, EditorRequired]
    public Expression<Func<TValue>> For { get; set; } = default!;

    /// <summary>Additional attributes splatted onto the rendered list element.</summary>
    /// <remarks>
    /// The kit's usual splat policy, stated for this element: the splat enters the render tree
    /// first and the computed attributes after, so the computed values win the
    /// duplicate-attribute race (Blazor applies last-write-wins). A consumer-splatted
    /// <c>class</c> is merged rather than replaced — the splatted value first, then
    /// <c>formidable-message-list</c>. A consumer-splatted <c>id</c> is ignored, because the
    /// rendered id is the <c>aria-describedby</c> contract
    /// (<see cref="FormidableFieldId.MessagesFor(FieldIdentifier)"/>) that every input
    /// describing itself by this list points at. And while
    /// <see cref="FormidableOptions.InlineMessageRole"/> is set, the <c>role</c> it configures
    /// wins a splatted one — the list's live-region behaviour is that option's to decide,
    /// form-wide; with the option unset the kit computes no <c>role</c>, so a splatted one
    /// stands.
    /// </remarks>
    [Parameter(CaptureUnmatchedValues = true)]
    public IReadOnlyDictionary<string, object>? AdditionalAttributes { get; set; }

    /// <summary>
    /// Registers <paramref name="field"/> with <paramref name="context"/>'s field registry, or
    /// returns null to skip registration. Messages are not inputs, so the base implementation
    /// (used by <see cref="FormidableFieldMessage{TValue}"/>) never registers;
    /// <see cref="FormidableCollectionMessage{TValue}"/> overrides this to mark its
    /// collection-level path revealed so collection-level rules
    /// surface even though the collection itself has no validated input registering it.
    /// </summary>
    private protected virtual FieldRegistration? RegisterField(FormidableFormContext context, FieldIdentifier field) => null;

    /// <inheritdoc />
    private protected sealed override FieldIdentifier ResolveField() =>
        FieldIdentifier.Create(FieldAccessor.RequireFor(For, GetType()));

    /// <summary>
    /// Resolves <see cref="For"/> to the field these messages speak for, computes the id the list
    /// renders — the target every <c>aria-describedby</c> for that field points at — and then
    /// hands the registration decision to <see cref="RegisterField"/>, the one thing the two
    /// message components differ on.
    /// </summary>
    /// <param name="context">The context now being bound.</param>
    /// <returns>Whatever <see cref="RegisterField"/> returns.</returns>
    protected sealed override FieldRegistration? Register(FormidableFormContext context)
    {
        _field = ResolveField();
        _messagesElementId = FormidableFieldId.MessagesFor(FormidableFieldId.For(_field));
        return RegisterField(context, _field);
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
            (Context.Engine as IValidatingFieldReader)?.InlineMessageRole,
            Context.Engine.GetIssues(_field));
    }
}

/// <summary>
/// The one implementation of the kit's message-list markup, shared by
/// <see cref="FormidableMessageBase{TValue}"/> and <see cref="FormidableModelMessage"/> so the
/// persistent-list contract — the always-rendered <c>ul</c>, the id that is the
/// <c>aria-describedby</c> target, the structural classes, and the severity-classed items — has
/// one place to be right. The splat policy is the kit's usual one: the consumer's attributes
/// enter the render tree first and the computed values after, so Blazor's last-write-wins hands
/// the computed <c>id</c>, the merged <c>class</c> and any configured <c>role</c> the
/// duplicate-attribute race.
/// </summary>
internal static class FormidableMessageList
{
    private const string ErrorItemClass = "formidable-message formidable-message--error";
    private const string WarningItemClass = "formidable-message formidable-message--warning";
    private const string InfoItemClass = "formidable-message formidable-message--info";

    internal static void Render(
        RenderTreeBuilder builder,
        IReadOnlyDictionary<string, object>? additionalAttributes,
        string listElementId,
        string? role,
        IReadOnlyList<ValidationIssue> issues)
    {
        var sequence = 0;
        builder.OpenElement(sequence++, "ul");
        builder.AddMultipleAttributes(sequence++, additionalAttributes!);
        builder.AddAttribute(sequence++, "id", listElementId);
        builder.AddAttribute(sequence++, "class", FormidableCss.CombineClassNames(additionalAttributes, "formidable-message-list"));
        if (role is not null)
        {
            builder.AddAttribute(sequence++, "role", role);
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

/// <summary>
/// Renders a field's current validation issues (any severity) as an accessible message list; the
/// list itself renders always, empty when the field has none. Does not register with the field
/// registry — messages are not inputs, so pairing a message with a validated input (or a
/// <see cref="FormidableFieldAnchor{TValue}"/>) elsewhere in the form is what keeps the field
/// revealed.
/// </summary>
/// <typeparam name="TValue">The field's value type (inferred from <see cref="FormidableMessageBase{TValue}.For"/>).</typeparam>
public sealed class FormidableFieldMessage<TValue> : FormidableMessageBase<TValue>
{
}
