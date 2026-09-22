using System.Linq.Expressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;

namespace Formidable.Blazor;

/// <summary>
/// Shared accessor for the kit's field-scoped components: the <see cref="For"/> parameter naming
/// the field, and the <see cref="FieldIdentifier"/> it resolves to for registration, row-key
/// verification, and whatever else the component built on it needs the field for.
/// </summary>
/// <remarks>
/// Public only because a public component cannot inherit a less accessible base; it is not an
/// extension point, and unlike <see cref="FormidableInputBase{TValue}"/> it is not meant to be
/// one. Its constructor is not accessible outside this assembly, so
/// <see cref="FormidableField{TValue}"/>, <see cref="FormidableFieldAnchor{TValue}"/>,
/// <see cref="FormidableRequiredIndicator{TValue}"/> and <see cref="FormidableMessageBase{TValue}"/>
/// are the shapes built on it.
/// </remarks>
/// <typeparam name="TValue">
/// The accessor's type, inferred from <see cref="For"/>: the field's own value type, or
/// <c>object</c> where a shared component forwards an
/// <c>Expression&lt;Func&lt;object&gt;&gt;</c>.
/// </typeparam>
public abstract class FormidableAccessorComponentBase<TValue> : FormidableComponentBase
{
    private protected FormidableAccessorComponentBase()
    {
    }

    /// <summary>
    /// Accessor for the field this component renders, registers, marks, or reports messages for,
    /// e.g. <c>() => Model.Description</c>.
    /// </summary>
    [Parameter, EditorRequired]
    public Expression<Func<TValue>> For { get; set; } = default!;

    /// <inheritdoc />
    private protected override FieldIdentifier ResolveField() =>
        FieldIdentifier.Create(FieldAccessor.RequireFor(For, GetType()));
}
