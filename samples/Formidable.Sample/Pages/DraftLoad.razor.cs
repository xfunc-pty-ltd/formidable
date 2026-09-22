using Formidable.Blazor;
using Formidable.Sample.Shared;

namespace Formidable.Sample.Pages;

public partial class DraftLoad
{
    private readonly SavedProposal _proposal = new();
    private FormidableForm<SavedProposal>? _form;
    private string _status = string.Empty;

    private async Task LoadSavedDraft()
    {
        // Stands in for the round trip a real page would make. Nothing here notifies the
        // EditContext, which is exactly the situation the call below exists for: writing model
        // properties leaves the form as pristine as it was, however much it now holds.
        _proposal.Title = "Progressive disclosure in practice";
        _proposal.ContactEmail = "ada.lovelace";
        _proposal.Summary = string.Empty;

        await _form!.DiscloseLoadedValuesAsync();
        _status = "Loaded the saved draft.";
    }

    private async Task StartBlank()
    {
        // ResetAsync returns the form to pristine and never writes model properties, so a page
        // that wants empty boxes as well as a clean slate clears the model itself first.
        _proposal.Title = string.Empty;
        _proposal.ContactEmail = string.Empty;
        _proposal.Summary = string.Empty;

        await _form!.ResetAsync();
        _status = string.Empty;
    }

    private void HandleValid() => _status = "Submitted — the proposal is complete.";
}
