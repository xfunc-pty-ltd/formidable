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

    private FieldIdentifier SubmitterField => new(_report, nameof(ExpenseReport.SubmitterName));

    // The id an element has to carry for the focus service to find it. A Formidable input renders
    // its own; "Submitted by" is the page's plain InputText and renders nothing of the sort, so a
    // focus aimed at that field has no element to land on. Left unrendered until a focus actually
    // misses, and rendered from then on: supplying it is the whole job of the fallback below, and
    // rendering it up front, the way the native input on Vanilla interop does, is the other way to
    // close the same gap.
    private string? _submitterElementId;

    // EditForm's own OnValidSubmit funnels through EditContext.Validate(), which nothing here
    // subscribes to — attach mode leaves the page owning its submit handler. Calling the
    // validator rather than the engine beneath it is what brings the rest of the submit story
    // with it: a blocked submit lands the visitor on the first error, through the fallback below
    // when that error has no element to land on.
    private async Task HandleSubmit()
    {
        var outcome = await _validator!.ValidateForSubmitAsync();
        if (outcome.CanProceed)
        {
            _status = "Submitted — every line accounted for.";
        }
    }

    // One callback, wired to both seams that move focus for the visitor: the blocked submit's own
    // auto-focus and a click on a summary entry. Each hands over the field it could not reach and
    // retries the focus once if this returns true, so all this has to do is make an element
    // carrying that field's id exist. There is one miss here to recover: the id stays rendered
    // afterwards, so a later miss on this same field is something this cannot fix. Returning false
    // for anything else leaves the miss as-is, which is the honest answer for a row the page has
    // genuinely removed.
    private async ValueTask<bool> MakeSubmitterReachableAsync(FieldIdentifier field)
    {
        if (_submitterElementId is not null || !field.Equals(SubmitterField))
        {
            return false;
        }

        _submitterElementId = FormidableFieldId.For(SubmitterField);

        // The retry looks the element up by id, so the render this asks for has to reach the DOM
        // before the caller gets its answer.
        StateHasChanged();
        await Task.Yield();
        return true;
    }

    private void AddLine() => _report.Lines.Add(new ExpenseLine());

    // No NotifyChanged and no NotifyFieldSetChanged: nothing here rules on the list itself, so
    // there is no live verdict to refresh, and FormidableValidator notices the field the removed
    // line's own input unregisters without being told.
    private void RemoveLine(ExpenseLine line) => _report.Lines.Remove(line);
}
