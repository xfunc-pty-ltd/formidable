using System.Linq.Expressions;

namespace Formidable.Blazor;

/// <summary>Resolves the field accessor a component was handed, or throws a message naming the component and the fix.</summary>
// FieldIdentifier.Create<TField> throws a bare ArgumentNullException naming its own parameter
// when handed nothing, which tells a consumer neither which component is unhappy nor what the
// fix is, and [EditorRequired] is only a build-time warning, so an omitted accessor reaches
// runtime.
internal static class FieldAccessor
{
    /// <summary>Returns the <c>For</c> accessor of a component with no value binding of its own, or throws when none was supplied.</summary>
    /// <typeparam name="TValue">The field's value type.</typeparam>
    /// <param name="forAccessor">The component's <c>For</c> parameter.</param>
    /// <param name="componentType">The component's type, named in the exception.</param>
    /// <returns>The accessor.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="forAccessor"/> is <see langword="null"/>.</exception>
    internal static Expression<Func<TValue>> RequireFor<TValue>(
        Expression<Func<TValue>>? forAccessor,
        Type componentType) =>
        forAccessor ?? throw new InvalidOperationException(
            $"{FriendlyTypeName.Of(componentType)} requires a For parameter (none was supplied) — set For to the " +
            "field's accessor, e.g. For=\"() => _order.Description\".");

    /// <summary>Returns an input's <c>For</c> accessor when set, else the expression <c>@bind-Value</c> supplied, or throws when neither was.</summary>
    /// <typeparam name="TValue">The field's value type.</typeparam>
    /// <param name="forAccessor">The component's <c>For</c> parameter, which wins when both are present.</param>
    /// <param name="valueExpression">The <c>ValueExpression</c> the Razor compiler supplies for a <c>@bind-Value</c>.</param>
    /// <param name="componentType">The component's type, named in the exception.</param>
    /// <returns>The accessor.</returns>
    /// <exception cref="InvalidOperationException">Both <paramref name="forAccessor"/> and <paramref name="valueExpression"/> are <see langword="null"/>.</exception>
    internal static Expression<Func<TValue>> RequireBoundField<TValue>(
        Expression<Func<TValue>>? forAccessor,
        Expression<Func<TValue>>? valueExpression,
        Type componentType) =>
        forAccessor ?? valueExpression ?? throw new InvalidOperationException(
            $"{FriendlyTypeName.Of(componentType)} requires the field it edits (neither @bind-Value nor For was " +
            "supplied) — bind the value, e.g. @bind-Value=\"_order.Description\", or name the field explicitly, " +
            "e.g. For=\"() => _order.Description\".");
}
