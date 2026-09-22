using FluentValidation;

namespace Formidable.Sample.Shared;

public class ReviewedPost
{
    public string Title { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public int? ReadMinutes { get; set; }
    public DateOnly? PublishDate { get; set; }
    public string ReviewNote { get; set; } = string.Empty;
}

// ProfiledValidator directly, not DraftSubmitValidator - this page's lesson is arbitrary
// named profiles, not the built-in draft/submit lifecycle. AdminReview composes the
// built-in Submit ruleset with a third ruleset of its own.
public class ReviewedPostValidator : ProfiledValidator<ReviewedPost>
{
    /// <summary>
    /// Default rules, plus <see cref="ValidationProfile.Submit"/>'s completeness ruleset,
    /// plus this validator's own "AdminReview" ruleset.
    /// </summary>
    public static readonly ValidationProfile AdminReview =
        ValidationProfile.Named("AdminReview", includeDefaultRules: true, ValidationProfile.SubmitRuleSetName, "AdminReview");

    protected override void ConfigureCommonRules() =>
        RuleFor(p => p.Title).NotEmpty().WithMessage("Title is required");

    protected override void ConfigureProfiles()
    {
        Profile(ValidationProfile.SubmitRuleSetName, () =>
        {
            RuleFor(p => p.Slug).NotEmpty().WithMessage("Slug is required");
            RuleFor(p => p.Category).NotEmpty().WithMessage("Category is required");
            RuleFor(p => p.ReadMinutes)
                .Cascade(CascadeMode.Stop)
                .NotNull().WithMessage("Read time is required")
                .InclusiveBetween(1, 180).WithMessage("Read time must be between 1 and 180 minutes");
            RuleFor(p => p.PublishDate).NotNull().WithMessage("Publish date is required");
        });

        Profile("AdminReview", () =>
            RuleFor(p => p.ReviewNote).NotEmpty().WithMessage("A review note is required for admin review"));
    }
}
