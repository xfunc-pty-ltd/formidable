using System.Net.Http.Json;
using Formidable;
using Formidable.Blazor;
using Formidable.Sample.Shared;

namespace Formidable.Sample.Pages;

public partial class ServerRoundTrip
{
    // True only in a HOSTED_DEMO build (see HostedDemoApiHandler) - static readonly rather than
    // const so the razor's @if is a real runtime branch, not something the compiler could ever
    // flag as unreachable in the build where it is always false.
#if HOSTED_DEMO
    private static readonly bool IsHostedDemo = true;
#else
    private static readonly bool IsHostedDemo = false;
#endif

    private readonly RoundTripOrder _order = new() { Lines = [new OrderLine()] };
    private FormidableForm<RoundTripOrder>? _form;
    private readonly List<string> _serverAdvisories = [];
    private string _status = string.Empty;
    private string _endpoint = "/api/orders/";

    private async Task Send()
    {
        // Normalizing before the POST keeps the client's line list identical to what the
        // server validates (its filter normalizes too) - so issue paths always match rows.
        _order.Normalize();
        _serverAdvisories.Clear();
        var response = await Http.PostAsJsonAsync(_endpoint, _order);

        if (response.IsSuccessStatusCode)
        {
            _status = "Server accepted the order.";
            return;
        }

        var problem = await response.Content.ReadFromJsonAsync<FormidableValidationProblem>();
        var issues = problem!.ToIssues();

        // The engine applies error-severity issues to fields; non-error issues are the
        // page's to present (a 400's advisories ride alongside its errors by contract).
        // Each call replaces the previous server verdict — pressing Send again with new
        // input swaps the old server errors for the new ones, rather than accumulating
        // them, so a corrected resubmission cannot leave stale errors behind.
        _form!.Engine!.ApplyServerIssues(issues);
        _serverAdvisories.AddRange(issues
            .Where(i => i.Severity != ValidationSeverity.Error)
            .Select(i => i.Message));
        _status = "Server rejected the order — its errors are now inline.";
    }
}
