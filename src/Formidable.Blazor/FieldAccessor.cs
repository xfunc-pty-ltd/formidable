using System.Linq.Expressions;

namespace Formidable.Blazor;

/// <summary>
/// Resolves the accessor a component was handed for its field, or says what to write instead.
/// <see cref="Microsoft.AspNetCore.Components.Forms.FieldIdentifier.Create{TField}"/> throws a
/// bare <c>ArgumentNullException</c> naming its own parameter when handed nothing, which tells a
/// consumer neither which component is unhappy nor what the fix is — and
/// <c>[EditorRequired]</c> is only a build-time warning, so an omitted accessor reaches runtime.
/// </summary>
internal static class FieldAccessor
{
    /// <summary>
    /// The accessor for a component with no value binding of its own, where <c>For</c> is the only
    /// spelling there is.
    /// </summary>
    internal static Expression<Func<TValue>> RequireFor<TValue>(
        Expression<Func<TValue>>? forAccessor,
        Type componentType) =>
        forAccessor ?? throw new InvalidOperationException(
            $"{FriendlyTypeName.Of(componentType)} requires a For parameter (none was supplied) — set For to the " +
            "field's accessor, e.g. For=\"() => _order.Description\".");

    /// <summary>
    /// The accessor for an input, which can learn its field from either spelling: an explicit
    /// <c>For</c> wins when both are present, and otherwise the <c>ValueExpression</c> the Razor
    /// compiler supplies for every <c>@bind-Value</c> answers.
    /// </summary>
    internal static Expression<Func<TValue>> RequireBoundField<TValue>(
        Expression<Func<TValue>>? forAccessor,
        Expression<Func<TValue>>? valueExpression,
        Type componentType) =>
        forAccessor ?? valueExpression ?? throw new InvalidOperationException(
            $"{FriendlyTypeName.Of(componentType)} requires the field it edits (neither @bind-Value nor For was " +
            "supplied) — bind the value, e.g. @bind-Value=\"_order.Description\", or name the field explicitly, " +
            "e.g. For=\"() => _order.Description\".");
}
