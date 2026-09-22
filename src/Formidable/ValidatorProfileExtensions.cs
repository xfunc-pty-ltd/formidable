using FluentValidation;
using FluentValidation.Results;

namespace Formidable;

/// <summary>Profile-aware validation entry points for any FluentValidation validator.</summary>
public static class ValidatorProfileExtensions
{
    /// <summary>Validates <paramref name="model"/> synchronously with the rules <paramref name="profile"/> selects; prefer <see cref="ValidateAsync{T}(IValidator{T}, T, ValidationProfile, CancellationToken)"/>.</summary>
    /// <typeparam name="T">The model type the validator accepts.</typeparam>
    /// <param name="validator">The validator to run.</param>
    /// <param name="model">The model to validate.</param>
    /// <param name="profile">The rule selection to run.</param>
    /// <returns>FluentValidation's result.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="validator"/> or <paramref name="profile"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="profile"/> names a ruleset a <see cref="ProfiledValidator{T}"/> never registered.</exception>
    /// <exception cref="AsyncValidatorInvokedSynchronouslyException">A rule <paramref name="profile"/> selects reaches an async validator or an async condition.</exception>
    /// <remarks>A <see cref="ProfiledValidator{T}"/> has the profile's ruleset names verified first; any other validator is not checked.</remarks>
    public static ValidationResult Validate<T>(this IValidator<T> validator, T model, ValidationProfile profile)
    {
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(profile);
        ProfiledValidator<T>.VerifyRuleSetsIfProfiled(validator, profile);

        return validator.Validate(BuildContext(model, profile));
    }

    /// <summary>Validates <paramref name="model"/> asynchronously with the rules <paramref name="profile"/> selects.</summary>
    /// <typeparam name="T">The model type the validator accepts.</typeparam>
    /// <param name="validator">The validator to run.</param>
    /// <param name="model">The model to validate.</param>
    /// <param name="profile">The rule selection to run.</param>
    /// <param name="cancellationToken">Cancels an async rule still running.</param>
    /// <returns>FluentValidation's result.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="validator"/> or <paramref name="profile"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="profile"/> names a ruleset a <see cref="ProfiledValidator{T}"/> never registered.</exception>
    /// <remarks>A <see cref="ProfiledValidator{T}"/> has the profile's ruleset names verified first; any other validator is not checked.</remarks>
    public static Task<ValidationResult> ValidateAsync<T>(
        this IValidator<T> validator, T model, ValidationProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(profile);
        ProfiledValidator<T>.VerifyRuleSetsIfProfiled(validator, profile);

        return validator.ValidateAsync(BuildContext(model, profile), cancellationToken);
    }

    /// <summary>The validation context naming the profile's rulesets on its strategy, from <see cref="ValidationProfile.ToRuleSetNames"/>.</summary>
    /// <typeparam name="T">The model type.</typeparam>
    /// <param name="model">The model the context carries.</param>
    /// <param name="profile">The profile whose ruleset names the strategy includes.</param>
    /// <returns>A context whose selector the strategy builds from those names.</returns>
    // ToRuleSetNames is the one place a profile becomes FluentValidation names, so a full
    // validation here and a selector built anywhere else in the library select from the same list
    // rather than from two readings that have to be kept agreeing.
    private static ValidationContext<T> BuildContext<T>(T model, ValidationProfile profile) =>
        ValidationContext<T>.CreateWithOptions(model, options => options.IncludeRuleSets(profile.ToRuleSetNames()));
}
