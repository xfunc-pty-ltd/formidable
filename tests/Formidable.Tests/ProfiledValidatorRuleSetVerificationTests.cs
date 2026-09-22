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
    public void Verification_is_cached_by_profile_name_not_by_profile_instance()
    {
        var validator = new TestOrderValidator();

        // Two DIFFERENT ValidationProfile objects sharing one Name -- the cache key -- prove the
        // cache is real: the first (valid) profile named "Once" gets verified and cached under
        // that name. A second, differently-shaped profile object that reuses the same name but
        // names an unregistered ruleset is never re-checked, because the cache short-circuits on
        // the name alone (mirroring ValidationProfile's own by-Name equality contract, see
        // ValidationProfileTests.Equality_is_by_name, rather than inventing a stricter cache key).
        // The typo'd ruleset therefore reaches FluentValidation itself, which silently runs zero
        // rules under a name it doesn't recognize -- exactly the failure mode this whole feature
        // exists to catch, except here the cache is the reason it slips through. This is
        // a known, accepted edge case (constructing two same-named profiles is already unusual;
        // see the class remarks), not a gap this test is pretending doesn't exist.
        var first = ValidationProfile.Named("Once", includeDefaultRules: true, "Submit");
        var second = ValidationProfile.Named("Once", includeDefaultRules: true, "Sumbit");

        validator.Validate(new TestOrder(), first);
        var result = validator.Validate(new TestOrder(), second);

        // Only the draft rule ran (Description's default "" passes MaximumLength(10)); the
        // "Sumbit" ruleset matched nothing and FluentValidation stayed silent about it -- proof
        // the second call skipped verification entirely rather than catching the typo.
        Assert.True(result.IsValid);
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
}
