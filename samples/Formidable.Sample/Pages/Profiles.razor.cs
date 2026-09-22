using Formidable;
using Formidable.Blazor;
using Formidable.Sample.Shared;
using Microsoft.AspNetCore.Components;

namespace Formidable.Sample.Pages;

public partial class Profiles
{
    [Inject] private IModelValidator<DraftedBrief> Validator { get; set; } = default!;

    private readonly DraftedBrief _brief = new();
    private FormidableForm<DraftedBrief>? _form;
    private string _status = string.Empty;

    private void HandleValid() =>
        _status = "Submitted — format and completeness rules all passed.";

    private async Task SaveDraft()
    {
        var report = await Validator.ValidateAsync(_brief, ValidationProfile.Draft);
        _status = report.IsValid
            ? "Draft saved — completeness rules were not enforced."
            : $"Draft blocked by format rules: {string.Join("; ", report.Errors.Select(e => e.Message))}";
    }

    private async Task Reset()
    {
        await _form!.ResetAsync();
        _status = string.Empty;
    }
}
