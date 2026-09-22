using FluentValidation;

namespace Formidable.Sample.Shared;

public class TravelRequest
{
    public string Destination { get; set; } = string.Empty;
    public string TravelerName { get; set; } = string.Empty;
    public bool? NeedsAccommodation { get; set; }
    public string AccommodationType { get; set; } = string.Empty;
    public string SpecialRequirements { get; set; } = string.Empty;
}

public class TravelRequestValidator : DraftSubmitValidator<TravelRequest>
{
    protected override void ConfigureDraftRules()
    {
        // Common bucket: enforced by a draft save and by a submit alike.
        RuleFor(t => t.Destination).NotEmpty().WithMessage("Destination is required");
        RuleFor(t => t.TravelerName).NotEmpty().WithMessage("Traveler name is required");
        RuleFor(t => t.NeedsAccommodation).NotNull().WithMessage("Answer the accommodation question");
        RuleFor(t => t.AccommodationType).NotEmpty().WithMessage("Choose an accommodation type")
            .When(t => t.NeedsAccommodation == true);
        RuleFor(t => t.SpecialRequirements).NotEmpty().WithMessage("Describe the special requirements")
            .When(t => t.NeedsAccommodation == true && t.AccommodationType == "Accessible");
    }

    protected override void ConfigureSubmitRules()
    {
    }
}
