#if HOSTED_DEMO
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Formidable;
using Formidable.Sample.Shared;

namespace Formidable.Sample;

// GitHub Pages hosts static files only, so the real API this sample otherwise talks to on
// localhost is unreachable from a deployed build. This handler stands in for it: it runs the
// same Shared validators, under the same Submit profile, that the real API's endpoint filter
// runs, and answers in the same wire shapes - so the round-trip and coupon-rejection lessons on
// /server and /workout survive without a server behind them. It only exists in HOSTED_DEMO
// builds; a normal build never compiles this type and talks to the real API exactly as before.
internal sealed class HostedDemoApiHandler : DelegatingHandler
{
    private const string ProblemType = "https://tools.ietf.org/html/rfc9110#section-15.5.1";
    private const string ProblemTitle = "One or more validation errors occurred.";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IModelValidator<RoundTripOrder> _orderValidator;
    private readonly IModelValidator<EventRegistration> _registrationValidator;

    public HostedDemoApiHandler(
        IModelValidator<RoundTripOrder> orderValidator,
        IModelValidator<EventRegistration> registrationValidator)
    {
        _orderValidator = orderValidator;
        _registrationValidator = registrationValidator;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method == HttpMethod.Post)
        {
            switch (request.RequestUri?.AbsolutePath)
            {
                // Same filter (RoundTripOrderValidator, Submit profile) behind both hosting
                // styles the /server page can pick - see ValidationEndpointFilter<TModel> and
                // OrdersController's [Validate] attribute.
                case "/api/orders/":
                case "/api/controller/orders":
                    return HandleOrderAsync(request, cancellationToken);

                case "/api/registrations/":
                    return HandleRegistrationAsync(request, cancellationToken);
            }
        }

        // Nothing else in this sample calls Http - reaching here means a page was added that
        // talks to a path this handler does not know how to answer.
        throw new InvalidOperationException(
            $"The hosted demo has no server behind it and does not simulate {request.Method} {request.RequestUri}.");
    }

    private async Task<HttpResponseMessage> HandleOrderAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var order = await request.Content!.ReadFromJsonAsync<RoundTripOrder>(JsonOptions, cancellationToken)
            ?? new RoundTripOrder();

        // Mirrors ValidationEndpointFilter<TModel>: normalize, then validate under Submit.
        order.Normalize();
        var report = await _orderValidator.ValidateAsync(order, ValidationProfile.Submit, cancellationToken);

        return report.IsValid
            ? JsonResponse(HttpStatusCode.OK, new { accepted = true, lines = order.Lines.Count })
            : ProblemResponse(report);
    }

    private async Task<HttpResponseMessage> HandleRegistrationAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var registration = await request.Content!.ReadFromJsonAsync<EventRegistration>(JsonOptions, cancellationToken)
            ?? new EventRegistration();

        registration.Normalize();
        var report = await _registrationValidator.ValidateAsync(registration, ValidationProfile.Submit, cancellationToken);

        if (!report.IsValid)
        {
            return ProblemResponse(report);
        }

        // The coupon list only the real server knows - mirrors Formidable.Sample.Api's
        // Program.cs registrations handler exactly, including its narrower wire shape: this
        // rejection is a standalone problem carrying only the coupon issue, built the same way
        // the real handler builds it (Results.ValidationProblem with no extensions), not routed
        // through the group filter's advisories extension above.
        var coupon = registration.CouponCode;
        var recognised = string.Equals(coupon, "WELCOME10", StringComparison.OrdinalIgnoreCase)
            || string.Equals(coupon, "SPEAKER", StringComparison.OrdinalIgnoreCase);

        if (!string.IsNullOrEmpty(coupon) && !recognised)
        {
            return ProblemResponse(new Dictionary<string, string[]>
            {
                ["CouponCode"] = ["Coupon code is not recognised"]
            });
        }

        return JsonResponse(HttpStatusCode.OK, new { accepted = true, attendees = registration.Attendees.Count });
    }

    // Mirrors ValidationReportProblemMapper (Formidable.AspNetCore, not referenceable from a
    // WASM client): errors keyed by path in issue order, non-error issues riding an "advisories"
    // extension that appears only when at least one exists.
    private static HttpResponseMessage ProblemResponse(ValidationReport report)
    {
        var errors = report.Errors
            .GroupBy(issue => issue.Path)
            .ToDictionary(group => group.Key, group => group.Select(issue => issue.Message).ToArray());

        var advisories = report.Issues
            .Where(issue => issue.Severity != ValidationSeverity.Error)
            .Select(issue => new
            {
                path = issue.Path,
                message = issue.Message,
                severity = issue.Severity.ToString(),
                code = issue.Code,
                displayName = issue.DisplayName
            })
            .ToList();

        return ProblemResponse(errors, advisories.Count > 0 ? advisories : null);
    }

    private static HttpResponseMessage ProblemResponse(Dictionary<string, string[]> errors) =>
        ProblemResponse(errors, advisories: null);

    private static HttpResponseMessage ProblemResponse(Dictionary<string, string[]> errors, object? advisories)
    {
        // Two differently-shaped anonymous types (not a ternary): the "advisories" key must be
        // absent entirely when there are none, matching TypedResults.ValidationProblem, which
        // never writes an empty/null extension.
        if (advisories is null)
        {
            return JsonResponse(
                HttpStatusCode.BadRequest,
                new { type = ProblemType, title = ProblemTitle, status = 400, errors },
                "application/problem+json");
        }

        return JsonResponse(
            HttpStatusCode.BadRequest,
            new { type = ProblemType, title = ProblemTitle, status = 400, errors, advisories },
            "application/problem+json");
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, object body, string mediaType = "application/json")
    {
        var content = JsonContent.Create(body, new MediaTypeHeaderValue(mediaType), JsonOptions);
        return new HttpResponseMessage(status) { Content = content };
    }
}
#endif
