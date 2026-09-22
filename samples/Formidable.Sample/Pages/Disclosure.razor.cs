using Formidable.Blazor;
using Formidable.Sample.Shared;
using Microsoft.AspNetCore.Components;

namespace Formidable.Sample.Pages;

public partial class Disclosure
{
    private readonly TravelRequest _request = new();
    private readonly TravelRequest _inlineRequest = new();
    private readonly List<string> _suppressed = [];
    private FormidableOptions? _options;
    private FormidableForm<TravelRequest>? _form;
    private bool _showDetails;
    private bool _showInlineSection;
    private string _status = string.Empty;
    private string _inlineStatus = string.Empty;

    protected override void OnInitialized()
    {
        _options = new FormidableOptions
        {
            SuppressedIssueDiagnostic = issue =>
            {
                _suppressed.Add($"{issue.Path}: {issue.Message}");
                _ = InvokeAsync(StateHasChanged);
            }
        };
    }

    private void SetNeeds(bool value, FormidableFieldContext field)
    {
        _request.NeedsAccommodation = value;
        field.NotifyChanged();
    }

    private void SetInlineNeeds(bool value, FormidableFieldContext field)
    {
        _inlineRequest.NeedsAccommodation = value;
        field.NotifyChanged();
    }

    private void OnTypeChanged(ChangeEventArgs args, FormidableFieldContext field)
    {
        _request.AccommodationType = args.Value?.ToString() ?? string.Empty;
        field.NotifyChanged();
    }

    private async Task Submit()
    {
        _suppressed.Clear();
        _status = string.Empty;
        await _form!.SubmitAsync();
    }

    private void HandleValid() => _status = "Submitted — every disclosed rule passed.";

    private void HandleInlineValid() => _inlineStatus = "Submitted — the trip details check out.";
}
