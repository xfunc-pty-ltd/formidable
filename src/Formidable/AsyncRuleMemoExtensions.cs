using FluentValidation;

namespace Formidable;

/// <summary>
/// Rule-builder sugar over <see cref="AsyncRuleMemo{TKey, TResult}"/>, so an async rule reuses the
/// answer it already gave for an unchanged value without the call site having to say so twice.
/// </summary>
public static class AsyncRuleMemoExtensions
{
    /// <summary>
    /// The same contract as FluentValidation's <c>MustAsync</c>, with the check's answer reused for
    /// as long as <paramref name="memo"/>'s window lasts and the value has not changed. A
    /// <see langword="null"/> value is checked directly and never memoized — nothing is keyed by
    /// the absence of an answer, and a value that was never asked for cannot go stale.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <paramref name="memo"/> has to outlive a validation pass to ever hit, so hold it as a field
    /// on the validator. Read <see cref="AsyncRuleMemo{TKey, TResult}"/> for the rest of the contract,
    /// in particular that the check must be pure with respect to its input.
    /// </para>
    /// <para>
    /// <typeparamref name="TKey"/> is <paramref name="memo"/>'s key type, and also the property's
    /// type when the property cannot be null. It reaches a nullable property too, as long as the
    /// property is a reference type: a <c>string</c> rule and a <c>string?</c> rule both bind
    /// <typeparamref name="TKey"/> to <c>string</c>, and <paramref name="ruleBuilder"/> and
    /// <paramref name="predicate"/> both accept the nullable-annotated <c>TKey?</c> so the
    /// <see langword="null"/> case still reaches <paramref name="predicate"/> directly. It does
    /// <em>not</em> reach a nullable <em>value</em>-typed property (<c>int?</c>, <c>Guid?</c>, and
    /// so on): C# erases an unconstrained <c>TKey?</c> to plain <c>TKey</c> for a value type — it
    /// only becomes <c>Nullable&lt;TKey&gt;</c> under a <see langword="struct"/> constraint, which
    /// would then reject every reference-typed property — so one type parameter cannot cover both
    /// nullable shapes at once. A rule on a nullable value-typed property has to unwrap it before
    /// this helps: <c>RuleFor(x => x.Age).MustAsync((value, ct) => value is null ? predicate(null,
    /// ct) : memo.GetAsync(value.Value, (v, ct2) => predicate(v, ct2), ct))</c>.
    /// </para>
    /// </remarks>
    /// <typeparam name="T">The model being validated.</typeparam>
    /// <typeparam name="TKey">
    /// The property's type with null excluded. Also <paramref name="memo"/>'s key type.
    /// </typeparam>
    /// <param name="ruleBuilder">The rule being built.</param>
    /// <param name="memo">Holds the answers. One per rule, held by the validator.</param>
    /// <param name="predicate">
    /// The check. The token it is handed depends on which path reaches it, because a memoized
    /// answer belongs to every caller waiting on it: on the <see langword="null"/> path it gets
    /// FluentValidation's own cancellation token, and on the memoized path
    /// <see cref="CancellationToken.None"/> — a caller that cancels stops waiting, while the call
    /// it was waiting on carries on for whoever else wants the answer.
    /// </param>
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
                : memo.GetAsync(value, (key, ct) => predicate(key, ct), cancellationToken));
    }
}
