using Microsoft.AspNetCore.Components;

namespace Formidable.Blazor;

/// <summary>Registers its field and renders nothing, so a control Formidable does not wrap can show its errors at submit; <see cref="FieldRegistry"/> says what registration decides.</summary>
/// <typeparam name="TValue">The accessor's type, inferred from <see cref="FormidableAccessorComponentBase{TValue}.For"/>: the field's own value type, or <c>object</c> where a shared component forwards an <c>Expression&lt;Func&lt;object&gt;&gt;</c>.</typeparam>
public sealed class FormidableFieldAnchor<TValue> : FormidableAccessorComponentBase<TValue>
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

    /// <summary><see langword="false"/>: an anchor renders nothing, so it subscribes to no state change.</summary>
    protected override bool ObservesEngineState => false;

    /// <summary>Returns <see cref="WaitForSubmit"/>.</summary>
    private protected override bool HoldsLiveMessages => WaitForSubmit;

    /// <summary>Registers the field <see cref="FormidableAccessorComponentBase{TValue}.For"/> names with <paramref name="context"/>'s registry under <see cref="KeepRegistered"/> and <see cref="WaitForSubmit"/>.</summary>
    /// <param name="context">The context being bound.</param>
    /// <returns>The registration the base releases on the next rebind or on disposal.</returns>
    protected override FieldRegistration? Register(FormidableFormContext context) =>
        context.Registry.RegisterWithHold(ResolveField(), KeepRegistered, HoldsLiveMessages);
}
