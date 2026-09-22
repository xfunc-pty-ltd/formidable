using System.Net;
using System.Net.Http.Json;
using System.Text;
using Formidable;
using Formidable.AspNetCore.Tests.Fixtures;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;

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
    public async Task Warnings_alone_do_not_block()
    {
        await using var app = await StartDefaultAppAsync();
        var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync("/orders",
            new SampleOrder { Description = "a-b", Items = [new SampleItem { Sku = "A" }] });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Missing_model_argument_is_a_configuration_error()
    {
        await using var app = await TestApp.StartAsync(a =>
            a.MapGet("/nothing", () => Results.Ok()).Validate<SampleOrder>());
        var client = app.GetTestClient();

        var response = await client.GetAsync("/nothing");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
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
    public async Task Grouped_endpoint_genuinely_missing_the_argument_still_throws()
    {
        await using var app = await TestApp.StartAsync(a =>
        {
            var group = a.MapGroup("/mixed-group").Validate<SampleOrder>();
            group.MapPost("/orders", (SampleOrder order) => Results.Ok(order));
            group.MapGet("/health", () => Results.Ok());
        });
        var client = app.GetTestClient();

        // /health declares no SampleOrder parameter at all -- a wiring bug, not something a
        // client's request shape can influence, so it stays a thrown 500 even though a sibling
        // endpoint in the very same group has the argument.
        var response = await client.GetAsync("/mixed-group/health");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
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
        // the group's validated TModel (PolymorphicSampleOrder). HasDeclaredParameter must
        // recognize this as the same parameter InvokeAsync's own OfType<TModel> retrieval would
        // match -- an exact-type check would misreport it as "no parameter of this type" and
        // throw 500 instead of the 400 a client can trigger by posting a null body.
        var response = await client.PostAsync("/derived-group/orders",
            new StringContent("null", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<FormidableValidationProblem>();
        Assert.Contains("A request body is required.", problem!.Errors[string.Empty]);
    }
}
