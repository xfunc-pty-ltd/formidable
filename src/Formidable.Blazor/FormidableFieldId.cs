using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Components.Forms;

namespace Formidable.Blazor;

/// <summary>
/// Deterministic DOM element ids for fields, shared by inputs (element id), messages
/// (aria-describedby target), and the focus service. Identity follows the owning object
/// instance plus the field name.
/// </summary>
public static class FormidableFieldId
{
    /// <summary>The id for a field: <c>formidable-{owner-hash}-{sanitized-name}</c>; the model-level field uses <c>form</c> as its name.</summary>
    public static string For(FieldIdentifier field)
    {
        var name = field.FieldName.Length == 0
            ? "form"
            : string.Concat(field.FieldName.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-'));
        return $"formidable-{RuntimeHelpers.GetHashCode(field.Model):x8}-{name}";
    }

    /// <summary>
    /// The id for a field named by a member-access expression, e.g. <c>o =&gt; o.Description</c> —
    /// equivalent to <c>For(new FieldIdentifier(model, nameof(Order.Description)))</c>, without the
    /// stringly-typed <c>nameof</c> step. There is no expression shape for the model-level field
    /// (an empty field name): construct that <see cref="FieldIdentifier"/> directly and pass it to
    /// <see cref="For(FieldIdentifier)"/> instead.
    /// </summary>
    /// <typeparam name="TModel">The type owning the field.</typeparam>
    /// <typeparam name="TValue">The field's value type.</typeparam>
    /// <param name="model">The field's owning instance.</param>
    /// <param name="accessor">A member-access expression naming the field, e.g. <c>o =&gt; o.Description</c>.</param>
    public static string For<TModel, TValue>(TModel model, Expression<Func<TModel, TValue>> accessor)
        where TModel : class
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(accessor);

        var member = accessor.Body switch
        {
            MemberExpression memberExpression => memberExpression,
            UnaryExpression { Operand: MemberExpression memberExpression } => memberExpression,
            _ => throw new ArgumentException(
                $"'{accessor}' is not a member access expression, e.g. 'o => o.Description'.",
                nameof(accessor)),
        };

        return For(new FieldIdentifier(model, member.Member.Name));
    }
}
