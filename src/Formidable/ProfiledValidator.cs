using System.Collections.Concurrent;
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
/// <para>
/// Every <see cref="ValidationProfile"/> passed to <see cref="Validate"/>/<see cref="ValidateAsync"/>
/// (or to <see cref="ValidatorProfileExtensions"/>'s extension methods called directly on an
/// instance of this class) has its ruleset names checked against this validator's own
/// registered rulesets, once per distinct profile name and cached thereafter. Matching is
/// case-insensitive, mirroring FluentValidation's own ruleset selector. A name that matches
/// zero registered rulesets throws <see cref="InvalidOperationException"/> naming the
/// unmatched ruleset and this validator's available names — catching a typo'd profile or
/// ruleset name that FluentValidation would otherwise ignore and silently under-validate. A
/// plain FluentValidation <c>AbstractValidator&lt;T&gt;</c> validated via
/// <see cref="ValidatorProfileExtensions"/> without deriving from this class is not covered.
/// The cache key is the profile's <see cref="ValidationProfile.Name"/> alone, compared ordinally
/// — the case-insensitivity above belongs to the ruleset match, not to this cache — so two
/// distinct profile instances sharing a name exactly are treated as the same profile here, and
/// only the first one actually validated has its ruleset names checked; a name differing only in
/// case is verified on its own.
/// </para>
/// </remarks>
public abstract class ProfiledValidator<T> : AbstractValidator<T>
{
    private readonly HashSet<string> _registeredRuleSetNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _verifiedProfileNames = new();

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
    protected void Profile(string ruleSetName, Action configureRules)
    {
        _registeredRuleSetNames.Add(ruleSetName);
        RuleSet(ruleSetName, configureRules);
    }

    /// <summary>Validates using the rules selected by <paramref name="profile"/>.</summary>
    public ValidationResult Validate(T model, ValidationProfile profile) =>
        ValidatorProfileExtensions.Validate(this, model, profile);

    /// <summary>Validates asynchronously using the rules selected by <paramref name="profile"/>.</summary>
    public Task<ValidationResult> ValidateAsync(T model, ValidationProfile profile, CancellationToken cancellationToken = default) =>
        ValidatorProfileExtensions.ValidateAsync(this, model, profile, cancellationToken);

    /// <summary>
    /// Throws when <paramref name="profile"/> names a ruleset this validator never registered
    /// via <see cref="Profile"/> — a ruleset counts as registered by the call, whether or not
    /// its <c>configureRules</c> body ends up adding any rule. Matching is case-insensitive, so
    /// this agrees with FluentValidation's own ruleset selector about what counts as a match.
    /// Verified once per distinct <see cref="ValidationProfile.Name"/> and cached — repeat
    /// validations with an already-verified profile pay only the cache lookup.
    /// </summary>
    internal void VerifyRuleSets(ValidationProfile profile)
    {
        if (profile.RuleSets.Count == 0 || _verifiedProfileNames.ContainsKey(profile.Name))
        {
            return;
        }

        foreach (var ruleSetName in profile.RuleSets)
        {
            if (!_registeredRuleSetNames.Contains(ruleSetName))
            {
                var available = _registeredRuleSetNames.Count > 0
                    ? string.Join(", ", _registeredRuleSetNames.OrderBy(name => name, StringComparer.Ordinal).Select(name => $"'{name}'"))
                    : "none registered";
                throw new InvalidOperationException(
                    $"Profile ruleset '{ruleSetName}' matches no ruleset on '{GetType().Name}' — available: {available}.");
            }
        }

        _verifiedProfileNames.TryAdd(profile.Name, 0);
    }
}
