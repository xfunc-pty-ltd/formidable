using Formidable.Blazor;
using Formidable.Sample.Components;
using Formidable.Sample.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;

namespace Formidable.Sample.Pages;

public partial class DialogSubmit
{
    private readonly InvoiceRequest _request = new();

    private FormidableForm<InvoiceRequest>? _form;
    private AnnouncementDialog? _announcement;
    private ElementReference _submitButton;
    private bool _waitForClose = true;
    private bool _autoFocusFirstError;
    private int _blockedFields;
    private string _status = string.Empty;

    private async Task AnnounceAsync(FormidableInvalidSubmitContext context)
    {
        // Said here because here is where it is known: a dialog is about to cover the form, so
        // the move the form makes next would put the caret in a box behind the overlay. The
        // toggle skips the call rather than reversing it, since there is nothing to reverse.
        if (!_autoFocusFirstError)
        {
            context.SuppressFirstErrorFocus();
        }

        // VisibleErrorSummary is already the distinct names, so the count agrees with the list
        // the dialog draws under it without the page counting anything itself.
        _blockedFields = context.Outcome.VisibleErrorSummary.Count;
        _status = string.Empty;

        await _announcement!.OpenAsync();
    }

    // The ways out of the dialog that name no field: Close and Escape. Nothing else is going to
    // move focus to a FIELD for them — no entry was clicked, so the summary has nobody to send
    // anywhere, and the dialog's own hand-back goes to the button that opened it — and the
    // visitor has just been told the form is not ready. It is the same move a blocked submit
    // makes for itself, asked for here at the moment it can land, so someone who closes without
    // choosing ends where the form would have put them had no dialog opened.
    private async Task ReturnToFirstErrorAsync() => await _form!.FocusFirstErrorAsync();

    // Runs before the summary attempts to focus a clicked entry's field, and the whole job here
    // is to be finished before it returns.
    private async ValueTask DismissAnnouncementAsync(FieldIdentifier field)
    {
        if (_announcement is null)
        {
            return;
        }

        if (_waitForClose)
        {
            await _announcement.CloseAsync();
            return;
        }

        // The other half of the toggle, kept so the difference is visible rather than described:
        // this begins the close and reports ready at once, so the focus move behind it happens
        // into a dialog still on screen, and the dialog's hand-back to Submit lands after it.
        _ = _announcement.CloseAsync();
    }

    // The entries are field names here rather than messages, and a display name is nullable: the
    // engine's own model-level issues carry none, and neither does an error mapped from a
    // ProblemDetails body. The property path stands in where an issue has one. An issue naming no
    // field at all takes the name the engine itself lists it under in
    // SubmitOutcome.VisibleErrorSummary, read from the options rather than written out again
    // here, so re-voicing that one string moves the entry and the count line above it together.
    private string NameOf(ValidationIssue issue) =>
        issue.DisplayName ?? (issue.Path.Length == 0
            ? _form!.Engine!.Options.ModelLevelDisplayName
            : issue.Path);

    private void HandleValid() => _status = "Submitted — the invoice request is on its way.";
}
