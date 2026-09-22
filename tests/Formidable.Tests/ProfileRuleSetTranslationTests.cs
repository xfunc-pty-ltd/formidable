using FluentValidation;
using FluentValidation.Internal;

namespace Formidable.Tests;

/// <summary>
/// Pins the FluentValidation fact a profile's two translations rest on: asking a validation
/// strategy for the rules outside every ruleset selects exactly what naming the default ruleset
/// selects, so a profile's rulesets plus <c>"default"</c> is one name list either way of writing
/// it. The library translates a profile into that list at one site and hands it to both the
/// strategy and the selector factory; these tests hold the framework behaviour that makes the two
/// routes one thing rather than two agreeing ones.
/// </summary>
public class ProfileRuleSetTranslationTests
{
    private static readonly ProbeModel AllRulesFail = new();

    private static IReadOnlyList<string> SelectedBy(
        IValidator<ProbeModel> validator, Action<ValidationStrategy<ProbeModel>> strategy) =>
        [.. validator.Validate(ValidationContext<ProbeModel>.CreateWithOptions(AllRulesFail, strategy)).Errors
            .Select(failure => failure.PropertyName)];

    [Fact]
    public void Asking_for_the_rules_outside_every_ruleset_selects_what_naming_the_default_ruleset_selects()
    {
        var validator = new ProbeValidator();

        var notInRuleSet = SelectedBy(validator, options => options.IncludeRulesNotInRuleSet());
        var namedDefault = SelectedBy(
            validator, options => options.IncludeRuleSets(RulesetValidatorSelector.DefaultRuleSetName));

        // The positive control: an empty report on both sides would satisfy the equality while
        // proving nothing about selection.
        Assert.Equal([nameof(ProbeModel.Unbucketed)], notInRuleSet);
        Assert.Equal(notInRuleSet, namedDefault);
    }

    [Fact]
    public void A_ruleset_and_the_default_name_in_one_list_selects_what_the_two_call_shape_selects()
    {
        var validator = new ProbeValidator();

        var twoCalls = SelectedBy(validator, options =>
        {
            options.IncludeRuleSets("Alpha");
            options.IncludeRulesNotInRuleSet();
        });
        var oneList = SelectedBy(validator, options =>
            options.IncludeRuleSets("Alpha", RulesetValidatorSelector.DefaultRuleSetName));

        // Discriminating on both sides: the named ruleset runs, the default rules run, and the
        // ruleset nobody named stays out.
        Assert.Equal([nameof(ProbeModel.Unbucketed), nameof(ProbeModel.Alpha)], twoCalls);
        Assert.Equal(twoCalls, oneList);
    }

    [Fact]
    public void The_name_list_order_does_not_move_what_is_selected_or_the_order_it_is_reported_in()
    {
        var validator = new ProbeValidator();

        var defaultFirst = SelectedBy(validator, options =>
        {
            options.IncludeRulesNotInRuleSet();
            options.IncludeRuleSets("Alpha");
        });
        var defaultLast = SelectedBy(validator, options =>
            options.IncludeRuleSets("Alpha", RulesetValidatorSelector.DefaultRuleSetName));

        // Selection asks each rule whether any of its rulesets is named, so the answer carries no
        // order of its own and failures come back in the order the rules are declared.
        Assert.Equal([nameof(ProbeModel.Unbucketed), nameof(ProbeModel.Alpha)], defaultFirst);
        Assert.Equal(defaultFirst, defaultLast);
    }

    [Fact]
    public void A_validator_with_no_rules_outside_a_ruleset_answers_both_spellings_the_same_way()
    {
        var validator = new BucketedOnlyValidator();

        var notInRuleSet = SelectedBy(validator, options => options.IncludeRulesNotInRuleSet());
        var namedDefault = SelectedBy(
            validator, options => options.IncludeRuleSets(RulesetValidatorSelector.DefaultRuleSetName));

        // Selecting nothing is the honest answer here, so the control is the same validator under
        // a name it does carry: without it, two empty reports would agree for want of a harness.
        Assert.Equal(
            [nameof(ProbeModel.Alpha)],
            SelectedBy(validator, options => options.IncludeRuleSets("Alpha")));
        Assert.Empty(notInRuleSet);
        Assert.Empty(namedDefault);
    }

    [Fact]
    public void The_wildcard_name_carries_the_same_way_with_the_default_name_beside_it()
    {
        var validator = new ProbeValidator();

        var twoCalls = SelectedBy(validator, options =>
        {
            options.IncludeRuleSets("*");
            options.IncludeRulesNotInRuleSet();
        });
        var oneList = SelectedBy(validator, options =>
            options.IncludeRuleSets("*", RulesetValidatorSelector.DefaultRuleSetName));
        var wildcardAlone = SelectedBy(validator, options => options.IncludeRuleSets("*"));

        // The wildcard already reaches every rule, bucketed or not, so the default name beside it
        // adds nothing — a profile naming the wildcard and asking for its default rules selects
        // what the wildcard alone selects.
        Assert.Equal(
            [nameof(ProbeModel.Unbucketed), nameof(ProbeModel.Alpha), nameof(ProbeModel.Beta)],
            wildcardAlone);
        Assert.Equal(wildcardAlone, twoCalls);
        Assert.Equal(wildcardAlone, oneList);
    }

