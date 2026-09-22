using Formidable.Blazor;
using Formidable.Sample.Shared;
using Microsoft.AspNetCore.Components.Forms;

namespace Formidable.Sample.Pages;

public partial class Normalize
{
    private readonly TrimmedNote _note = new();
    private FormidableForm<TrimmedNote>? _form;
    private string _status = string.Empty;

    private void NormalizeNow()
    {
        var titleBefore = _note.Title;
        var bodyBefore = _note.Body;
        _note.Normalize();

        // The engine listens for field-change notifications, not the model: code that mutates
        // the model notifies each changed field so live validation re-judges the cleaned value.
        var editContext = _form!.Engine!.EditContext;
        if (!string.Equals(titleBefore, _note.Title, StringComparison.Ordinal))
        {
            editContext.NotifyFieldChanged(new FieldIdentifier(_note, nameof(TrimmedNote.Title)));
        }

        if (!string.Equals(bodyBefore, _note.Body, StringComparison.Ordinal))
        {
            editContext.NotifyFieldChanged(new FieldIdentifier(_note, nameof(TrimmedNote.Body)));
        }
    }

    private async Task NormalizeAndSubmit()
    {
        _note.Normalize();
        var outcome = await _form!.SubmitAsync();
        _status = outcome.CanProceed
            ? "Submitted — the raw values above are exactly what the server would receive."
            : "Blocked by errors — fix them and resubmit.";
    }
}
