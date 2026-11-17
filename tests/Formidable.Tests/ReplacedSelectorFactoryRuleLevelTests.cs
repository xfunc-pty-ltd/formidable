using FluentValidation;
using FluentValidation.Internal;

namespace Formidable.Tests;

/// <summary>
/// With FluentValidation's global ruleset-selector factory replaced, a set of rules run apart
/// admits the rules inside <c>DependentRules</c> and a child validator's rules as the factory's
/// selector admits them in a whole-profile run, so the rule-by-rule answer, the whole-profile
/// answer, FluentValidation's own run and the required mark agree. A selector of a type other
/// than FluentValidation's own may admit a child or dependent rule by more than its rulesets, so
/// every such decision marks the call as answering only for its profile.
/// </summary>
// Runs alone: the factory is read by every validation in the process, and while one of these
// tests holds a selector of its own there, a set run apart anywhere else would read its calls as
// answering only for their profile.
// Mutations that must break these: (1) build the delegated selector directly instead of through
// the factory, and the rule-by-rule rows report no error where the run reports one; (2) drop the
// selector-type test from the profile-scope reading, and the non-stock rows read false.
[Collection(ExclusiveGlobalStateCollection.Name)]
public class ReplacedSelectorFactoryRuleLevelTests
{
    private const string ProbeRuleSet = "FactoryProbe";
    private const string StrictRuleSet = "Strict";

    public sealed class FactoryCustomer
    {
        public string? Name { get; set; }
    }

    public sealed class FactoryContact
    {
        public string? Email { get; set; } = "ada@example.com";

        public string? Phone { get; set; }

        public FactoryCustomer Customer { get; set; } = new();
    }

    /// <summary>Admits any rule tagged <see cref="ProbeRuleSet"/> whatever the name list says, and answers every other rule as FluentValidation's own selector does.</summary>
    private sealed class ProbeSelector(IEnumerable<string> ruleSets) : IValidatorSelector
    {
        private readonly RulesetValidatorSelector _stock = new(ruleSets);

        public bool CanExecute(IValidationRule rule, string propertyPath, IValidationContext context) =>
            rule.RuleSets?.Contains(ProbeRuleSet, StringComparer.Ordinal) == true
            || _stock.CanExecute(rule, propertyPath, context);
    }

    /// <summary>Answers every rule as FluentValidation's own selector does, from a type of its own.</summary>
    private sealed class StockAnswering(IEnumerable<string> ruleSets) : IValidatorSelector
    {
        private readonly RulesetValidatorSelector _stock = new(ruleSets);

        public bool CanExecute(IValidationRule rule, string propertyPath, IValidationContext context) =>
            _stock.CanExecute(rule, propertyPath, context);
    }

    /// <summary>Admits a rule on <see cref="FactoryContact.Phone"/> only under a profile naming <see cref="StrictRuleSet"/>, whatever its rulesets, and answers every other rule as FluentValidation's own selector does.</summary>
    private sealed class StrictPhoneSelector(IEnumerable<string> ruleSets) : IValidatorSelector
    {
        private readonly string[] _names = [.. ruleSets];
        private readonly RulesetValidatorSelector _stock = new(ruleSets);

        public bool CanExecute(IValidationRule rule, string propertyPath, IValidationContext context) =>
            rule is IValidationRule<FactoryContact> && rule.PropertyName == nameof(FactoryContact.Phone)
                ? _names.Contains(StrictRuleSet, StringComparer.OrdinalIgnoreCase)
                : _stock.CanExecute(rule, propertyPath, context);
    }

    private sealed class ProbeCustomerValidator : AbstractValidator<FactoryCustomer>
    {
        public ProbeCustomerValidator() => RuleSet(ProbeRuleSet, () => RuleFor(c => c.Name).NotEmpty());
    }

    private sealed class PlainCustomerValidator : AbstractValidator<FactoryCustomer>
    {
        public PlainCustomerValidator() => RuleFor(c => c.Name).NotEmpty();
    }

    public enum Shape
    {
        DependentInProbeRuleSet,
        ChildRuleInProbeRuleSet,
        UntaggedDependent,
        UntaggedChildRule,
    }

    private static InlineValidator<FactoryContact> Build(Shape shape)
    {
        var v = new InlineValidator<FactoryContact>();
        switch (shape)
        {
            case Shape.DependentInProbeRuleSet:
                v.RuleFor(x => x.Email).NotEmpty().DependentRules(() =>
                    v.RuleSet(ProbeRuleSet, () => v.RuleFor(x => x.Phone).NotEmpty()));
                break;
            case Shape.ChildRuleInProbeRuleSet:
                v.RuleFor(x => x.Customer).SetValidator(new ProbeCustomerValidator());
                break;
            case Shape.UntaggedDependent:
                v.RuleFor(x => x.Email).NotEmpty().DependentRules(() => v.RuleFor(x => x.Phone).NotEmpty());
                break;
            case Shape.UntaggedChildRule:
                v.RuleFor(x => x.Customer).SetValidator(new PlainCustomerValidator());
                break;
        }

        return v;
    }

    private static string FieldOf(Shape shape) =>
        shape is Shape.DependentInProbeRuleSet or Shape.UntaggedDependent ? "Phone" : "Customer.Name";

