using FluentValidation;
using Formidable;

namespace Formidable.Tests.Fixtures;

/// <summary>
/// Exercises every mapping feature: draft rules, submit rules, display names, error codes,
/// severities, nested and per-item rules.
/// </summary>
public sealed class TestOrderValidator : DraftSubmitValidator<TestOrder>
{
    protected override void ConfigureDraftRules()
    {
        RuleFor(x => x.Description).MaximumLength(10);
        RuleForEach(x => x.LineItems).ChildRules(item =>
            item.RuleFor(x => x.Quantity).LessThanOrEqualTo(100));
    }

    protected override void ConfigureSubmitRules()
    {
        RuleFor(x => x.Description)
            .NotEmpty()
            .WithName("Order description")
            .WithErrorCode("DESC_REQUIRED");

        RuleFor(x => x.Customer).NotNull();

        RuleForEach(x => x.LineItems).ChildRules(item =>
            item.RuleFor(x => x.Sku).NotEmpty());

        RuleFor(x => x.Description)
            .Must(d => !d.Contains('-'))
            .WithSeverity(Severity.Warning)
            .WithMessage("Hyphenated descriptions are discouraged");
    }
}
