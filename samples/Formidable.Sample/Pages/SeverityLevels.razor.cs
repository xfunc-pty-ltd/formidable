using Formidable.Blazor;
using Formidable.Sample.Shared;

namespace Formidable.Sample.Pages;

public partial class SeverityLevels
{
    private readonly Listing _listing = new();
    private FormidableForm<Listing>? _form;
    private string _status = string.Empty;

    private async Task Submit()
    {
        var outcome = await _form!.SubmitAsync();
        _status = outcome.CanProceed
            ? $"Submitted with {outcome.Report.Warnings.Count()} warning(s) and {outcome.Report.Infos.Count()} info(s) — none of them blocked."
            : "Blocked by errors — fix them and resubmit.";
    }
}
