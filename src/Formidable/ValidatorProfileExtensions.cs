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
        if (validator is ProfiledValidator<T> profiledValidator)
        {
            profiledValidator.VerifyRuleSets(profile);
        }

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
        if (validator is ProfiledValidator<T> profiledValidator)
        {
            profiledValidator.VerifyRuleSets(profile);
        }

        return validator.ValidateAsync(BuildContext(model, profile), cancellationToken);
    }

    private static ValidationContext<T> BuildContext<T>(T model, ValidationProfile profile) =>
        ValidationContext<T>.CreateWithOptions(model, options =>
        {
            if (profile.RuleSets.Count > 0)
            {
                options.IncludeRuleSets([.. profile.RuleSets]);
            }

            if (profile.IncludeDefaultRules)
            {
                options.IncludeRulesNotInRuleSet();
            }
        });
}
