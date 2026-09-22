using System.Linq.Expressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;

namespace Formidable.Blazor;

/// <summary>
/// Shared lifecycle and rendering for field-level and collection-level message components:
/// resolves <see cref="For"/> to a <see cref="FieldIdentifier"/>, subscribes to the cascaded
/// engine's <see cref="IFormValidationEngine.StateChanged"/> so a validation pass re-renders the
/// list, and renders an accessible message list from <see cref="IFormValidationEngine.GetIssues"/>
/// — nothing when the field currently has no issues. This type is public only because a public
/// component cannot inherit a less accessible base; it is not an extension point, and unlike
/// <see cref="FormidableInputBase{TValue}"/> it is not meant to be one. The constructor is not
/// accessible outside this assembly, so <see cref="FormidableFieldMessage{TValue}"/> and
/// <see cref="FormidableCollectionMessage{TValue}"/> are the only two shapes; whether the field
/// is also registered with the field registry is the one thing they differ on (see
/// <see cref="Register"/>). <see cref="For"/> is (re-)read whenever
/// the cascaded <see cref="FormidableFormContext"/> is a new instance — including the first
/// render and again after a host such as <c>FormidableForm</c>/<c>FormidableValidator</c> swaps
/// its model and rebuilds its engine and registry — so any registration and the engine
/// subscription always target the currently-active context.
/// </summary>
/// <typeparam name="TValue">The field's value type (inferred from <see cref="For"/>).</typeparam>
public abstract class FormidableMessageBase<TValue> : ComponentBase, IDisposable
{
    private const string ErrorItemClass = "formidable-message formidable-message--error";
    private const string WarningItemClass = "formidable-message formidable-message--warning";
    private const string InfoItemClass = "formidable-message formidable-message--info";

    private readonly FormContextBinding _binding = new();
    private FieldIdentifier _field;
    private string _messagesElementId = string.Empty;

    private protected FormidableMessageBase()
    {
    }

    [CascadingParameter]
    private FormidableFormContext? Context { get; set; }

    /// <summary>Accessor for the field whose messages are rendered, e.g. <c>() => Model.Description</c>.</summary>
    [Parameter, EditorRequired]
    public Expression<Func<TValue>> For { get; set; } = default!;

    /// <summary>
    /// Registers <paramref name="field"/> with <paramref name="context"/>'s field registry, or
    /// returns null to skip registration. Messages are not inputs, so the base implementation
    /// (used by <see cref="FormidableFieldMessage{TValue}"/>) never registers;
    /// <see cref="FormidableCollectionMessage{TValue}"/> overrides this to mark its
    /// collection-level path revealed so collection-level rules
    /// surface even though the collection itself has no validated input registering it.
    /// </summary>
    private protected virtual FieldRegistration? Register(FormidableFormContext context, FieldIdentifier field) => null;

    /// <inheritdoc />
    protected override void OnParametersSet()
    {
        if (_binding.IsBound(Context))
        {
            return;
        }

        _binding.Update(
            Context,
            GetType(),
            register: context =>
            {
                _field = FieldIdentifier.Create(FieldAccessor.RequireFor(For, GetType()));
                _messagesElementId = FormidableFieldId.MessagesFor(FormidableFieldId.For(_field));
                return Register(context, _field);
            },
            stateChanged: OnEngineStateChanged);
    }

    /// <inheritdoc />
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        if (Context is null)
        {
            return;
        }

        var issues = Context.Engine.GetIssues(_field);
        if (issues.Count == 0)
        {
            return;
        }

        var sequence = 0;
        builder.OpenElement(sequence++, "ul");
        builder.AddAttribute(sequence++, "id", _messagesElementId);
        builder.AddAttribute(sequence++, "class", "formidable-messages");

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

    private void OnEngineStateChanged() => _ = InvokeAsync(StateHasChanged);

    /// <inheritdoc />
    public void Dispose() => _binding.Dispose();
}

/// <summary>
/// Renders a field's current validation issues (any severity) as an accessible message list;
/// renders nothing when the field has none. Does not register with the field registry — messages
/// are not inputs, so pairing a message with a validated input (or a
/// <see cref="FormidableFieldAnchor{TValue}"/>) elsewhere in the form is what keeps the field
/// revealed.
/// </summary>
/// <typeparam name="TValue">The field's value type (inferred from <see cref="FormidableMessageBase{TValue}.For"/>).</typeparam>
public sealed class FormidableFieldMessage<TValue> : FormidableMessageBase<TValue>
{
}
