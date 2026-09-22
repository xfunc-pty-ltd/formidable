using FluentValidation;

namespace Formidable.WebApp.Fixture;

/// <summary>The quickstart's validator, message for message, so a hosting test asserts the same
/// strings the sample's own journeys do.</summary>
public sealed class ContactValidator : AbstractValidator<Contact>
{
    public ContactValidator()
    {
        RuleFor(c => c.Name).NotEmpty().WithMessage("Name is required");
        RuleFor(c => c.Email).Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("Email is required")
            .EmailAddress().WithMessage("Enter a valid email address");
    }
}
