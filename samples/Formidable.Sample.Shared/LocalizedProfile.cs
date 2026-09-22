using System.Globalization;
using FluentValidation;
using Formidable.Sample.Shared.Resources;

namespace Formidable.Sample.Shared;

public class LocalizedProfile
{
    public string FullName { get; set; } = string.Empty;

    // Age is a string so the rule judges what was actually typed: a non-numeric entry and an
    // out-of-range one fail the same rule and share the one custom message.
    public string Age { get; set; } = string.Empty;
}

public class LocalizedProfileValidator : AbstractValidator<LocalizedProfile>
{
    public LocalizedProfileValidator()
    {
        // Deliberately no WithMessage: FluentValidation carries translations of its own default
        // messages and chooses one by CultureInfo.CurrentUICulture, so this rule localizes itself.
        RuleFor(p => p.FullName).NotEmpty();

        // The message-factory overload defers the resx lookup to validation time, so the message
        // follows the culture in force when the rule runs - not the one the validator was built in.
        RuleFor(p => p.Age).Must(BeAWholeAgeInRange).WithMessage(_ => ValidationMessages.AgeRange);
    }

    private static bool BeAWholeAgeInRange(string age) =>
        int.TryParse(age, NumberStyles.Integer, CultureInfo.InvariantCulture, out var years)
        && years is >= 18 and <= 130;
}
