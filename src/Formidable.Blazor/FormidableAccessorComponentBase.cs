using System.Linq.Expressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;

namespace Formidable.Blazor;

/// <summary>The base of the kit's field-scoped components, which name their field with a <see cref="For"/> accessor.</summary>
/// <typeparam name="TValue">The accessor's type, inferred from <see cref="For"/>: the field's own value type, or <c>object</c> where a shared component forwards an <c>Expression&lt;Func&lt;object&gt;&gt;</c>.</typeparam>
/// <remarks>Not an extension point (the constructor is not accessible outside the assembly).</remarks>
// Public only because a public component cannot inherit a less accessible base.
public abstract class FormidableAccessorComponentBase<TValue> : FormidableComponentBase
{
    private protected FormidableAccessorComponentBase()
    {
    }

    /// <summary>The accessor naming the field this component works on, such as <c>() => Model.Description</c>. Required.</summary>
    [Parameter, EditorRequired]
    public Expression<Func<TValue>> For { get; set; } = default!;

    /// <summary>The field <see cref="For"/> names at this moment.</summary>
    /// <returns>The identifier <see cref="FieldIdentifier.Create{TField}"/> builds from <see cref="For"/>.</returns>
    /// <exception cref="InvalidOperationException"><see cref="For"/> is unset; the message names the component and the fix.</exception>
    private protected override FieldIdentifier ResolveField() =>
        FieldIdentifier.Create(FieldAccessor.RequireFor(For, GetType()));
}
