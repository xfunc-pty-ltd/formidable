using System.Reflection;

namespace Formidable.Tests;

/// <summary>
/// Pins <see cref="RuleIdentity"/>'s implementor-facing surface: the key an
/// <see cref="IRuleLevelValidator{TModel}"/> implementation wraps in
/// <see cref="RuleIdentity(object)"/> is readable back through the public
/// <see cref="RuleIdentity.Key"/> when an engine hands the identity to
/// <see cref="IRuleLevelValidator{TModel}.ValidateRulesAsync"/>. Without that read an
/// implementor must carry a side dictionary mapping its own identities back to its own
/// rules, while the identity already holds the rule it minted.
/// </summary>
public class RuleIdentityTests
{
    /// <summary>
    /// The minimal seam implementor: one rule object of its own, wrapped at selection and
    /// unwrapped at execution the way any producer resolves the identities it minted.
    /// </summary>
    private sealed class KeyReadingValidator : IRuleLevelValidator<string>
    {
        private readonly object _rule = new();

        public object WrappedRule => _rule;
        public object? ObservedKey { get; private set; }

        public bool CanValidateByRule => true;

        public IReadOnlyList<RuleIdentity> SelectRules(ValidationProfile profile) =>
            [new RuleIdentity(_rule)];

        public Task<RuleLevelResult> ValidateRulesAsync(string model, ValidationProfile profile,
            IReadOnlyList<RuleIdentity> rules, CancellationToken cancellationToken = default)
        {
            ObservedKey = rules.Single().Key;
            return Task.FromResult(new RuleLevelResult(ValidationReport.Empty, false));
        }

        // One group holding everything, which for a single rule IS the group-per-rule answer
        // the interface documents as always sound — there is nothing here to refine.
        public IReadOnlyList<IReadOnlyList<RuleIdentity>> GroupBySelectionClass(IReadOnlyList<RuleIdentity> rules) =>
            [rules];
    }

    [Fact]
    public async Task An_implementor_reads_back_the_key_instance_it_wrapped()
    {
        // The direct read in the validator above compiles for this assembly whatever the
        // member's visibility, because the test project is granted InternalsVisibleTo - so the
        // public surface is pinned by reflection, where GetProperty's default binding sees
        // public members only.
        var key = typeof(RuleIdentity).GetProperty(nameof(RuleIdentity.Key));
        Assert.NotNull(key);
        Assert.True(key.GetMethod!.IsPublic);

        var validator = new KeyReadingValidator();
        var rule = validator.SelectRules(ValidationProfile.Submit).Single();

        await validator.ValidateRulesAsync("model", ValidationProfile.Submit, [rule]);

        Assert.Same(validator.WrappedRule, validator.ObservedKey);
    }
}
