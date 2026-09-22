using FluentValidation;

namespace Formidable.Sample.Shared;

public class DraftedBrief
{
    public string Title { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
}

public class DraftedBriefValidator : DraftSubmitValidator<DraftedBrief>
{
    protected override void ConfigureDraftRules()
    {
        // Format rules: enforced always, including while drafting.
        RuleFor(b => b.Title).MaximumLength(60).WithMessage("Title is 60 characters max");
    }

    protected override void ConfigureSubmitRules()
    {
        // Completeness rules: the submit bucket, so a draft save leaves them alone.
        RuleFor(b => b.Title).NotEmpty().WithMessage("Title is required to submit");
        RuleFor(b => b.Summary).NotEmpty().WithMessage("Summary is required to submit");
    }
}
