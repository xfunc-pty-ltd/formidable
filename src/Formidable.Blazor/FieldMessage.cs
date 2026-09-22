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
/// — nothing when the field currently has no issues. The constructor is not accessible outside
/// this assembly, so <see cref="FieldMessage{TValue}"/> and <see cref="CollectionMessage{TValue}"/>
/// are the only two shapes; whether the field is also registered with the field registry is the
/// one thing they differ on (see <see cref="Register"/>). <see cref="For"/> is (re-)read whenever
/// the cascaded <see cref="FormidableFormContext"/> is a new instance — including the first
/// render and again after a host such as <c>FormidableForm</c>/<c>FormidableValidator</c> swaps
/// its model and rebuilds its engine and registry — so any registration and the engine
/// subscription always target the currently-active context.
/// </summary>
/// <typeparam name="TValue">The field's value type (inferred from <see cref="For"/>).</typeparam>
public abstract class FieldMessageBase<TValue> : ComponentBase, IDisposable
{
    private readonly FormContextBinding _binding = new();
    private FieldIdentifier _field;

    private protected FieldMessageBase()
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
    /// (used by <see cref="FieldMessage{TValue}"/>) never registers; <see cref="CollectionMessage{TValue}"/>
    /// overrides this to mark its collection-level path revealed so collection-level rules
    /// surface even though the collection itself has no validated input registering it.
    /// </summary>
    private protected virtual FieldRegistration? Register(FormidableFormContext context, FieldIdentifier field) => null;

    /// <inheritdoc />
    protected override void OnParametersSet() =>
        _binding.Update(
            Context,
            GetType(),
            register: context =>
            {
                _field = FieldIdentifier.Create(For);
                return Register(context, _field);
            },
            stateChanged: OnEngineStateChanged);

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
        builder.AddAttribute(sequence++, "id", $"{FormidableFieldId.For(_field)}-messages");
        builder.AddAttribute(sequence++, "class", "formidable-messages");

        foreach (var issue in issues)
        {
            var severitySuffix = issue.Severity switch
            {
                ValidationSeverity.Error => "--error",
                ValidationSeverity.Warning => "--warning",
                _ => "--info"
            };

            builder.OpenElement(sequence++, "li");
            builder.AddAttribute(sequence++, "class", $"formidable-message formidable-message{severitySuffix}");
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
/// are not inputs, so pairing a message with a validated input (or a <see cref="FieldAnchor{TValue}"/>)
/// elsewhere in the form is what keeps the field revealed.
/// </summary>
/// <typeparam name="TValue">The field's value type (inferred from <see cref="FieldMessageBase{TValue}.For"/>).</typeparam>
public sealed class FieldMessage<TValue> : FieldMessageBase<TValue>
{
}
