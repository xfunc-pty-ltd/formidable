using FluentValidation;
using FluentValidation.Results;

namespace Formidable;

/// <summary>
/// Validator base class that organizes rules for use with <see cref="ValidationProfile"/>s
/// without prescribing any particular set of profiles. Override
/// <see cref="ConfigureCommonRules"/> for rules that run under any profile that includes
/// default rules, and register named rulesets in <see cref="ConfigureProfiles"/> via
/// <see cref="Profile"/>. For the common save-draft/submit form lifecycle, use
/// <see cref="DraftSubmitValidator{T}"/> instead.
/// </summary>
/// <remarks>
/// The configure hooks run from this base constructor, before a derived class's own
/// constructor body executes. Derived instance state assigned in the derived constructor is
/// not yet available when the hooks run eagerly — capture such state lazily inside rule
/// lambdas instead.
/// </remarks>
public abstract class ProfiledValidator<T> : AbstractValidator<T>
{
    /// <summary>Runs the configure hooks: common rules, then profile rulesets.</summary>
    protected ProfiledValidator()
    {
        ConfigureCommonRules();
        ConfigureProfiles();
    }

    /// <summary>
    /// Rules registered outside any ruleset. Included by every profile whose
    /// <see cref="ValidationProfile.IncludeDefaultRules"/> is true.
    /// </summary>
    protected virtual void ConfigureCommonRules()
    {
    }

    /// <summary>Registers named rulesets via <see cref="Profile"/>.</summary>
    protected virtual void ConfigureProfiles()
    {
    }

    /// <summary>Registers rules under a named ruleset that a <see cref="ValidationProfile"/> can compose.</summary>
    protected void Profile(string ruleSetName, Action configureRules) => RuleSet(ruleSetName, configureRules);

    /// <summary>Validates using the rules selected by <paramref name="profile"/>.</summary>
    public ValidationResult Validate(T model, ValidationProfile profile) =>
        ValidatorProfileExtensions.Validate(this, model, profile);

    /// <summary>Validates asynchronously using the rules selected by <paramref name="profile"/>.</summary>
    public Task<ValidationResult> ValidateAsync(T model, ValidationProfile profile, CancellationToken cancellationToken = default) =>
        ValidatorProfileExtensions.ValidateAsync(this, model, profile, cancellationToken);
}
