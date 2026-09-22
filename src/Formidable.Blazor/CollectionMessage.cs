using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;

namespace Formidable.Blazor;

/// <summary>
/// Renders a collection-level field's current validation issues (any severity) as an accessible
/// message list — identical rendering to <see cref="FieldMessage{TValue}"/> — and additionally
/// registers the field with the field registry, so a collection-level rule's issues are treated
/// as revealed even though the collection itself (e.g. a <c>List&lt;T&gt;</c> property) has no
/// validated input of its own to register it. Renders nothing when the field has no issues.
/// </summary>
/// <typeparam name="TValue">The field's value type (inferred from <see cref="FieldMessageBase{TValue}.For"/>).</typeparam>
public sealed class CollectionMessage<TValue> : FieldMessageBase<TValue>
{
    /// <summary>Keeps the field revealed after disposal — for virtualized containers.</summary>
    [Parameter]
    public bool KeepRegistered { get; set; }

    private protected override FieldRegistration? Register(FormidableFormContext context, FieldIdentifier field) =>
        context.Registry.Register(field, KeepRegistered);
}
