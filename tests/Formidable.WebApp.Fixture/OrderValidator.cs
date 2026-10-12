using FluentValidation;

namespace Formidable.WebApp.Fixture;

/// <summary>The order's rules, which the form checks and the page's stand-in server checks alike:
/// a description, and a SKU on every line.</summary>
public sealed class OrderValidator : AbstractValidator<Order>
{
    public OrderValidator()
    {
        RuleFor(o => o.Description).NotEmpty().WithMessage("Description is required");
        RuleForEach(o => o.Lines).ChildRules(line =>
            line.RuleFor(l => l.Sku).NotEmpty().WithMessage("SKU is required"));
    }
}
