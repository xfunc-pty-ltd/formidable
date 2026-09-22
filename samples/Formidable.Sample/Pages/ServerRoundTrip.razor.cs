using System.Net.Http.Json;
using System.Text.Json;
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

    // The whole point of this page is that the SERVER is the judge, so the live channel is
    // narrowed to the profile that holds no opinion: RoundTripOrderValidator's draft bucket is
    // empty, which makes "no client rule runs while you type" literal rather than a matter of
    // which rules happen to be cheap. Left unset, the live channel would follow the submit
    // profile and answer for the description and every SKU before the POST ever went out - the
    // right default for a form the client judges, and the wrong one for this demonstration.
    private readonly FormidableOptions _options = new() { LiveProfile = ValidationProfile.Draft };

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

        // A 400 says the request was rejected, not that the endpoint is what rejected it. A
        // reverse proxy, a gateway or a WAF in front of it answers with its own HTML page or its
        // own JSON, and the parse reads the Content-Type header's character set as well as the
        // body, so either can be something it cannot make sense of. The JSON literal null throws
        // nothing and deserializes to nothing at all. A rejection the page cannot read is still a
        // rejection, and none of these is an exception the visitor should meet.
        FormidableValidationProblem? problem;
        try
        {
            // The generated metadata, not the plain generic overload: a trimmed publish (what a
            // Release build of a WebAssembly app produces) cannot deserialize the type by
            // reflection, and the guard below catches that refusal too rather than let it reach
            // the visitor.
            problem = await response.Content.ReadFromJsonAsync(
                FormidableValidationProblemJsonContext.Default.FormidableValidationProblem);
        }
        catch (Exception ex)
            when (ex is JsonException or InvalidOperationException or NotSupportedException)
        {
            problem = null;
        }

        if (problem is null)
        {
            // The last verdict stays on screen. An unreadable response is no evidence that it
            // stopped being true, and the reverse case is real too: a corrected resubmission that
            // comes back unreadable leaves the old reasons standing under the new status line. A
            // page that would rather show nothing hands ApplyServerIssues an empty sequence here.
            _status = "Rejected — but the response is not a verdict this page can read.";
            return;
        }

        // One call for the whole verdict: every issue lands on the field it names, at the
        // severity it carries, so the page needs no advisory plumbing of its own. Each call
        // replaces the previous server verdict — pressing Send again with new input swaps the
        // old messages for the new ones, rather than accumulating them, so a corrected
        // resubmission cannot leave a stale one behind.
        _form!.ApplyServerIssues(problem);
        _status = "Server rejected the order — its verdict is now inline.";
    }
}
