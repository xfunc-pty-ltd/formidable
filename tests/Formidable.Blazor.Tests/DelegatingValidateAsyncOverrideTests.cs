using FluentValidation;
using Formidable.Introspection;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Time.Testing;

namespace Formidable.Blazor.Tests;

/// <summary>
/// A <see cref="DelegatingModelValidator{TModel}"/> subclass that adds a check of its own at
/// <c>ValidateAsync</c> and leaves <c>ValidateRulesAsync</c> alone has that check run by a form's
/// submit, so its error blocks the submit, over a validator whose rules carry <c>DependentRules</c>
/// and over one whose rules do not. The same subclass written at every entry point blocks it too.
/// </summary>
// Mutation that must break the first two rows: answer "no override" from the base's detection,
// and the submit is taken rule by rule, never reaches the check, and proceeds.
public class DelegatingValidateAsyncOverrideTests
{
    private const string Blocked = "The address is on the blocklist";

    public sealed class Contact
    {
        public string? Email { get; set; } = "ada@example.com";

        public string? Phone { get; set; } = "555 0100";
    }

    private sealed class DependentContactValidator : AbstractValidator<Contact>
    {
        public DependentContactValidator() =>
            RuleFor(m => m.Email).NotEmpty().DependentRules(() => RuleFor(m => m.Phone).NotEmpty());
    }

    private sealed class PlainContactValidator : AbstractValidator<Contact>
    {
        public PlainContactValidator()
        {
            RuleFor(m => m.Email).NotEmpty();
            RuleFor(m => m.Phone).NotEmpty();
        }
    }

    /// <summary>Adds the blocklist check at <c>ValidateAsync</c> and <c>Validate</c>, and counts the asynchronous calls.</summary>
    private class BlocklistAtValidateAsync(IModelValidator<Contact> inner) : DelegatingModelValidator<Contact>(inner)
    {
        public int ValidateAsyncCalls { get; private set; }

        protected static ValidationReport WithBlocklist(ValidationReport report) =>
            new([.. report.Issues, new ValidationIssue(nameof(Contact.Email), Blocked)]);

        public override async Task<ValidationReport> ValidateAsync(
            Contact model, ValidationProfile profile, CancellationToken cancellationToken = default)
        {
            ValidateAsyncCalls++;
            return WithBlocklist(await base.ValidateAsync(model, profile, cancellationToken));
        }

        public override ValidationReport Validate(Contact model, ValidationProfile profile) =>
            WithBlocklist(base.Validate(model, profile));
    }

    /// <summary>The same check written at every entry point, which keeps the rule-by-rule path.</summary>
    private sealed class BlocklistEverywhere(IModelValidator<Contact> inner) : BlocklistAtValidateAsync(inner)
    {
        public override async Task<RuleLevelResult> ValidateRulesAsync(
            Contact model, ValidationProfile profile, IReadOnlyList<RuleIdentity> rules,
            CancellationToken cancellationToken = default)
        {
            var result = await base.ValidateRulesAsync(model, profile, rules, cancellationToken);
            return result with { Report = WithBlocklist(result.Report) };
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task The_subclass_s_own_check_blocks_the_submit(bool dependentRules, bool everyEntryPoint)
    {
        var model = new Contact();
        IValidator<Contact> rules = dependentRules ? new DependentContactValidator() : new PlainContactValidator();
        var adapter = new FluentValidationModelValidator<Contact>(rules);
        var validator = everyEntryPoint ? new BlocklistEverywhere(adapter) : new BlocklistAtValidateAsync(adapter);
        using var engine = new FormidableEngine<Contact>(
            model,
            new EditContext(model),
            validator,
            new ReflectionModelIntrospector(),
            new FormidableOptions(),
            new FakeTimeProvider());

        var outcome = await engine.ValidateForSubmitAsync();

        Assert.False(outcome.CanProceed);
        Assert.Equal(everyEntryPoint, validator.CanValidateByRule);
        Assert.Equal(everyEntryPoint ? 0 : 1, validator.ValidateAsyncCalls);

        // The control: the rules alone pass this model, so the check is what blocks.
        Assert.True((await adapter.ValidateAsync(model, ValidationProfile.Submit)).IsValid);
    }
}
