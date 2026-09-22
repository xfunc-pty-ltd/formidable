using FluentValidation;

namespace Formidable.Sample.Shared;

public class Listing
{
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Tags { get; set; } = string.Empty;
}

public class ListingValidator : DraftSubmitValidator<Listing>
{
    protected override void ConfigureDraftRules()
    {
        // Common bucket: enforced by a draft save and by a submit alike.
        RuleFor(l => l.Title).NotEmpty().WithMessage("Title is required");
        RuleFor(l => l.Description)
            .Must(d => !d.Contains('!'))
            .WithSeverity(Severity.Warning)
            .WithMessage("Exclamation marks read as shouty — consider removing them");
        RuleFor(l => l.Tags)
            .Must(t => t.Length == 0 || t.Split(',').Length <= 5)
            .WithSeverity(Severity.Info)
            .WithMessage("More than five tags rarely helps discovery");
    }

    protected override void ConfigureSubmitRules()
    {
    }
}
