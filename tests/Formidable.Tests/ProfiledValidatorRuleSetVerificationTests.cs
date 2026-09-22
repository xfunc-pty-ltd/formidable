using FluentValidation;
using Formidable.Tests.Fixtures;

namespace Formidable.Tests;

public class ProfiledValidatorRuleSetVerificationTests
{
    [Fact]
    public void Typo_d_ruleset_name_throws_naming_the_typo_and_the_available_rulesets()
    {
        var validator = new TestOrderValidator();
        var typo = ValidationProfile.Named("Sumbit", includeDefaultRules: true, "Sumbit");

        var exception = Assert.Throws<InvalidOperationException>(
            () => validator.Validate(new TestOrder(), typo));

        Assert.Contains("Sumbit", exception.Message);
        Assert.Contains(nameof(TestOrderValidator), exception.Message);
        Assert.Contains("Submit", exception.Message);
    }

    [Fact]
    public async Task Typo_d_ruleset_name_throws_on_the_async_path_too()
    {
        var validator = new TestOrderValidator();
        var typo = ValidationProfile.Named("Sumbit", includeDefaultRules: true, "Sumbit");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => validator.ValidateAsync(new TestOrder(), typo));

        Assert.Contains("Sumbit", exception.Message);
    }

    [Fact]
    public void Correct_ruleset_names_do_not_throw()
    {
        var validator = new TestOrderValidator();

        var result = validator.Validate(new TestOrder(), ValidationProfile.Submit);

        Assert.False(result.IsValid); // rules ran normally; no exception along the way
    }

    [Fact]
    public void Profile_with_no_ruleset_names_is_never_checked_against_the_validator()
    {
        var validator = new TestOrderValidator();

        // ValidationProfile.Draft carries no ruleset names at all -- nothing to verify, no
        // matter what the validator does or doesn't register.
        var result = validator.Validate(new TestOrder(), ValidationProfile.Draft);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Two_same_named_profiles_of_different_shapes_are_each_verified_on_their_own()
    {
        var validator = new TestOrderValidator();

        // Two DIFFERENT ValidationProfile objects sharing one Name. The cache key is the whole
        // profile, which compares by its full shape, so the first (valid) profile named "Once"
        // being verified and cached vouches for nothing about the second: a same-named profile
        // naming an unregistered ruleset is a different profile and gets its own check. Keying
        // on the name alone lets that typo through to FluentValidation, which runs zero rules
        // under a name it does not recognize and says nothing about it -- the exact failure
        // this verification exists to catch.
        var first = ValidationProfile.Named("Once", includeDefaultRules: true, "Submit");
        var second = ValidationProfile.Named("Once", includeDefaultRules: true, "Sumbit");

        validator.Validate(new TestOrder(), first);

        var exception = Assert.Throws<InvalidOperationException>(
            () => validator.Validate(new TestOrder(), second));

        Assert.Contains("Sumbit", exception.Message);
    }

    [Fact]
    public void A_validator_with_no_registered_rulesets_reports_none_as_available()
    {
        var validator = new EmptyProfiledValidator();
        var typo = ValidationProfile.Named("Sumbit", includeDefaultRules: true, "Sumbit");

        var exception = Assert.Throws<InvalidOperationException>(
            () => validator.Validate(new TestOrder(), typo));

        Assert.Contains("Sumbit", exception.Message);
        Assert.Contains(nameof(EmptyProfiledValidator), exception.Message);
    }

    private sealed class EmptyProfiledValidator : ProfiledValidator<TestOrder>
    {
    }

    [Fact]
    public void A_differently_cased_ruleset_name_matches_the_registered_ruleset_and_runs_it()
    {
        // FluentValidation's own RulesetValidatorSelector matches ruleset names case-insensitively
        // (decompile-verified: FluentValidation.Internal.RulesetValidatorSelector.CanExecute uses
        // StringComparer.OrdinalIgnoreCase). Verification must mirror that, or a validator that
        // declares "Submit" would reject a profile that requests "submit" -- a false positive that
        // breaks a combination FluentValidation itself has always accepted.
        var validator = new TestOrderValidator();
        var lowerCasedSubmit = ValidationProfile.Named("LowerSubmit", includeDefaultRules: true, "submit");

        var result = validator.Validate(new TestOrder(), lowerCasedSubmit);

        // No throw during verification, and the "Submit" ruleset's rules actually ran (not
        // silently skipped): TestOrder's default Customer is null, which fails Submit's NotNull.
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(TestOrder.Customer));
    }

    [Fact]
    public void Declared_Escalated_ruleset_matches_a_requested_lowercase_escalated_and_runs_it()
    {
        // The exact scenario from FluentValidation's own case-insensitive matching: a validator
        // declaring "Escalated" must accept a profile requesting "escalated".
        var validator = new EscalatedRuleSetValidator();
        var lowerCasedEscalated = ValidationProfile.Named("LowerEscalated", includeDefaultRules: false, "escalated");

        var result = validator.Validate(new TestOrder(), lowerCasedEscalated);

        // No throw, and the "Escalated" ruleset's rule actually ran: default Description "" fails
        // NotEmpty.
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(TestOrder.Description));
    }

    private sealed class EscalatedRuleSetValidator : ProfiledValidator<TestOrder>
    {
        protected override void ConfigureProfiles() =>
            Profile("Escalated", () => RuleFor(x => x.Description).NotEmpty());
    }

    [Fact]
    public void A_ruleset_declared_with_no_rules_inside_it_still_counts_as_registered()
    {
        // Formidable's own DraftSubmitValidator<T> declares "Submit" via Profile(...) even when
        // a particular subclass's ConfigureSubmitRules body adds no rules at all (an async-only
        // draft-axis test fixture, for instance) -- the ruleset is real, just momentarily empty.
        // Verification must key off the DECLARATION (the Profile(...) call), not off whether any
        // IValidationRule ended up carrying the name, or a legitimately-empty ruleset would read
        // as unregistered and throw.
        var validator = new EmptyRuleSetValidator();

        var result = validator.Validate(new TestOrder(), ValidationProfile.Submit);

        Assert.True(result.IsValid);
    }

    private sealed class EmptyRuleSetValidator : DraftSubmitValidator<TestOrder>
    {
        protected override void ConfigureDraftRules()
        {
        }

        protected override void ConfigureSubmitRules()
        {
            // deliberately empty -- pins the empty-ruleset-still-registered behavior above
        }
    }

    private sealed class CommaRuleSetValidator : ProfiledValidator<TestOrder>
    {
        protected override void ConfigureProfiles()
        {
            Profile("Alpha, Beta", () => RuleFor(x => x.Description).NotEmpty());
            Profile(" Gamma ", () => RuleFor(x => x.Customer).NotNull());
        }
    }

    [Theory]
    [InlineData("Alpha")]
    [InlineData("Beta")]
    [InlineData("Gamma")]
    public void A_ruleset_declared_in_a_comma_joined_name_is_registered_under_each_part(string requested)
    {
        // FluentValidation's own RuleSet splits a name on ',' and ';' and trims each part, so
        // one Profile call declaring "Alpha, Beta" tags its rules with two rulesets FV selects
        // individually. Recording the literal instead rejects the very names FV honours, which
        // turns a guard against a typo into a guard against FluentValidation's own idiom.
        var validator = new CommaRuleSetValidator();
        var profile = ValidationProfile.Named(requested, includeDefaultRules: false, requested);

        var result = validator.Validate(new TestOrder(), profile);

        // No throw during verification, and the ruleset's own rule ran: the empty default
        // Description fails NotEmpty and the null default Customer fails NotNull.
        Assert.False(result.IsValid);
    }

    private sealed class PlainCommaRuleSetValidator : AbstractValidator<TestOrder>
    {
        public PlainCommaRuleSetValidator() =>
            RuleSet("Alpha, Beta", () => RuleFor(x => x.Description).NotEmpty());
    }

    [Fact]
    public void A_plain_FluentValidation_validator_selects_the_same_comma_joined_ruleset()
    {
        // The ground truth the test above mirrors, taken from FluentValidation itself rather
        // than from a reading of it: the identical rule on a plain AbstractValidator, selected
        // by one of the two names the joined declaration carries, runs.
        var plain = new PlainCommaRuleSetValidator();

        var result = plain.Validate(new TestOrder(), options => options.IncludeRuleSets("Beta"));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.PropertyName == nameof(TestOrder.Description));
    }

    [Theory]
    [InlineData("default")]
    [InlineData("DEFAULT")]
    public void The_default_pseudo_ruleset_selects_the_unnamed_rules(string pseudo)
    {
        // "default" is FluentValidation's name for the rules outside every ruleset. It is not a
        // ruleset a validator can declare, so matching it against the declared names rejects a
        // selection FluentValidation carries out.
        var validator = new TestOrderValidator();
        var profile = ValidationProfile.Named("Pseudo" + pseudo, includeDefaultRules: false, pseudo);

        var result = validator.Validate(new TestOrder(), profile);

        // Only the draft rules ran, and a default TestOrder passes them: the submit bucket's
        // NotNull on Customer would have failed had the selection reached it.
        Assert.True(result.IsValid);
    }

    [Fact]
    public void A_wildcard_profile_runs_every_rule_the_validator_declares()
    {
        // The pseudo-ruleset above is not merely tolerated: "*" is what FluentValidation reads
        // it as, so the submit-bucket rules run without the profile naming their ruleset.
        var validator = new TestOrderValidator();
        var everything = ValidationProfile.Named("Everything", includeDefaultRules: false, "*");

        var result = validator.Validate(new TestOrder(), everything);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.PropertyName == nameof(TestOrder.Customer));
    }
}
