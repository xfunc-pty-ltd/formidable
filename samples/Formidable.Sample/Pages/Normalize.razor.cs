using Formidable.Blazor;
using Formidable.Sample.Shared;
using Microsoft.AspNetCore.Components.Forms;

namespace Formidable.Sample.Pages;

public partial class Normalize
{
    private readonly TrimmedNote _note = new();
    private readonly FormidableOptions _options = new();
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

    // No manual Normalize() call here — this is the plain submit path, so whether the raw
    // values below get cleaned before validation depends entirely on the NormalizeOnSubmit
    // toggle above the form.
    private async Task Submit()
    {
        var outcome = await _form!.SubmitAsync();
        _status = outcome.CanProceed
            ? "Submitted — see whether the raw values below changed."
            : "Blocked by errors — fix them and resubmit.";
    }
}
