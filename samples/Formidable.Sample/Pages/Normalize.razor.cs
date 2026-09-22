using Formidable.Blazor;
using Formidable.Sample.Shared;

namespace Formidable.Sample.Pages;

public partial class Normalize
{
    private readonly TrimmedNote _note = new();
    private FormidableForm<TrimmedNote>? _form;
    private string _status = string.Empty;

    private void NormalizeNow() => _note.Normalize();

    private async Task NormalizeAndSubmit()
    {
        _note.Normalize();
        var outcome = await _form!.SubmitAsync();
        _status = outcome.CanProceed
            ? "Submitted — the raw values above are exactly what the server would receive."
            : "Blocked by errors — fix them and resubmit.";
    }
}
