using FluentValidation;

namespace Formidable;

/// <summary>Puts an <see cref="AsyncRuleMemo{TKey, TResult}"/> behind a FluentValidation <c>MustAsync</c> rule.</summary>
public static class AsyncRuleMemoExtensions
{
    /// <summary>Like FluentValidation's <c>MustAsync</c>, but <paramref name="memo"/> reuses the answer for an unchanged non-null value within its window; a <see langword="null"/> value is checked directly, never memoized.</summary>
    /// <typeparam name="T">The model being validated.</typeparam>
    /// <typeparam name="TKey">The property's type with null excluded; also <paramref name="memo"/>'s key type.</typeparam>
    /// <param name="ruleBuilder">The rule being built.</param>
    /// <param name="memo">Holds the answers; one per rule, held as a field on the validator.</param>
    /// <param name="predicate">The check; handed FluentValidation's cancellation token on the null path and <see cref="CancellationToken.None"/> on the memoized path, where a caller that cancels stops waiting while the check continues.</param>
    /// <returns>The rule options, chained as after <c>MustAsync</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="ruleBuilder"/>, <paramref name="memo"/> or <paramref name="predicate"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// <paramref name="memo"/> keys its answers by the value alone, so no overload hands
    /// <paramref name="predicate"/> the model; a check that needs more than the value belongs on
    /// <c>MustAsync</c> directly. <typeparamref name="TKey"/> binds a reference-typed property
    /// whether or not it is nullable (<c>string</c> and <c>string?</c> both bind <c>string</c>) but
    /// not a nullable value-typed one (<c>int?</c>), where inference fails; unwrap the value inside
    /// <c>MustAsync</c> and call <see cref="AsyncRuleMemo{TKey, TResult}.GetAsync"/> yourself.
    /// </remarks>
    // A check that also read the model would have the answer it computed against one model state
    // served against another for as long as the window lasts and the value stands still, which is
    // why the memo's key is the value and nothing else. C# erases an unconstrained TKey? to plain
    // TKey for a value type; it becomes Nullable<TKey> only under a struct constraint, which would
    // then reject every reference-typed property, so one type parameter cannot cover both nullable
    // shapes at once. A null value bypasses the memo because nothing is keyed by the absence of an
    // answer, and a value that was never asked for cannot go stale.
    public static IRuleBuilderOptions<T, TKey?> MustAsyncMemoized<T, TKey>(
        this IRuleBuilder<T, TKey?> ruleBuilder,
        AsyncRuleMemo<TKey, bool> memo,
        Func<TKey?, CancellationToken, Task<bool>> predicate)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(ruleBuilder);
        ArgumentNullException.ThrowIfNull(memo);
        ArgumentNullException.ThrowIfNull(predicate);

        return ruleBuilder.MustAsync((value, cancellationToken) =>
            value is null
                ? predicate(value, cancellationToken)
                : memo.GetAsync(value, predicate, cancellationToken));
    }
}
