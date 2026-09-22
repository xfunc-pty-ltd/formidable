using FluentValidation;

namespace Formidable.Sample.Shared;

public class RoundTripOrder : INormalizableModel
{
    public string Description { get; set; } = string.Empty;
    public List<OrderLine> Lines { get; set; } = [];

    public void Normalize()
    {
        // Whitespace-only SKUs are noise - drop those lines entirely. A genuinely empty
        // SKU ("") survives on purpose so NotEmpty can point at the row.
        Lines.RemoveAll(line => line.Sku.Length > 0 && string.IsNullOrWhiteSpace(line.Sku));
    }
}

public class OrderLine
{
    public string Sku { get; set; } = string.Empty;
}

public class RoundTripOrderValidator : DraftSubmitValidator<RoundTripOrder>
{
    protected override void ConfigureDraftRules()
    {
    }

    protected override void ConfigureSubmitRules()
    {
        RuleFor(o => o.Description).NotEmpty().WithMessage("Description is required");
        RuleFor(o => o.Description)
            .Must(d => !d.Contains('-'))
            .WithSeverity(Severity.Warning)
            .WithMessage("Hyphens make order references harder to read aloud");
        RuleForEach(o => o.Lines).ChildRules(line =>
            line.RuleFor(l => l.Sku).NotEmpty().WithMessage("SKU is required"));
    }
}
