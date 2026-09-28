using FluentValidation;

namespace Formidable.Blazor.Tests.Fixtures;

public static class MinimumCountExtensions
{
    // A null list passes and is left to NotNull(), as FluentValidation's length rules leave a
    // null string. The doubled braces keep {PropertyName} for FluentValidation to fill in.
    public static IRuleBuilderOptions<T, IList<TItem>?> MinimumCount<T, TItem>(
        this IRuleBuilder<T, IList<TItem>?> rule, int minimum) =>
        rule.Must(list => list is null || list.Count >= minimum)
            .WithMessage($"'{{PropertyName}}' must contain at least {minimum} items.");
}
