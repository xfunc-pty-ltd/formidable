using Formidable.Blazor;
using Formidable.Sample.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;

namespace Formidable.Sample.Pages;

public partial class Disclosure
{
    private readonly TravelRequest _request = new();
    private readonly List<string> _suppressed = [];
    private FormidableOptions? _options;
    private FormidableForm<TravelRequest>? _form;
    private bool _showDetails;
    private string _status = string.Empty;

    // The defensive gate reports under the model-level field, which owns no input: without an
    // element carrying its id, the one summary entry a fully-suppressed submit produces would be
    // the one entry that goes nowhere. The form element itself is that element.
    private string FormGateId => FormidableFieldId.For(new FieldIdentifier(_request, string.Empty));

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
}
