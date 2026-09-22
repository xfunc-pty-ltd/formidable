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
    public async Task Null_body_for_a_declared_nullable_parameter_returns_400_not_500()
    {
        await using var app = await TestApp.StartAsync(a =>
            a.MapPost("/optional", (SampleOrder? order) => Results.Ok(order)).Validate<SampleOrder>());
        var client = app.GetTestClient();

        // A declared SampleOrder? parameter bound to the JSON literal `null` is something any
        // anonymous client can trigger by posting exactly this body — it must not throw.
        var response = await client.PostAsync("/optional",
            new StringContent("null", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<FormidableValidationProblem>();
        Assert.Contains("A request body is required.", problem!.Errors[string.Empty]);
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
    public async Task A_null_bound_model_400_stashes_no_report_because_no_validator_ran()
    {
        ValidationReport? seenAfterPipeline = new ValidationReport([]); // sentinel — must be overwritten
        await using var app = await TestApp.StartAsync(a =>
        {
            a.Use(async (context, next) =>
            {
                await next(context);
                seenAfterPipeline = context.GetFormidableValidationReport();
            });
            a.MapPost("/optional", (SampleOrder? order) => Results.Ok(order)).Validate<SampleOrder>();
        });
        var client = app.GetTestClient();

        // The filter answers a null-bound model with its 400 before any validator can run, so
        // there is no computed report for the accessor to serve — null, not an empty report.
        var response = await client.PostAsync("/optional",
            new StringContent("null", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(seenAfterPipeline);
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
        // fail endpoint building, taking every route in the app down with it, instead of leaving
        // the endpoint serving the 400 a client can trigger by posting a null body.
        var response = await client.PostAsync("/derived-group/orders",
            new StringContent("null", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<FormidableValidationProblem>();
        Assert.Contains("A request body is required.", problem!.Errors[string.Empty]);
    }
}
