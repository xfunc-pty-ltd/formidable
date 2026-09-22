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
    private string _status = string.Empty;
    private string _endpoint = "/api/orders/";

    private async Task Send()
    {
        // Normalizing before the POST keeps the client's line list identical to what the
        // server validates (its filter normalizes too) - so issue paths always match rows.
        _order.Normalize();
        var response = await Http.PostAsJsonAsync(_endpoint, _order);

        if (response.IsSuccessStatusCode)
        {
            _status = "Server accepted the order.";
            return;
        }

        // One call for the whole verdict: every issue lands on the field it names, at the
        // severity it carries, so the page needs no advisory plumbing of its own. Each call
        // replaces the previous server verdict — pressing Send again with new input swaps the
        // old messages for the new ones, rather than accumulating them, so a corrected
        // resubmission cannot leave a stale one behind.
        var problem = await response.Content.ReadFromJsonAsync<FormidableValidationProblem>();
        _form!.ApplyServerIssues(problem!);
        _status = "Server rejected the order — its verdict is now inline.";
    }
}