    /// <summary>Runs <paramref name="body"/> with the global ruleset-selector factory replaced by <paramref name="factory"/>, restoring it however the body ends.</summary>
    private static async Task WithFactory(Func<IEnumerable<string>, IValidatorSelector> factory, Func<Task> body)
    {
        var stock = ValidatorOptions.Global.ValidatorSelectors.RulesetValidatorSelectorFactory;
        try
        {
            ValidatorOptions.Global.ValidatorSelectors.RulesetValidatorSelectorFactory = factory;
            await body();
        }
        finally
        {
            ValidatorOptions.Global.ValidatorSelectors.RulesetValidatorSelectorFactory = stock;
        }
    }

    /// <summary>What a form's check reports through the rule-by-rule seam: the profile's selection, grouped, one call per group.</summary>
    private static async Task<(List<string> Issues, List<bool> Scoped)> ByRuleAsync(
        FluentValidationModelValidator<FactoryContact> adapter, FactoryContact model, ValidationProfile profile)
    {
        var issues = new List<string>();
        var scoped = new List<bool>();
        foreach (var group in adapter.GroupBySelectionClass(adapter.SelectRules(profile)))
        {
            var result = await adapter.ValidateRulesAsync(model, profile, group);
            issues.AddRange(result.Report.Issues.Select(issue => $"{issue.Path}:{issue.Severity}"));
            scoped.Add(result.IsProfileScoped);
        }

        return (issues, scoped);
    }

    // The review's scenario: a factory whose selector admits FactoryProbe-tagged rules under any
    // profile. FluentValidation runs the tagged dependent, and the tagged rule of a child
    // validator attached with no rulesets of its own, under Submit; so must a set run apart, and
    // the required mark says the same. Under the stock factory neither runs (the control).
    [Theory]
    [InlineData(Shape.DependentInProbeRuleSet)]
    [InlineData(Shape.ChildRuleInProbeRuleSet)]
    public async Task A_set_run_apart_admits_what_the_replaced_factory_admits(Shape shape)
    {
        var field = FieldOf(shape);
        var model = new FactoryContact();
        var profile = ValidationProfile.Submit;

        var stockAdapter = new FluentValidationModelValidator<FactoryContact>(Build(shape));
        Assert.Empty((await ByRuleAsync(stockAdapter, model, profile)).Issues);
        Assert.Equal(FieldRequirement.NotRequired, stockAdapter.GetFieldRequirement(field, profile));

        await WithFactory(names => new ProbeSelector(names), async () =>
        {
            var fluent = Build(shape);
            var adapter = new FluentValidationModelValidator<FactoryContact>(fluent);
            Assert.True(adapter.CanValidateByRule);

            var own = await fluent.ValidateAsync(model, options => options.IncludeRuleSets(profile.ToRuleSetNames()));
            var ownKeys = own.Errors.Select(failure => $"{failure.PropertyName}:{failure.Severity}").ToList();
            var whole = (await adapter.ValidateAsync(model, profile)).Issues.Select(issue => $"{issue.Path}:{issue.Severity}").ToList();
            var (byRule, _) = await ByRuleAsync(adapter, model, profile);

            Assert.Equal([$"{field}:Error"], ownKeys);
            Assert.Equal(ownKeys, whole);
            Assert.Equal(ownKeys, byRule);
            Assert.Equal(FieldRequirement.Required, adapter.GetFieldRequirement(field, profile));
        });
    }

    // An untagged dependent or child rule under an untagged rule follows that rule's selection
    // under FluentValidation's own selector, so the set answers every profile. From a selector of
    // another type the same decision proves nothing about other profiles, so the set answers for
    // its own profile alone, though every issue is the same.
    [Theory]
    [InlineData(Shape.UntaggedDependent)]
    [InlineData(Shape.UntaggedChildRule)]
    public async Task A_selector_of_another_type_scopes_every_decision_it_makes_to_the_profile(Shape shape)
    {
        var model = new FactoryContact();
        var profile = ValidationProfile.Submit;
        var stock = await ByRuleAsync(new FluentValidationModelValidator<FactoryContact>(Build(shape)), model, profile);
        Assert.Equal([false], stock.Scoped);

        await WithFactory(names => new StockAnswering(names), async () =>
        {
            var replaced = await ByRuleAsync(new FluentValidationModelValidator<FactoryContact>(Build(shape)), model, profile);
            Assert.Equal(stock.Issues, replaced.Issues);
            Assert.Equal([true], replaced.Scoped);
        });
    }

    // Why the scope matters: a selector that admits Phone's dependent rule only under Strict.
    // Under a profile without Strict the set reports nothing for Phone, and under Strict it
    // reports Phone's error, so the first answer holds for its own profile alone. Were it read as
    // answering every profile, a check under Strict could take it and let a submit through with
    // Phone empty.
    [Fact]
    public async Task A_dependent_the_selector_admits_under_one_profile_alone_answers_for_that_profile()
    {
        var model = new FactoryContact();
        var lenient = ValidationProfile.Named("Lenient", includeDefaultRules: true);
        var strict = ValidationProfile.Named(StrictRuleSet, includeDefaultRules: true, StrictRuleSet);

        await WithFactory(names => new StrictPhoneSelector(names), async () =>
        {
            var adapter = new FluentValidationModelValidator<FactoryContact>(Build(Shape.UntaggedDependent));

            var underLenient = await ByRuleAsync(adapter, model, lenient);
            var underStrict = await ByRuleAsync(adapter, model, strict);

            Assert.Empty(underLenient.Issues);
            Assert.Equal(["Phone:Error"], underStrict.Issues);
            Assert.Equal([true], underLenient.Scoped);
        });
    }
}
