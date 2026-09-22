using FluentValidation;

namespace Formidable.Sample.Shared;

public class InvoiceRequest
{
    public string Reference { get; set; } = string.Empty;
    public string SupplierName { get; set; } = string.Empty;
    public string SupplierEmail { get; set; } = string.Empty;
    public string CostCentre { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Description { get; set; } = string.Empty;
}

public class InvoiceRequestValidator : DraftSubmitValidator<InvoiceRequest>
{
    protected override void ConfigureDraftRules()
    {
        // Common bucket: enforced by a draft save and by a submit alike. Every rule names its
        // field with the label the page prints, because the dialog lists names rather than
        // messages: a list of names the form never uses reads as a list about some other form.
        RuleFor(r => r.Reference).NotEmpty()
            .WithName("Invoice reference").WithMessage("An invoice reference is required");

        // A second rule on the same field, and one an empty box fails too. That is what gives a
        // blank submit two complaints about one field, which is the shape a list of names has to
        // collapse and a list of messages has to keep.
        RuleFor(r => r.Reference).Matches("^INV-[0-9]{4}$")
            .WithName("Invoice reference").WithMessage("An invoice reference looks like INV-0000");

        RuleFor(r => r.SupplierName).NotEmpty()
            .WithName("Supplier").WithMessage("A supplier is required");

        RuleFor(r => r.SupplierEmail).NotEmpty()
            .WithName("Supplier email").WithMessage("A supplier email is required");
        RuleFor(r => r.SupplierEmail).EmailAddress()
            .WithName("Supplier email").WithMessage("That is not a valid email address")
            .When(r => r.SupplierEmail.Length > 0);

        RuleFor(r => r.CostCentre).NotEmpty()
            .WithName("Cost centre").WithMessage("A cost centre is required");

        RuleFor(r => r.Amount).GreaterThan(0)
            .WithName("Amount").WithMessage("An amount greater than zero is required");

        RuleFor(r => r.Description).NotEmpty()
            .WithName("Description").WithMessage("Describe what the invoice is for");
    }

    protected override void ConfigureSubmitRules()
    {
    }
}
