using System.Collections.Concurrent;
using FluentValidation;
using FluentValidation.Internal;
using FluentValidation.Results;

namespace Formidable;

/// <summary>Base validator whose rules are organised by <see cref="ValidationProfile"/>: common rules in one hook, named rulesets in another.</summary>
/// <typeparam name="T">The model type the validator accepts.</typeparam>
/// <remarks>
/// Override <see cref="ConfigureCommonRules"/> for rules every profile with default rules runs,
/// and register named rulesets through <see cref="Profile"/> in <see cref="ConfigureProfiles"/>;
/// <see cref="DraftSubmitValidator{T}"/> packages the save-draft and submit shape. The hooks run
/// from this constructor, before a derived constructor body, so capture derived state lazily
/// inside rule lambdas. Every profile validated through this class, or through
/// <see cref="ValidatorProfileExtensions"/> on an instance of it, has its ruleset names checked
/// case-insensitively; a name matching no registered ruleset, and neither <c>"*"</c> nor
/// <c>"default"</c>, throws <see cref="InvalidOperationException"/> naming the available ones.
/// </remarks>
// The check catches a typo'd profile or ruleset name that FluentValidation would otherwise
// ignore and silently under-validate. A plain AbstractValidator<T> validated through the
// extensions is not covered. The cache key is the whole profile, which compares by its full
// shape, so two same-named profiles composing different rulesets are checked on their own.
public abstract class ProfiledValidator<T> : AbstractValidator<T>
{
    private readonly HashSet<string> _registeredRuleSetNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<ValidationProfile, byte> _verifiedProfiles = new();

    /// <summary>Runs <see cref="ConfigureCommonRules"/>, then <see cref="ConfigureProfiles"/>.</summary>
    protected ProfiledValidator()
    {
        ConfigureCommonRules();
        ConfigureProfiles();
    }

    /// <summary>Registers rules outside any ruleset, which every profile with <see cref="ValidationProfile.IncludeDefaultRules"/> runs; does nothing by default.</summary>
    protected virtual void ConfigureCommonRules()
    {
    }

    /// <summary>Registers named rulesets through <see cref="Profile"/>; does nothing by default.</summary>
    protected virtual void ConfigureProfiles()
    {
    }

    /// <summary>Registers <paramref name="configureRules"/> under <paramref name="ruleSetName"/>, which may join several names with <c>,</c> or <c>;</c>.</summary>
    /// <param name="ruleSetName">One ruleset name, or several joined with <c>,</c> or <c>;</c>; every rule inside then carries all of them, and each part is registered on its own.</param>
    /// <param name="configureRules">The rule declarations to register under the name.</param>
    protected void Profile(string ruleSetName, Action configureRules)
    {
        foreach (var name in SplitRuleSetNames(ruleSetName))
        {
            _registeredRuleSetNames.Add(name);
        }

        RuleSet(ruleSetName, configureRules);
    }

