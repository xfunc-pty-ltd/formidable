using System.Net;
using System.Text;
using System.Text.Json;

namespace Formidable.Tutorial.Services;

/// <summary>
/// Stands in for a real HTTP endpoint so Stage 6's round trip runs entirely in the browser.
/// Registered as the browser <see cref="HttpClient"/>'s handler (see <c>Program.cs</c>) — the
/// page code posts and reads exactly as it would against a real API; this is the only place
/// that knows the API isn't real.
/// </summary>
public sealed class FakeRegistrationApi : HttpMessageHandler
{
    private const string RejectedEmailSuffix = "@personal.example";

    private const string RejectionBody = """
        {
          "errors": {
            "Email": ["Use your work email address — personal domains are not accepted for team accounts"]
          }
        }
        """;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method != HttpMethod.Post
            || request.RequestUri?.AbsolutePath != "/api/signups"
            || request.Content is null)
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        var body = await request.Content.ReadAsStringAsync(cancellationToken);
        var email = ReadEmail(body);

        if (email is not null && email.EndsWith(RejectedEmailSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(RejectionBody, Encoding.UTF8, "application/json"),
            };
        }

        return new HttpResponseMessage(HttpStatusCode.OK);
    }

    // PostAsJsonAsync serializes with the System.Net.Http.Json web defaults, which write property
    // names in camelCase - "email", not "Email" - regardless of what the C# property is called.
    private static string? ReadEmail(string requestBody)
    {
        using var document = JsonDocument.Parse(requestBody);
        return document.RootElement.TryGetProperty("email", out var email) ? email.GetString() : null;
    }
}
