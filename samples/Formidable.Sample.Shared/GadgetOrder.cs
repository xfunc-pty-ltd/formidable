using FluentValidation;

namespace Formidable.Sample.Shared;

public class GadgetOrder
{
    public string Colour { get; set; } = string.Empty;
    public string Nickname { get; set; } = string.Empty;
    public List<Gadget> Gadgets { get; set; } = [];
}

public class Gadget
{
    public string Serial { get; set; } = string.Empty;
}

public class GadgetOrderValidator : DraftSubmitValidator<GadgetOrder>
{
    protected override void ConfigureDraftRules()
    {
        // Common bucket: enforced by a draft save and by a submit alike.
        RuleFor(g => g.Colour).NotEmpty().WithMessage("Colour is required");
        RuleFor(g => g.Nickname).NotEmpty().WithMessage("Nickname is required");
        RuleForEach(g => g.Gadgets).ChildRules(gadget =>
            gadget.RuleFor(x => x.Serial).NotEmpty().WithMessage("Serial is required"));
    }

    protected override void ConfigureSubmitRules()
    {
    }
}
