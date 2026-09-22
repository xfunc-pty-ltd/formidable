namespace Formidable.Blazor.Tests;

public class ProfileDeltaTests
{
    [Fact]
    public void Default_pair_subtracts_to_the_submit_ruleset_alone()
    {
        var result = ProfileDelta.Compute(ValidationProfile.Submit, ValidationProfile.Draft);

        Assert.Equal(ProfileDeltaKind.Delta, result.Kind);
        Assert.NotNull(result.Profile);
        Assert.False(result.Profile!.IncludeDefaultRules);
        Assert.Equal(["Submit"], result.Profile.RuleSets);
    }

    [Fact]
    public void Identical_profiles_subtract_to_empty()
    {
        var result = ProfileDelta.Compute(ValidationProfile.Draft, ValidationProfile.Draft);

        Assert.Equal(ProfileDeltaKind.Empty, result.Kind);
        Assert.Null(result.Profile);
    }

    [Fact]
    public void A_live_profile_selecting_more_is_not_subtractable()
    {
        var live = ValidationProfile.Named("ExtraLive", includeDefaultRules: true, "Extra");

        var result = ProfileDelta.Compute(ValidationProfile.Submit, live);

        Assert.Equal(ProfileDeltaKind.NotSubtractable, result.Kind);
        Assert.Null(result.Profile);
    }

    [Fact]
    public void A_live_profile_with_default_rules_against_a_submit_without_is_not_subtractable()
    {
        var submit = ValidationProfile.Named("S", includeDefaultRules: false, "S");

        var result = ProfileDelta.Compute(submit, ValidationProfile.Draft);

        Assert.Equal(ProfileDeltaKind.NotSubtractable, result.Kind);
        Assert.Null(result.Profile);
    }

    [Fact]
    public void Overlapping_rulesets_are_removed_and_the_rest_kept()
    {
        var submit = ValidationProfile.Named("Wide", includeDefaultRules: true, "A", "B", "C");
        var live = ValidationProfile.Named("Narrow", includeDefaultRules: true, "B");

        var result = ProfileDelta.Compute(submit, live);

        Assert.Equal(ProfileDeltaKind.Delta, result.Kind);
        Assert.Equal(["A", "C"], result.Profile!.RuleSets);
    }

    [Fact]
    public void The_same_inputs_produce_an_equal_profile()
    {
        var first = ProfileDelta.Compute(ValidationProfile.Submit, ValidationProfile.Draft);
        var second = ProfileDelta.Compute(ValidationProfile.Submit, ValidationProfile.Draft);

        Assert.Equal(first.Profile, second.Profile);
    }
}
