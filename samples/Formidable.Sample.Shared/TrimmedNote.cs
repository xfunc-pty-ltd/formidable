using FluentValidation;

namespace Formidable.Sample.Shared;

public class TrimmedNote : INormalizableModel
{
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;

    public void Normalize()
    {
        Title = System.Text.RegularExpressions.Regex.Replace(Title.Trim(), @"\s+", " ");
        Body = Body.Trim();
    }
}

// Plain AbstractValidator, like QuickContact - Normalize() is this page's lesson, not profiles.
public class TrimmedNoteValidator : AbstractValidator<TrimmedNote>
{
    public TrimmedNoteValidator()
    {
        RuleFor(n => n.Title)
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("Title is required")
            .MaximumLength(40).WithMessage("Title is 40 characters max");
    }
}
