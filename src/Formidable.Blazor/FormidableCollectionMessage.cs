using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;

namespace Formidable.Blazor;

/// <summary>Renders a collection-level field's issues as <see cref="FormidableFieldMessage{TValue}"/> does and also registers the field, so a rule on the collection itself (a <c>List&lt;T&gt;</c> property no input registers) can show its errors at submit.</summary>
/// <typeparam name="TValue">The accessor's type, inferred from <see cref="FormidableAccessorComponentBase{TValue}.For"/>: the field's own value type, or <c>object</c> where a shared component forwards an <c>Expression&lt;Func&lt;object&gt;&gt;</c>.</typeparam>
public sealed class FormidableCollectionMessage<TValue> : FormidableMessageBase<TValue>
{
    /// <summary>Whether the field stays registered after this component is disposed, for rows a <c>Virtualize</c> container disposes while they remain in the form. Defaults to <see langword="false"/>.</summary>
    [Parameter]
    public bool KeepRegistered { get; set; }

    /// <summary>Whether the field's messages wait for the form's first submit or server reply, while its rules still run. Defaults to <see langword="false"/>.</summary>
    /// <remarks>
    /// A field waits while any component registered for it sets this, and a change takes effect
    /// at the component's next render. A kit input's
    /// <see cref="FormidableInputBase{TValue}.UpdateOn"/> still decides which event commits the
    /// value, and a passing value still earns the valid class.
    /// <see cref="IFormidableEngine.IsFormValid"/> does not wait, so a Submit button disabled on it
    /// can refuse the one click that would show why.
    /// </remarks>
    [Parameter]
    public bool WaitForSubmit { get; set; }

    /// <summary>Returns <see cref="WaitForSubmit"/>.</summary>
    private protected override bool HoldsLiveMessages => WaitForSubmit;

    /// <summary>Registers <paramref name="field"/> with <paramref name="context"/>'s registry under <see cref="KeepRegistered"/> and <see cref="WaitForSubmit"/>.</summary>
    /// <param name="context">The context being bound.</param>
    /// <param name="field">The collection-level field.</param>
    /// <returns>The registration the base releases on the next rebind or on disposal.</returns>
    private protected override FieldRegistration? RegisterField(FormidableFormContext context, FieldIdentifier field) =>
        context.Registry.RegisterWithHold(field, KeepRegistered, HoldsLiveMessages);
}
