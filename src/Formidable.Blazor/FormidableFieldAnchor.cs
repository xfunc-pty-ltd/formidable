using Microsoft.AspNetCore.Components;

namespace Formidable.Blazor;

/// <summary>Registers its field and renders nothing, so a control Formidable does not wrap can show its errors at submit; <see cref="FieldRegistry"/> says what registration decides.</summary>
/// <typeparam name="TValue">The accessor's type, inferred from <see cref="FormidableAccessorComponentBase{TValue}.For"/>: the field's own value type, or <c>object</c> where a shared component forwards an <c>Expression&lt;Func&lt;object&gt;&gt;</c>.</typeparam>
public sealed class FormidableFieldAnchor<TValue> : FormidableAccessorComponentBase<TValue>
{
    /// <summary>Whether the field stays registered after this component is disposed, for rows a <c>Virtualize</c> container disposes while they remain in the form. Defaults to <see langword="false"/>.</summary>
    [Parameter]
    public bool KeepRegistered { get; set; }

    /// <summary><see langword="false"/>: an anchor renders nothing, so it subscribes to no state change.</summary>
    protected override bool ObservesEngineState => false;

    /// <summary>Registers the field <see cref="FormidableAccessorComponentBase{TValue}.For"/> names with <paramref name="context"/>'s registry under <see cref="KeepRegistered"/>.</summary>
    /// <param name="context">The context being bound.</param>
    /// <returns>The registration the base releases on the next rebind or on disposal.</returns>
    protected override FieldRegistration? Register(FormidableFormContext context) =>
        context.Registry.Register(ResolveField(), KeepRegistered);
}
