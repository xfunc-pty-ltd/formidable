using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Formidable;
using Formidable.AspNetCore.Tests.Fixtures;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Formidable.AspNetCore.Tests;

[ApiController]
[Route("mvc")]
public sealed class OrdersController : ControllerBase
{
    [HttpPost("orders")]
    [Validate]
    public IActionResult Submit([FromBody] SampleOrder order) => Ok(order);

    [HttpPost("drafts")]
    [Validate(Profile = "Draft")]
    public IActionResult Draft([FromBody] SampleOrder order) => Ok(order);

    [HttpPost("drafts-lowercase")]
    [Validate(Profile = "draft")]
    public IActionResult DraftLowercase([FromBody] SampleOrder order) => Ok(order);

    [HttpPost("explicit")]
    [Validate(typeof(SampleOrder))]
    public IActionResult Explicit([FromBody] SampleOrder order) => Ok(order);

    [HttpPost("explicit-custom")]
    [Validate(typeof(ExplicitModel))]
    public IActionResult ExplicitCustom([FromBody] ExplicitModel model) => Ok(model);
}

/// <summary>Model validated by a consumer-registered <see cref="IModelValidator{TModel}"/>
/// whose members are explicit interface implementations — pins the reflection lookup fix that
/// resolves ValidateAsync from the interface type rather than the concrete validator type.</summary>
public class ExplicitModel
{
    public string Name { get; set; } = string.Empty;
}

/// <summary>Deliberately implements every member explicitly so `validator.GetType().GetMethod(...)`
/// (which only finds implicitly implemented interface members) would fail to locate it.</summary>
public sealed class ExplicitModelValidator : IModelValidator<ExplicitModel>
{
    Task<ValidationReport> IModelValidator<ExplicitModel>.ValidateAsync(
        ExplicitModel model, ValidationProfile profile, CancellationToken cancellationToken) =>
        Task.FromResult(new ValidationReport([new ValidationIssue(nameof(ExplicitModel.Name), "Custom error")]));

    ValidationReport IModelValidator<ExplicitModel>.Validate(ExplicitModel model, ValidationProfile profile) =>
        new([new ValidationIssue(nameof(ExplicitModel.Name), "Custom error")]);
}

public class ValidateAttributeTests
{
    private static Task<Microsoft.AspNetCore.Builder.WebApplication> StartMvcAppAsync() =>
        TestApp.StartAsync(
            app => app.MapControllers(),
            services =>
            {
                services.AddControllers().AddApplicationPart(typeof(OrdersController).Assembly);
                services.AddSingleton<IModelValidator<ExplicitModel>>(new ExplicitModelValidator());
            });

    [Fact]
    public async Task Invalid_submit_returns_400_with_parity_shape()
    {
        await using var app = await StartMvcAppAsync();
        var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync("mvc/orders",
            new SampleOrder { Description = "a-b", Items = [new SampleItem()] });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        var problem = JsonSerializer.Deserialize<FormidableValidationProblem>(body, JsonSerializerOptions.Web);
        Assert.Contains("Sku required", problem!.Errors["Items[0].Sku"]);
        Assert.Contains(problem.Warnings, w => w.Path == "Description" && w.Severity == "Warning");

        using var document = JsonDocument.Parse(body);
        Assert.NotNull(document.RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public async Task Draft_profile_string_maps_to_the_draft_profile()
    {
        await using var app = await StartMvcAppAsync();
        var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync("mvc/drafts", new SampleOrder { Description = "" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Profile_name_is_case_insensitive_for_the_built_in_profiles()
    {
        await using var app = await StartMvcAppAsync();
        var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync("mvc/drafts-lowercase", new SampleOrder { Description = "" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Explicit_model_type_mode_validates_that_type()
    {
        await using var app = await StartMvcAppAsync();
        var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync("mvc/explicit", new SampleOrder { Description = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Normalize_runs_for_mvc_arguments_too()
    {
        await using var app = await StartMvcAppAsync();
        var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync("mvc/orders",
            new SampleOrder { Description = "ok", Items = [new SampleItem { Sku = " " }, new SampleItem { Sku = "A" }] });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var echoed = await response.Content.ReadFromJsonAsync<SampleOrder>();
        Assert.Single(echoed!.Items);
    }

    [Fact]
    public async Task Valid_submit_passes_through()
    {
        await using var app = await StartMvcAppAsync();
        var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync("mvc/orders",
            new SampleOrder { Description = "ok", Items = [new SampleItem { Sku = "A" }] });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Explicit_interface_implementation_validators_are_dispatched_correctly()
    {
        await using var app = await StartMvcAppAsync();
        var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync("mvc/explicit-custom", new ExplicitModel { Name = "x" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<FormidableValidationProblem>();
        Assert.Contains("Custom error", problem!.Errors["Name"]);
    }

    /// <summary>
    /// Bypasses the shared TestApp fixture (which always calls AddFormidable) to reproduce the
    /// discovery-mode misconfiguration directly: an IValidator&lt;T&gt; is registered but the
    /// IModelValidator&lt;T&gt; adapter is not, because the consumer forgot AddFormidable().
    /// </summary>
    [Fact]
    public async Task Missing_AddFormidable_registration_produces_a_diagnostic_message()
    {
        var services = new ServiceCollection();
        services.AddSingleton<FluentValidation.IValidator<SampleOrder>>(new SampleOrderValidator());
        var httpContext = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        var actionExecutingContext = new ActionExecutingContext(
            actionContext,
            new List<IFilterMetadata>(),
            new Dictionary<string, object?> { ["order"] = new SampleOrder() },
            controller: new object());

        var attribute = new ValidateAttribute();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            attribute.OnActionExecutionAsync(
                actionExecutingContext,
                () => Task.FromResult<ActionExecutedContext>(null!)));

        Assert.Contains("IModelValidator<SampleOrder>", exception.Message);
        Assert.Contains("services.AddFormidable()", exception.Message);
        Assert.NotNull(exception.InnerException);
    }
}
