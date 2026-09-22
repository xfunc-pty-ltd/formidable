using FluentValidation;

namespace Formidable.Sample.Shared;

public class SupportTicket
{
    public string Reference { get; set; } = string.Empty;
    public string Requester { get; set; } = string.Empty;
    public string RequesterEmail { get; set; } = string.Empty;
    public string Product { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Steps { get; set; } = string.Empty;
    public string Impact { get; set; } = string.Empty;
}

public class SupportTicketValidator : DraftSubmitValidator<SupportTicket>
{
    protected override void ConfigureDraftRules()
    {
        // Common bucket: enforced by a draft save and by a submit alike. Every rule names its
        // field with the label the page prints, because the summary can list names instead of
        // messages, and a name the form never shows reads as a list about some other form.

        // Two rules on one field, and an empty box fails both at once. That pair is what a list
        // of names has to collapse and a list of messages has to keep.
        RuleFor(t => t.Reference).NotEmpty()
            .WithName("Ticket reference").WithMessage("A ticket reference is required");
        RuleFor(t => t.Reference).Matches("^TKT-[0-9]{4}$")
            .WithName("Ticket reference").WithMessage("A ticket reference looks like TKT-0000");

        RuleFor(t => t.Requester).NotEmpty()
            .WithName("Requester").WithMessage("A requester is required");

        RuleFor(t => t.RequesterEmail).NotEmpty()
            .WithName("Requester email").WithMessage("A requester email is required");
        RuleFor(t => t.RequesterEmail).EmailAddress()
            .WithName("Requester email").WithMessage("That is not a valid email address")
            .When(t => t.RequesterEmail.Length > 0);

        RuleFor(t => t.Product).NotEmpty()
            .WithName("Product").WithMessage("A product is required");

        RuleFor(t => t.Version).NotEmpty()
            .WithName("Version").WithMessage("A version is required");

        RuleFor(t => t.Steps).NotEmpty()
            .WithName("Steps to reproduce").WithMessage("Describe the steps that reproduce it");

        // A warning rather than an error, so a blank submit fills two severity bands. One band
        // holding a single entry is what makes a cap of three visibly per-band: it caps the
        // errors and leaves this one alone.
        RuleFor(t => t.Impact).NotEmpty()
            .WithSeverity(Severity.Warning)
            .WithName("Impact")
            .WithMessage("Without an impact this ticket queues behind everything else");
    }

    protected override void ConfigureSubmitRules()
    {
    }
}
