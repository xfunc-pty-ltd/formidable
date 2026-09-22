using System.Globalization;
using FluentValidation;

namespace Formidable.Sample.Shared;

public sealed class Attendee
{
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
}

public sealed class SessionPick
{
    public int Id { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Seats { get; set; } = "0";
}

public sealed class EventRegistration : INormalizableModel
{
    public string ContactEmail { get; set; } = string.Empty;
    public string EventName { get; set; } = string.Empty;
    public string EventDate { get; set; } = string.Empty;          // yyyy-MM-dd from <input type="date">
    public string EarlyBirdDeadline { get; set; } = string.Empty;  // yyyy-MM-dd
    public string Description { get; set; } = string.Empty;
    public string CouponCode { get; set; } = string.Empty;
    public bool IncludeCatering { get; set; }
    public string CateringHeadcount { get; set; } = string.Empty;
    public string DietaryNotes { get; set; } = string.Empty;
    public string TicketTier { get; set; } = string.Empty;
    public string VenueRegion { get; set; } = string.Empty;
    public List<Attendee> Attendees { get; set; } = [];
    public List<SessionPick> Sessions { get; set; } = [];

    public void Normalize()
    {
        ContactEmail = ContactEmail.Trim();
        EventName = System.Text.RegularExpressions.Regex.Replace(EventName.Trim(), @"\s+", " ");
        Description = Description.Trim();
        CouponCode = CouponCode.Trim().ToUpperInvariant();
    }
}

public class EventRegistrationValidator : DraftSubmitValidator<EventRegistration>
{
    private static bool TryParseDate(string value, out DateTime date) =>
        DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    protected override void ConfigureDraftRules()
    {
        // Format/malformed-value rules: enforced always, including while drafting.
        RuleFor(r => r.ContactEmail)
            .EmailAddress()
            .WithMessage("Contact email must be a valid email address")
            .When(r => !string.IsNullOrEmpty(r.ContactEmail));

        // Fixed short delay keeps the composite deterministic for E2E; the /async page owns
        // the adjustable-delay lesson.
        RuleFor(r => r.ContactEmail)
            .MustAsync(async (email, cancellationToken) =>
            {
                await Task.Delay(300, cancellationToken);
                return !string.Equals(email.Trim(), "taken@example.com", StringComparison.OrdinalIgnoreCase);
            })
            .WithMessage("That email is already registered")
            .When(r => !string.IsNullOrEmpty(r.ContactEmail));

        RuleFor(r => r.EventDate)
            .Must(d => TryParseDate(d, out _))
            .WithMessage("Event date must be a valid date")
            .When(r => !string.IsNullOrEmpty(r.EventDate));

        RuleFor(r => r.EarlyBirdDeadline)
            .Must(d => TryParseDate(d, out _))
            .WithMessage("Early-bird deadline must be a valid date")
            .When(r => !string.IsNullOrEmpty(r.EarlyBirdDeadline));

        RuleFor(r => r.EarlyBirdDeadline)
            .Must((r, deadline) =>
            {
                TryParseDate(deadline, out var deadlineDate);
                TryParseDate(r.EventDate, out var eventDate);
                return deadlineDate <= eventDate;
            })
            .WithMessage("Early-bird deadline must be on or before the event date")
            .When(r => TryParseDate(r.EventDate, out _) && TryParseDate(r.EarlyBirdDeadline, out _));

        RuleFor(r => r.CateringHeadcount)
            .Must(v => int.TryParse(v, out var n) && n is >= 1 and <= 500)
            .WithMessage("Catering headcount must be a whole number between 1 and 500")
            .When(r => r.IncludeCatering && !string.IsNullOrEmpty(r.CateringHeadcount));

        RuleForEach(r => r.Sessions).ChildRules(session =>
            session.RuleFor(s => s.Seats)
                .Must(v => int.TryParse(v, out var n) && n is >= 0 and <= 500)
                .WithMessage("Seats must be a whole number between 0 and 500"));

        RuleFor(r => r.Attendees)
            .Must(a => a.Count <= 10)
            .WithSeverity(Severity.Warning)
            .WithMessage("More than 10 attendees needs approval — submission is not blocked");

        RuleFor(r => r.Attendees)
            .Must(a => a.Count > 0)
            .WithSeverity(Severity.Info)
            .WithMessage("You can add attendees now or after registering");

        // Deliberately unconditional: the UI alone gates this field's visibility, so the rule
        // stays registered whether or not catering is selected - the one shape that exercises
        // disclosure suppression and its all-suppressed defensive gate.
        RuleFor(r => r.DietaryNotes).NotEmpty().WithMessage("Dietary notes are required for catering");
    }

    protected override void ConfigureSubmitRules()
    {
        // Presence rules: enforced only at submit.
        RuleFor(r => r.ContactEmail).NotEmpty().WithMessage("Contact email is required");
        RuleFor(r => r.EventName).NotEmpty().WithMessage("Event name is required");
        RuleFor(r => r.EventDate).NotEmpty().WithMessage("Event date is required");
        RuleFor(r => r.TicketTier).NotEmpty().WithMessage("Ticket tier is required");
        RuleFor(r => r.VenueRegion).NotEmpty().WithMessage("Venue region is required");

        RuleForEach(r => r.Attendees).ChildRules(attendee =>
        {
            attendee.RuleFor(a => a.Name).NotEmpty().WithMessage("Attendee name is required");
            attendee.RuleFor(a => a.Email)
                .EmailAddress()
                .WithMessage("Attendee email must be a valid email address")
                .When(a => !string.IsNullOrEmpty(a.Email));
        });
    }
}
