using Formidable.Blazor;
using Formidable.Sample.Shared;
using Microsoft.AspNetCore.Components.Forms;

namespace Formidable.Sample.Pages;

public partial class AttachMode
{
    private readonly ExpenseReport _report = new()
    {
        SubmitterName = "Ada",
        Lines = [new ExpenseLine { Description = "Rideshare" }, new ExpenseLine()]
    };

    private FormidableValidator<ExpenseReport>? _validator;
    private string _status = string.Empty;

    // FormidableValidator renders no <form> of its own, so the all-suppressed defensive gate's
    // summary entry has nowhere to land unless the page's own EditForm carries the id by hand —
    // the same wiring every FormidableForm-rooted page gets for free.
    private string GateId => FormidableFieldId.For(new FieldIdentifier(_report, string.Empty));

    // EditForm's own OnValidSubmit funnels through EditContext.Validate(), which nothing here
    // subscribes to — attach mode leaves the page owning its submit handler, so this calls the
    // engine directly, the same call FormidableForm's own SubmitAsync makes internally.
    private async Task HandleSubmit()
    {
        var outcome = await _validator!.Engine!.ValidateForSubmitAsync();
        if (outcome.CanProceed)
        {
            _status = "Submitted — every line accounted for.";
        }
    }

    private void AddLine() => _report.Lines.Add(new ExpenseLine());

    // No NotifyChanged and no NotifyFieldSetChanged: nothing here rules on the list itself, so
    // there is no live verdict to refresh, and FormidableValidator notices the field the removed
    // line's own input unregisters without being told.
    private void RemoveLine(ExpenseLine line) => _report.Lines.Remove(line);
}
