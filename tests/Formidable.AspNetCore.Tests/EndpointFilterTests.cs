using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentValidation;
using Formidable;
using Formidable.AspNetCore.Tests.Fixtures;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Formidable.AspNetCore.Tests;

public class EndpointFilterTests
{
    private static Task<Microsoft.AspNetCore.Builder.WebApplication> StartDefaultAppAsync() =>
        TestApp.StartAsync(app =>
        {
            app.MapPost("/orders", (SampleOrder order) => Results.Ok(order))
                .Validate<SampleOrder>();
            app.MapPost("/drafts", (SampleOrder order) => Results.Ok(order))
                .Validate<SampleOrder>(ValidationProfile.Draft);
        });

    [Fact]
    public async Task Invalid_submit_returns_400_with_paths_in_client_format()
    {
        await using var app = await StartDefaultAppAsync();
        var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync("/orders",
            new SampleOrder { Description = "", Items = [new SampleItem()] });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<FormidableValidationProblem>();
        Assert.Contains("Required", problem!.Errors["Description"]);
        Assert.Contains("Sku required", problem.Errors["Items[0].Sku"]);
    }

    [Fact]
    public async Task Draft_profile_is_lenient_where_submit_is_strict()
    {
        await using var app = await StartDefaultAppAsync();
        var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync("/drafts", new SampleOrder { Description = "" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Normalize_runs_before_validation_and_the_handler()
    {
        await using var app = await StartDefaultAppAsync();
        var client = app.GetTestClient();

        // The blank row would fail "Sku required" — Normalize strips it first, and the echoed
        // model proves the handler saw the normalized instance.
        var response = await client.PostAsJsonAsync("/orders",
            new SampleOrder { Description = "ok", Items = [new SampleItem { Sku = "  " }, new SampleItem { Sku = "A" }] });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var echoed = await response.Content.ReadFromJsonAsync<SampleOrder>();
        Assert.Single(echoed!.Items);
        Assert.Equal("A", echoed.Items[0].Sku);
    }

    [Fact]
    public async Task Advisories_ride_the_extension_and_round_trip_to_issues()
    {
        await using var app = await StartDefaultAppAsync();
        var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync("/orders",
            new SampleOrder { Description = "a-b", Items = [new SampleItem()] }); // error (Sku) + warning (hyphen)

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<FormidableValidationProblem>();
        var issues = problem!.ToIssues();
        Assert.Contains(issues, i => i.Path == "Items[0].Sku" && i.Severity == ValidationSeverity.Error);
        Assert.Contains(issues, i => i.Path == "Description" && i.Severity == ValidationSeverity.Warning && i.Message == "Avoid hyphens");
    }

    [Fact]
    public async Task Errors_without_advisories_omit_the_extension_key_entirely()
    {
        await using var app = await StartDefaultAppAsync();
        var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync("/orders",
            new SampleOrder { Description = "", Items = [new SampleItem { Sku = "A" }] }); // error only, no advisories

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        Assert.False(document.RootElement.TryGetProperty("advisories", out _));
    }

    [Fact]
    public async Task Warnings_alone_do_not_block()
    {
        await using var app = await StartDefaultAppAsync();
        var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync("/orders",
            new SampleOrder { Description = "a-b", Items = [new SampleItem { Sku = "A" }] });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // Builds an app with everything correctly registered but never starts it and never sends a
    // request, so InvokeAsync can never run — endpoint building is the only moment the
    // validation wiring executes in the missing-argument tests below.
    private static WebApplication BuildUnstartedApp()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddFormidable();
        builder.Services.AddScoped<IValidator<SampleOrder>, SampleOrderValidator>();
        return builder.Build();
    }

    // Materializing the route endpoints is what routing itself does before it can match any
    // request; building each endpoint's request pipeline runs its endpoint filter factories.
    private static Endpoint[] MaterializeEndpoints(WebApplication app) =>
        [.. ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)];

    [Fact]
    public async Task Missing_model_argument_throws_at_endpoint_building_not_at_a_request()
    {
        await using var app = BuildUnstartedApp();
        app.MapGet("/nothing", () => Results.Ok()).Validate<SampleOrder>();

        // The server is never started and no request is ever made — the wiring bug surfaces
        // from building the endpoint alone.
        var exception = Assert.Throws<InvalidOperationException>(() => MaterializeEndpoints(app));
        Assert.Contains("Validate<SampleOrder>() found no endpoint argument", exception.Message);
    }

    [Fact]
    public async Task Group_level_validate_filters_every_endpoint_in_the_group()
    {
        await using var app = await TestApp.StartAsync(a =>
        {
            var group = a.MapGroup("/grouped").Validate<SampleOrder>();
            group.MapPost("/orders", (SampleOrder order) => Results.Ok(order));
        });
        var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync("/grouped/orders",
            new SampleOrder { Description = "", Items = [new SampleItem()] });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<FormidableValidationProblem>();
        Assert.Contains("Required", problem!.Errors["Description"]);
    }

    [Fact]
    public async Task A_nullable_parameter_the_platform_admits_still_reaches_the_handler()
    {
        await using var app = await TestApp.StartAsync(a =>
            a.MapPost("/optional", (SampleOrder? order) => Results.Ok(new { bodyWasNull = order is null }))
                .Validate<SampleOrder>());
        var client = app.GetTestClient();

        // Declaring the parameter nullable IS the consumer saying a missing body is acceptable,
        // and the platform honours it by running the handler with null. The filter has nothing to
        // validate and nothing to decide, so it passes the request on: second-guessing the
        // declaration here would override the one lever minimal APIs leave a consumer, since they
        // silently discard EmptyBodyBehavior.Disallow.
        var response = await client.PostAsync("/optional",
            new StringContent("null", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"bodyWasNull\":true", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_non_nullable_parameter_the_platform_refuses_gets_the_validation_shape()
    {
        await using var app = await TestApp.StartAsync(a =>
            a.MapPost("/required", (SampleOrder order) => Results.Ok(order)).Validate<SampleOrder>());
        var client = app.GetTestClient();

        // The platform refuses a null body for a non-nullable parameter itself, before the
        // handler, and writes a bare bodiless 400 for it — cause-blind even with ProblemDetails
        // configured. The filter keeps that decision and replaces only the empty body, so a
        // client gets the same errors dictionary every other rejection on this page uses.
        var response = await client.PostAsync("/required",
            new StringContent("null", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<FormidableValidationProblem>();
        Assert.Contains("A request body is required.", problem!.Errors[string.Empty]);
    }

    [Fact]
    public async Task A_downstream_filters_own_400_is_passed_on_rather_than_enriched()
    {
        await using var app = await TestApp.StartAsync(a =>
            a.MapPost("/downstream", (SampleOrder? order) => Results.Ok(order))
                .Validate<SampleOrder>()
                .AddEndpointFilter((invocation, next) =>
                    ValueTask.FromResult<object?>(Results.BadRequest(new { mine = true }))));
        var client = app.GetTestClient();

        // The discriminator the enrichment gate owes. Validate<TModel>() is added first, so it is
        // OUTERMOST and its next() reaches the filter below — which rejects the request itself,
        // with the model bound null, which is every precondition but the one that matters. The
        // gate must not claim that 400: it is not the platform refusing a declaration.
        var response = await client.PostAsync("/downstream",
            new StringContent("null", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"mine\":true", body);
        Assert.DoesNotContain("A request body is required.", body);
    }

    [Fact]
    public async Task A_downstream_empty_result_that_is_not_a_400_is_passed_on_rather_than_enriched()
    {
        await using var app = await TestApp.StartAsync(a =>
            a.MapPost("/downstream-empty-200", (SampleOrder? order) => Results.Ok(order))
                .Validate<SampleOrder>()
                .AddEndpointFilter((invocation, next) => ValueTask.FromResult<object?>(Results.Empty)));
        var client = app.GetTestClient();

        // Isolates the STATUS half of the gate. The result IS empty and nothing has been written,
        // so every other condition is met — only "a 400 stands on the response" separates this
        // from the platform refusing a declaration, and turning a downstream 200 into a 400 would
        // be the filter inventing a rejection nobody made.
        var response = await client.PostAsync("/downstream-empty-200",
            new StringContent("null", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("A request body is required.", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_downstream_400_written_straight_onto_the_response_is_passed_on_rather_than_enriched()
    {
        await using var app = await TestApp.StartAsync(a =>
            a.MapPost("/downstream-direct-400", (SampleOrder? order) => Results.Ok(order))
                .Validate<SampleOrder>()
                .AddEndpointFilter((invocation, next) =>
                {
                    invocation.HttpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return ValueTask.FromResult<object?>(Results.Text("mine"));
                }));
        var client = app.GetTestClient();

        // Isolates the EMPTY-RESULT half of the gate. Here the 400 really is on the response
        // before the check runs, so the status alone cannot tell this from the platform's own
        // refusal — what does is that a result was produced to write, which is never true of a
        // request the platform declined to bind.
        var response = await client.PostAsync("/downstream-direct-400",
            new StringContent("null", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal("mine", body);
    }

    [Fact]
    public async Task A_response_already_on_the_wire_is_left_alone()
    {
        await using var app = await TestApp.StartAsync(a =>
            a.MapPost("/downstream-started", (SampleOrder? order) => Results.Ok(order))
                .Validate<SampleOrder>()
                .AddEndpointFilter(async (invocation, next) =>
                {
                    invocation.HttpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await invocation.HttpContext.Response.WriteAsync("already sent");
                    return Results.Empty;
                }));
        var client = app.GetTestClient();

        // Isolates the HasStarted half. Empty result, 400 on the response: the two other
        // conditions both hold, and the only thing left is that the bytes have gone. Replacing a
        // response that is already on the wire cannot be done, so the check is what keeps this a
        // pass-through instead of a throw on the way out.
        var response = await client.PostAsync("/downstream-started",
            new StringContent("null", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("already sent", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Grouped_endpoint_missing_the_argument_fails_endpoint_building_despite_a_valid_sibling()
    {
        await using var app = BuildUnstartedApp();
        var group = app.MapGroup("/mixed-group").Validate<SampleOrder>();
        group.MapPost("/orders", (SampleOrder order) => Results.Ok(order));
        group.MapGet("/health", () => Results.Ok());

        // /health declares no SampleOrder parameter at all -- a wiring bug, not something a
        // client's request shape can influence. The check runs per endpoint (the sibling with
        // the argument is fine); the server is never started and no request is ever made -- the
        // wiring bug surfaces from building the group's endpoints alone.
        var exception = Assert.Throws<InvalidOperationException>(() => MaterializeEndpoints(app));
        Assert.Contains("Validate<SampleOrder>() found no endpoint argument", exception.Message);
    }

    /// <summary>Hands back a report it built itself and records the instance, so a test can
    /// prove the accessor serves the very report the filter computed rather than a re-run's
    /// copy. The report is valid WITH a warning — the shape the 400 path never carries.</summary>
    private sealed class RecordingModelValidator : IModelValidator<SampleOrder>
    {
        public ValidationReport? LastReport { get; private set; }

        public Task<ValidationReport> ValidateAsync(
            SampleOrder model, ValidationProfile profile, CancellationToken cancellationToken = default)
        {
            LastReport = new ValidationReport(
                [new ValidationIssue(nameof(SampleOrder.Description), "Avoid hyphens", ValidationSeverity.Warning)]);
            return Task.FromResult(LastReport);
        }

        public ValidationReport Validate(SampleOrder model, ValidationProfile profile) =>
            new([new ValidationIssue(nameof(SampleOrder.Description), "Avoid hyphens", ValidationSeverity.Warning)]);
    }

    [Fact]
    public async Task Success_path_handler_reads_the_same_report_instance_the_filter_computed()
    {
        var recorder = new RecordingModelValidator();
        ValidationReport? seenByHandler = null;
        await using var app = await TestApp.StartAsync(
            a => a.MapPost("/accepted", (SampleOrder order, HttpContext http) =>
                {
                    seenByHandler = http.GetFormidableValidationReport();
                    return Results.Ok();
                }).Validate<SampleOrder>(),
            // Registered AFTER AddFormidable's open-generic adapter, so this closed registration
            // wins resolution and the filter validates through the recorder.
            services => services.AddSingleton<IModelValidator<SampleOrder>>(recorder));
        var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync("/accepted", new SampleOrder { Description = "ok" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(recorder.LastReport); // positive control: the recorder is what validated
        Assert.NotNull(seenByHandler);
        Assert.Same(recorder.LastReport, seenByHandler);
        // The report is valid WITH a warning: an advisory the wire has no success shape for is
        // reachable in the handler, which is the accessor's whole reason to exist.
        Assert.True(seenByHandler!.IsValid);
        Assert.Contains(seenByHandler.Advisories, i => i.Severity == ValidationSeverity.Warning);
    }

    [Fact]
    public async Task A_rejected_request_still_carries_the_report_for_middleware_to_read()
    {
        ValidationReport? seenAfterPipeline = null;
        await using var app = await TestApp.StartAsync(a =>
        {
            a.Use(async (context, next) =>
            {
                await next(context);
                seenAfterPipeline = context.GetFormidableValidationReport();
            });
            a.MapPost("/orders", (SampleOrder order) => Results.Ok(order)).Validate<SampleOrder>();
        });
        var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync("/orders",
            new SampleOrder { Description = "", Items = [new SampleItem()] });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        // The handler never ran, but the stash happened before the 400 decision — middleware
        // reads the rejection's full severity detail without parsing the response body.
        Assert.NotNull(seenAfterPipeline);
        Assert.False(seenAfterPipeline!.IsValid);
    }

    [Fact]
    public async Task Accessor_is_null_on_a_request_no_formidable_filter_validated()
    {
        var handlerRan = false;
        ValidationReport? seenByHandler = new ValidationReport([]); // sentinel — must be overwritten
        await using var app = await TestApp.StartAsync(a =>
            a.MapPost("/unfiltered", (SampleOrder order, HttpContext http) =>
            {
                handlerRan = true;
                seenByHandler = http.GetFormidableValidationReport();
                return Results.Ok();
            }));
        var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync("/unfiltered", new SampleOrder { Description = "ok" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(handlerRan);
        Assert.Null(seenByHandler);
    }

    [Fact]
    public async Task A_null_bound_model_stashes_no_report_because_no_validator_ran()
    {
        // A null-bound model means there was nothing to validate, so there is no computed report
        // for the accessor to serve — null, not an empty report — and that holds on both of the
        // outcomes the platform can choose. It is also the ONE case where a handler sitting
        // behind Validate<TModel>() sees null from the accessor: its own parameter bound null and
        // the platform let it through.
        ValidationReport? seenAfterPassThrough = new ValidationReport([]); // sentinel — must be overwritten
        await using var passThrough = await TestApp.StartAsync(a =>
        {
            a.Use(async (context, next) =>
            {
                await next(context);
                seenAfterPassThrough = context.GetFormidableValidationReport();
            });
            a.MapPost("/optional", (SampleOrder? order) => Results.Ok(order)).Validate<SampleOrder>();
        });

        var passed = await passThrough.GetTestClient().PostAsync("/optional",
            new StringContent("null", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, passed.StatusCode);
        Assert.Null(seenAfterPassThrough);

        ValidationReport? seenAfterRefusal = new ValidationReport([]); // sentinel — must be overwritten
        await using var refused = await TestApp.StartAsync(a =>
        {
            a.Use(async (context, next) =>
            {
                await next(context);
                seenAfterRefusal = context.GetFormidableValidationReport();
            });
            a.MapPost("/required", (SampleOrder order) => Results.Ok(order)).Validate<SampleOrder>();
        });

        var rejected = await refused.GetTestClient().PostAsync("/required",
            new StringContent("null", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Null(seenAfterRefusal);
    }

    [Fact]
    public async Task Grouped_endpoint_with_a_derived_declared_parameter_type_still_returns_400_not_500_on_null()
    {
        await using var app = await TestApp.StartAsync(a =>
        {
            var group = a.MapGroup("/derived-group").Validate<PolymorphicSampleOrder>();
            group.MapPost("/orders", (RushPolymorphicSampleOrder? order) => Results.Ok(order));
        });
        var client = app.GetTestClient();

        // The handler declares a MORE DERIVED parameter type (RushPolymorphicSampleOrder) than
        // the group's validated TModel (PolymorphicSampleOrder). ThrowIfNoDeclaredParameter must
        // recognize this as the same parameter InvokeAsync's own OfType<TModel> retrieval would
        // match -- an exact-type check would misreport it as "no parameter of this type" and
        // fail endpoint building, taking every route in the app down with it. The endpoint
        // answering at all is what says the check accepted it; the parameter is declared
        // nullable, so the platform runs the handler with null and the filter passes that on.
        var response = await client.PostAsync("/derived-group/orders",
            new StringContent("null", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
