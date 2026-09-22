using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;

namespace Formidable.Blazor;

/// <summary>
/// Renders a collection-level field's current validation issues (any severity) as an accessible
/// message list — identical rendering to <see cref="FormidableFieldMessage{TValue}"/>, so the
/// list itself renders always, empty when the field has no issues — and additionally registers
/// the field with the field registry, so a collection-level rule's issues are treated as revealed
/// even though the collection itself (e.g. a <c>List&lt;T&gt;</c> property) has no validated
/// input of its own to register it.
/// </summary>
/// <typeparam name="TValue">
/// The accessor's type, inferred from <see cref="FormidableMessageBase{TValue}.For"/>: the field's own value type, or
/// <c>object</c> where a shared component forwards an
/// <c>Expression&lt;Func&lt;object&gt;&gt;</c>.
/// </typeparam>
public sealed class FormidableCollectionMessage<TValue> : FormidableMessageBase<TValue>
{
    /// <summary>Keeps the field registered after disposal — for virtualized containers.</summary>
    [Parameter]
    public bool KeepRegistered { get; set; }

    private protected override FieldRegistration? RegisterField(FormidableFormContext context, FieldIdentifier field) =>
        context.Registry.Register(field, KeepRegistered);
}
