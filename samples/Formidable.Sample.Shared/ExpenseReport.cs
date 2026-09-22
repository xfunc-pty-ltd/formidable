using FluentValidation;

namespace Formidable.Sample.Shared;

public class ExpenseReport
{
    public string SubmitterName { get; set; } = string.Empty;
    public List<ExpenseLine> Lines { get; set; } = [];
}

public class ExpenseLine
{
    public string Description { get; set; } = string.Empty;
}

public class ExpenseReportValidator : DraftSubmitValidator<ExpenseReport>
{
    protected override void ConfigureDraftRules()
    {
        // Common bucket: enforced by a draft save and by a submit alike, same as every other
        // page's collection rules — SubmitterName's own rule sits beside it, unremarkable in
        // FluentValidation terms even though the field it targets is a plain native input.
        RuleFor(r => r.SubmitterName).NotEmpty().WithMessage("Submitter name is required");
        RuleForEach(r => r.Lines).ChildRules(line =>
            line.RuleFor(l => l.Description).NotEmpty().WithMessage("Description is required"));
    }

    protected override void ConfigureSubmitRules()
    {
    }
}
