using FluentValidation;

namespace Formidable.Sample.Shared;

public class SavedProposal
{
    public string Title { get; set; } = string.Empty;
    public string ContactEmail { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
}

public class SavedProposalValidator : DraftSubmitValidator<SavedProposal>
{
    protected override void ConfigureDraftRules()
    {
        // Format rules: enforced by a draft save and by a submit alike. The email carries one of
        // these AND a presence rule below, so an empty box and a half-typed address fail
        // different rules - which is what lets a form tell "you have not got to this yet" from
        // "this one is wrong".
        RuleFor(p => p.ContactEmail)
            .EmailAddress().WithMessage("That is not a valid email address")
            .When(p => p.ContactEmail.Length > 0);
    }

    protected override void ConfigureSubmitRules()
    {
        // Completeness rules: what a finished proposal has to carry.
        RuleFor(p => p.Title).NotEmpty().WithMessage("Title is required");
        RuleFor(p => p.ContactEmail).NotEmpty().WithMessage("A contact email is required");
        RuleFor(p => p.Summary).NotEmpty().WithMessage("A summary is required");
    }
}