    // Mirrors what FluentValidation's RuleSet does with the name it is handed, so what is
    // recorded here is what it actually tagged the rules with. Recording the literal instead
    // makes the verification reject the very names FluentValidation selects on.
    private static string[] SplitRuleSetNames(string ruleSetName) =>
        ruleSetName.Split([',', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Validates <paramref name="model"/> with the rules <paramref name="profile"/> selects.</summary>
    /// <param name="model">The model to validate.</param>
    /// <param name="profile">The rule selection to run.</param>
    /// <returns>FluentValidation's result.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="profile"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="profile"/> names a ruleset never registered through <see cref="Profile"/>.</exception>
    /// <exception cref="AsyncValidatorInvokedSynchronouslyException">A rule <paramref name="profile"/> selects reaches an async validator or an async condition.</exception>
    public ValidationResult Validate(T model, ValidationProfile profile) =>
        ValidatorProfileExtensions.Validate(this, model, profile);

    /// <summary>Validates <paramref name="model"/> asynchronously with the rules <paramref name="profile"/> selects.</summary>
    /// <param name="model">The model to validate.</param>
    /// <param name="profile">The rule selection to run.</param>
    /// <param name="cancellationToken">Cancels an async rule still running.</param>
    /// <returns>FluentValidation's result.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="profile"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="profile"/> names a ruleset never registered through <see cref="Profile"/>.</exception>
    public Task<ValidationResult> ValidateAsync(T model, ValidationProfile profile, CancellationToken cancellationToken = default) =>
        ValidatorProfileExtensions.ValidateAsync(this, model, profile, cancellationToken);

    /// <summary>Throws when <paramref name="profile"/> names a ruleset never registered through <see cref="Profile"/>; checked once per distinct profile.</summary>
    /// <param name="profile">The profile whose ruleset names are checked.</param>
    /// <exception cref="InvalidOperationException">A name in <see cref="ValidationProfile.RuleSets"/> matches no registered ruleset, <c>"*"</c> or <c>"default"</c>.</exception>
    // A ruleset counts as registered by the Profile call, whether or not its body adds any rule.
    // Matching is case-insensitive and reads a joined Profile name as the several rulesets
    // FluentValidation tags its rules with. FluentValidation's own "*" (every rule) and "default"
    // (the rules outside every ruleset) are names its selector honours rather than rulesets a
    // validator declares, so they always match; what is left to reject is a name FluentValidation
    // would select nothing for. A profile naming no ruleset has nothing to reject and is not
    // recorded; repeat validations with an equal profile pay only the cache lookup.
    internal void VerifyRuleSets(ValidationProfile profile)
    {
        if (profile.RuleSets.Count == 0 || _verifiedProfiles.ContainsKey(profile))
        {
            return;
        }

        foreach (var ruleSetName in profile.RuleSets)
        {
            if (!IsSelectable(ruleSetName))
            {
                var available = _registeredRuleSetNames.Count > 0
                    ? string.Join(", ", _registeredRuleSetNames.OrderBy(name => name, StringComparer.Ordinal).Select(name => $"'{name}'"))
                    : "none registered";
                throw new InvalidOperationException(
                    $"Profile ruleset '{ruleSetName}' matches no ruleset on '{FriendlyTypeName.Of(GetType())}' — available: {available}.");
            }
        }

        _verifiedProfiles.TryAdd(profile, 0);
    }

    /// <summary>Runs <paramref name="validator"/>'s own <see cref="VerifyRuleSets"/> when it derives from <see cref="ProfiledValidator{T}"/>, and does nothing otherwise.</summary>
    /// <typeparam name="TValidated">The model type <paramref name="validator"/> accepts.</typeparam>
    /// <param name="validator">The validator to test.</param>
    /// <param name="profile">The profile whose ruleset names are checked.</param>
    /// <exception cref="InvalidOperationException"><paramref name="profile"/> names a ruleset the validator never registered.</exception>
    // The one call every profile-aware entry point shares instead of repeating the type test.
    // Static, so the enclosing type's own type parameter plays no part in that test: the pattern
    // match below closes over TValidated alone, resolved from the validator, and a caller reaches
    // this member through whichever closed ProfiledValidator<T> happens to be in scope.
    internal static void VerifyRuleSetsIfProfiled<TValidated>(IValidator<TValidated> validator, ValidationProfile profile)
    {
        if (validator is ProfiledValidator<TValidated> profiledValidator)
        {
            profiledValidator.VerifyRuleSets(profile);
        }
    }

    private bool IsSelectable(string ruleSetName) =>
        _registeredRuleSetNames.Contains(ruleSetName)
        || string.Equals(ruleSetName, RulesetValidatorSelector.WildcardRuleSetName, StringComparison.OrdinalIgnoreCase)
        || string.Equals(ruleSetName, RulesetValidatorSelector.DefaultRuleSetName, StringComparison.OrdinalIgnoreCase);
}