    [Fact]
    public void The_default_name_twice_over_runs_the_rules_outside_every_ruleset_once()
    {
        var validator = new ProbeValidator();

        // A profile may name "default" itself, in which case appending it again produces the
        // name twice. Selection asks a membership question, so a repeated name is a repeated
        // question with one answer rather than a second execution.
        var twice = SelectedBy(validator, options =>
        {
            options.IncludeRuleSets(RulesetValidatorSelector.DefaultRuleSetName);
            options.IncludeRulesNotInRuleSet();
        });

        Assert.Equal([nameof(ProbeModel.Unbucketed)], twice);
    }

    [Fact]
    public void A_profile_becomes_its_rulesets_followed_by_the_default_name_it_asks_for()
    {
        Assert.Equal(["default"], ValidationProfile.Draft.ToRuleSetNames());
        Assert.Equal(["Submit", "default"], ValidationProfile.Submit.ToRuleSetNames());
        Assert.Equal(
            ["Alpha", "Beta", "default"],
            ValidationProfile.Named("Both", includeDefaultRules: true, "Alpha", "Beta").ToRuleSetNames());
        Assert.Equal(
            ["Alpha"],
            ValidationProfile.Named("AlphaOnly", includeDefaultRules: false, "Alpha").ToRuleSetNames());

        // A fresh array per call: a selector keeps the array it is handed, so two selectors built
        // from one profile must not be handed one array between them.
        Assert.NotSame(ValidationProfile.Submit.ToRuleSetNames(), ValidationProfile.Submit.ToRuleSetNames());
    }

    [Fact]
    public void A_profile_of_default_rules_alone_selects_the_same_either_way_it_is_named()
    {
        AssertBothSpellingsSelect(ValidationProfile.Draft, [nameof(ProbeModel.Unbucketed)]);
    }

    [Fact]
    public void A_profile_of_rulesets_and_default_rules_selects_the_same_either_way_it_is_named()
    {
        AssertBothSpellingsSelect(
            ValidationProfile.Named("Alpha", includeDefaultRules: true, "Alpha"),
            [nameof(ProbeModel.Unbucketed), nameof(ProbeModel.Alpha)]);
    }

    [Fact]
    public void A_profile_excluding_default_rules_selects_the_same_either_way_it_is_named()
    {
        AssertBothSpellingsSelect(
            ValidationProfile.Named("AlphaOnly", includeDefaultRules: false, "Alpha"),
            [nameof(ProbeModel.Alpha)]);
    }

    [Fact]
    public void A_profile_naming_the_wildcard_selects_the_same_either_way_it_is_named()
    {
        AssertBothSpellingsSelect(
            ValidationProfile.Named("Everything", includeDefaultRules: true, "*"),
            [nameof(ProbeModel.Unbucketed), nameof(ProbeModel.Alpha), nameof(ProbeModel.Beta)]);
    }

    /// <summary>
    /// Holds one profile's two readings together: naming its rulesets and its default rules as
    /// separate asks, and naming its single <see cref="ValidationProfile.ToRuleSetNames"/> list.
    /// The expected selection is stated outright as well, so neither reading can drift to agree
    /// on the wrong rules.
    /// </summary>
    private static void AssertBothSpellingsSelect(ValidationProfile profile, string[] expected)
    {
        var validator = new ProbeValidator();

        var separateAsks = SelectedBy(validator, options =>
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
        var oneNameList = SelectedBy(validator, options => options.IncludeRuleSets(profile.ToRuleSetNames()));

        Assert.Equal(expected, separateAsks);
        Assert.Equal(expected, oneNameList);
    }

    private sealed class ProbeModel
    {
        public string Unbucketed { get; init; } = string.Empty;

        public string Alpha { get; init; } = string.Empty;

        public string Beta { get; init; } = string.Empty;
    }

    /// <summary>Carries a rule outside every ruleset and one in each of two rulesets, so which
    /// names a selection holds is readable from which properties report.</summary>
    private sealed class ProbeValidator : AbstractValidator<ProbeModel>
    {
        public ProbeValidator()
        {
            RuleFor(model => model.Unbucketed).NotEmpty();
            RuleSet("Alpha", () => RuleFor(model => model.Alpha).NotEmpty());
            RuleSet("Beta", () => RuleFor(model => model.Beta).NotEmpty());
        }
    }

    /// <summary>Every rule sits in a ruleset, so a selection of the unbucketed rules has nothing
    /// to find.</summary>
    private sealed class BucketedOnlyValidator : AbstractValidator<ProbeModel>
    {
        public BucketedOnlyValidator()
        {
            RuleSet("Alpha", () => RuleFor(model => model.Alpha).NotEmpty());
            RuleSet("Beta", () => RuleFor(model => model.Beta).NotEmpty());
        }
    }
}
