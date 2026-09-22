using System.Net.Http.Json;
using Formidable;
using Formidable.Blazor;
using Formidable.Sample.Shared;

namespace Formidable.Sample.Pages;

public partial class ServerRoundTrip
{
    private readonly RoundTripOrder _order = new() { Lines = [new OrderLine()] };
    private FormidableForm<RoundTripOrder>? _form;
    private readonly List<string> _serverWarnings = [];
    private string _status = string.Empty;

    private async Task Send()
    {
        _serverWarnings.Clear();
        var response = await Http.PostAsJsonAsync("/api/orders/", _order);

        if (response.IsSuccessStatusCode)
        {
            _status = "Server accepted the order.";
            return;
        }

        var problem = await response.Content.ReadFromJsonAsync<FormidableValidationProblem>();
        var issues = problem!.ToIssues();

        // The engine applies error-severity issues to fields; non-error issues are the
        // page's to present (a 400's warnings ride alongside its errors by contract).
        // Each call replaces the previous server verdict — pressing Send again with new
        // input swaps the old server errors for the new ones, rather than accumulating
        // them, so a corrected resubmission cannot leave stale errors behind.
        _form!.Engine!.ApplyServerIssues(issues);
        _serverWarnings.AddRange(issues
            .Where(i => i.Severity != ValidationSeverity.Error)
            .Select(i => i.Message));
        _status = "Server rejected the order — its errors are now inline.";
    }
}
