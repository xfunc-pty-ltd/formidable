using Formidable.Blazor;
using Formidable.Sample.Shared;
using Microsoft.AspNetCore.Components;

namespace Formidable.Sample.Pages;

public partial class Disclosure
{
    private readonly TravelRequest _request = new();
    private readonly List<string> _suppressed = [];
    private FormidableOptions? _options;
    private bool _showDetails;
    private string _status = string.Empty;

    protected override void OnInitialized()
    {
        _options = new FormidableOptions
        {
            SuppressedIssueDiagnostic = issue => _suppressed.Add($"{issue.Path}: {issue.Message}")
        };
    }

    private void SetNeeds(bool value) => _request.NeedsAccommodation = value;

    private void OnTypeChanged(ChangeEventArgs args) =>
        _request.AccommodationType = args.Value?.ToString() ?? string.Empty;

    private void HandleValid() => _status = "Submitted — every disclosed rule passed.";
}
