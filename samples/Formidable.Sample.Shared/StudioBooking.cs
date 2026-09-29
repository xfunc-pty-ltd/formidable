using FluentValidation;

namespace Formidable.Sample.Shared;

public class StudioBooking
{
    // Nullable, as the value MudBlazor's text field and select hand back is: the model takes what
    // the control reports.
    public string? BandName { get; set; }
    public string? Room { get; set; }
}

// Plain FluentValidation: nothing in the rules knows which component library draws the form.
public class StudioBookingValidator : AbstractValidator<StudioBooking>
{
    public StudioBookingValidator()
    {
        RuleFor(b => b.BandName).NotEmpty().WithMessage("Band name is required");
        RuleFor(b => b.Room).NotEmpty().WithMessage("Room is required");
    }
}
