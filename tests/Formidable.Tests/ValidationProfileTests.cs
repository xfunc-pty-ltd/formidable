namespace Formidable.Tests;

public class ValidationProfileTests
{
    [Fact]
    public void Draft_includes_default_rules_and_no_rulesets()
    {
        Assert.Equal("Draft", ValidationProfile.Draft.Name);
        Assert.True(ValidationProfile.Draft.IncludeDefaultRules);
        Assert.Empty(ValidationProfile.Draft.RuleSets);
    }

    [Fact]
    public void Submit_includes_default_rules_plus_submit_ruleset()
    {
        Assert.Equal("Submit", ValidationProfile.Submit.Name);
        Assert.True(ValidationProfile.Submit.IncludeDefaultRules);
        Assert.Equal(new[] { ValidationProfile.SubmitRuleSetName }, ValidationProfile.Submit.RuleSets);
    }

    [Fact]
    public void Named_creates_custom_profile()
    {
        var profile = ValidationProfile.Named("Approve", includeDefaultRules: false, "Submit", "Approve");

        Assert.Equal("Approve", profile.Name);
        Assert.False(profile.IncludeDefaultRules);
        Assert.Equal(new[] { "Submit", "Approve" }, profile.RuleSets);
    }

    [Fact]
    public void Named_rejects_blank_names()
    {
        Assert.ThrowsAny<ArgumentException>(() => ValidationProfile.Named(" "));
    }

    [Fact]
    public void Named_rejects_profile_that_selects_no_rules()
    {
        Assert.ThrowsAny<ArgumentException>(() => ValidationProfile.Named("Empty", includeDefaultRules: false));
    }

    [Fact]
    public void Named_rejects_null_ruleset_array()
    {
        Assert.Throws<ArgumentNullException>(() => ValidationProfile.Named("X", true, null!));
    }

    [Fact]
    public void Equality_is_by_name()
    {
        Assert.Equal(ValidationProfile.Named("Submit"), ValidationProfile.Submit);
        Assert.NotEqual(ValidationProfile.Draft, ValidationProfile.Submit);
        Assert.Equal("Draft", ValidationProfile.Draft.ToString());
    }

    [Fact]
    public void FromName_matches_built_in_names_case_insensitively_to_the_canonical_singletons()
    {
        Assert.Same(ValidationProfile.Draft, ValidationProfile.FromName("draft"));
        Assert.Same(ValidationProfile.Submit, ValidationProfile.FromName("SUBMIT"));
    }

    [Fact]
    public void FromName_builds_a_custom_profile_for_any_other_name()
    {
        var profile = ValidationProfile.FromName("Custom");

        Assert.Equal("Custom", profile.Name);
        Assert.True(profile.IncludeDefaultRules);
        Assert.Equal(new[] { "Custom" }, profile.RuleSets);
    }
}
