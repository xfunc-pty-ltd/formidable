using FluentValidation;

namespace Formidable.Sample.Shared;

public class QuickContact
{
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
}

// The five-minute experience: one plain FluentValidation validator, no profiles.
// Formidable's Draft/Submit profiles both include default rules, so an ordinary
// AbstractValidator works unchanged.
public class QuickContactValidator : AbstractValidator<QuickContact>
{
    public QuickContactValidator()
    {
        RuleFor(c => c.Name).NotEmpty().WithMessage("Name is required");
        RuleFor(c => c.Email).NotEmpty().EmailAddress().WithMessage("A valid email is required");
    }
}
