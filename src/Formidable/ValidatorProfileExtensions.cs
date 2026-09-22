using FluentValidation;
using FluentValidation.Results;

namespace Formidable;

/// <summary>Profile-aware validation entry points for any FluentValidation validator.</summary>
public static class ValidatorProfileExtensions
{
    /// <summary>Validates <paramref name="model"/> using the rules selected by <paramref name="profile"/>.</summary>
    /// <remarks>Throws if the validator contains async rules — use
    /// <see cref="ValidateAsync{T}(IValidator{T}, T, ValidationProfile, CancellationToken)"/> for those.
    /// When <paramref name="validator"/> derives from <see cref="ProfiledValidator{T}"/>, see that
    /// class's remarks for the ruleset-name verification this performs first.</remarks>
    public static ValidationResult Validate<T>(this IValidator<T> validator, T model, ValidationProfile profile)
    {
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(profile);
        ProfiledValidator<T>.VerifyRuleSetsIfProfiled(validator, profile);

        return validator.Validate(BuildContext(model, profile));
    }

    /// <summary>Validates <paramref name="model"/> asynchronously using the rules selected by <paramref name="profile"/>.</summary>
    /// <remarks>When <paramref name="validator"/> derives from <see cref="ProfiledValidator{T}"/>,
    /// see that class's remarks for the ruleset-name verification this performs first.</remarks>
    public static Task<ValidationResult> ValidateAsync<T>(
        this IValidator<T> validator, T model, ValidationProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(profile);
        ProfiledValidator<T>.VerifyRuleSetsIfProfiled(validator, profile);

        return validator.ValidateAsync(BuildContext(model, profile), cancellationToken);
    }

    /// <summary>
    /// Names the profile's rulesets on the validation strategy, which builds its selector from
    /// them. The names come from <see cref="ValidationProfile.ToRuleSetNames"/>, the one place a
    /// profile becomes FluentValidation names — so a whole-profile run here and a selector built
    /// anywhere else in the library select from the same list rather than from two readings that
    /// have to be kept agreeing.
    /// </summary>
    private static ValidationContext<T> BuildContext<T>(T model, ValidationProfile profile) =>
        ValidationContext<T>.CreateWithOptions(model, options => options.IncludeRuleSets(profile.ToRuleSetNames()));
}
