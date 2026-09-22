using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentValidation;
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

    [HttpPost("polymorphic")]
    [Validate]
    public IActionResult Polymorphic([FromBody] PolymorphicSampleOrder order) => Ok(order);

    [HttpPost("polymorphic-derived-only")]
    [Validate]
    public IActionResult PolymorphicDerivedOnly([FromBody] OnlyDerivedPolymorphicOrder order) => Ok(order);

    [HttpPost("strict-orders")]
    [Validate(RequireValidator = true)]
    public IActionResult StrictOrders([FromBody] SampleOrder order) => Ok(order);

    [HttpPost("strict-unregistered")]
    [Validate(RequireValidator = true)]
    public IActionResult StrictUnregistered([FromBody] UnregisteredModel model) => Ok(model);

    [HttpPost("strict-optional")]
    [Validate(RequireValidator = true)]
    public IActionResult StrictOptional([FromBody] SampleOrder? order) => Ok(order);

    [HttpPost("explicit-unregistered")]
    [Validate(typeof(UnregisteredModel))]
    public IActionResult ExplicitUnregistered([FromBody] UnregisteredModel model) => Ok(model);

    [HttpPost("many-messages")]
    [Validate(typeof(ManyMessageModel))]
    public IActionResult ManyMessages([FromBody] ManyMessageModel model) => Ok(model);

    [HttpPost("empty-message")]
    [Validate(typeof(EmptyMessageModel))]
    public IActionResult EmptyMessage([FromBody] EmptyMessageModel model) => Ok(model);
}

/// <summary>Validated by a validator reporting an error with no message — the shape the wire
/// mapper's null-message tolerance produces, and the one the two adapters carry differently.</summary>
public class EmptyMessageModel
{
    public string Field { get; set; } = string.Empty;
}

public sealed class EmptyMessageValidator : IModelValidator<EmptyMessageModel>
{
    public Task<ValidationReport> ValidateAsync(
        EmptyMessageModel model, ValidationProfile profile, CancellationToken cancellationToken = default) =>
        Task.FromResult(Validate(model, profile));

    public ValidationReport Validate(EmptyMessageModel model, ValidationProfile profile) =>
        new([new ValidationIssue("Field", null!)]);
}

/// <summary>Validated by <see cref="ManyMessageValidator"/>, which reports the shapes the
/// wire mapping has to carry across both server adapters: two messages under one path, two
/// paths whose report order is not their sorted order, and an issue with no message at
/// all.</summary>
public class ManyMessageModel
{
    public string Zebra { get; set; } = string.Empty;

    public string Apple { get; set; } = string.Empty;
}

public sealed class ManyMessageValidator : IModelValidator<ManyMessageModel>
{
    public Task<ValidationReport> ValidateAsync(
        ManyMessageModel model, ValidationProfile profile, CancellationToken cancellationToken = default) =>
        Task.FromResult(Validate(model, profile));

    public ValidationReport Validate(ManyMessageModel model, ValidationProfile profile) => new([
        new ValidationIssue("Zebra", "z first"),
        new ValidationIssue("Apple", "a first"),
        new ValidationIssue("Apple", "a second")
    ]);
}

/// <summary>Base of a polymorphic pair pinning the declared-type validator-resolution fix: the
/// <c>[FromBody]</c> parameter above is declared as this base type, but System.Text.Json's
/// <c>$type</c> discriminator can bind a derived <see cref="RushPolymorphicSampleOrder"/>
/// instance to it.</summary>
[JsonPolymorphic]
[JsonDerivedType(typeof(RushPolymorphicSampleOrder), "rush")]
public class PolymorphicSampleOrder
{
    public string Description { get; set; } = string.Empty;
}

/// <summary>Deliberately has no <see cref="IModelValidator{TModel}"/> registered anywhere in
/// this file's test apps — only the base type's validator should ever run.</summary>
public sealed class RushPolymorphicSampleOrder : PolymorphicSampleOrder
{
    public bool Rush { get; set; }
}

public class PolymorphicSampleOrderValidator : DraftSubmitValidator<PolymorphicSampleOrder>
{
    protected override void ConfigureDraftRules()
    {
    }

    protected override void ConfigureSubmitRules() =>
        RuleFor(order => order.Description).NotEmpty().WithMessage("Required");
}

/// <summary>Base of a second polymorphic pair pinning the runtime-type FALLBACK: unlike
/// <see cref="PolymorphicSampleOrder"/>, this base type has NO registered validator anywhere in
/// this file's test apps — only its derived <see cref="RushOnlyPolymorphicOrder"/> does.</summary>
[JsonPolymorphic]
[JsonDerivedType(typeof(RushOnlyPolymorphicOrder), "rush")]
public class OnlyDerivedPolymorphicOrder
{
    public string Description { get; set; } = string.Empty;
}

/// <summary>The only type in this pair with a registered validator — resolving strictly by the
/// declared (base) type would find nothing and silently skip the argument; the fallback probe
/// of the argument's runtime type is what makes validation still run.</summary>
public sealed class RushOnlyPolymorphicOrder : OnlyDerivedPolymorphicOrder
{
    public bool Rush { get; set; }
}

public class RushOnlyPolymorphicOrderValidator : DraftSubmitValidator<RushOnlyPolymorphicOrder>
{
    protected override void ConfigureDraftRules()
    {
    }

    protected override void ConfigureSubmitRules() =>
        RuleFor(order => order.Description).NotEmpty().WithMessage("Required");
}

/// <summary>Never has a validator registered anywhere in this file's test apps — pins the
/// <c>RequireValidator</c> strict-mode throw.</summary>
public class UnregisteredModel
{
    public string Name { get; set; } = string.Empty;
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

/// <summary>Capture slots for the report-accessor tests: the validator records the report it
/// produced, the action records what the accessor served it, and the two are compared without
/// anything crossing the wire.</summary>
public sealed class ReportCapture
{
    public ValidationReport? Computed { get; set; }
    public ValidationReport? SeenByAction { get; set; }
    public bool ActionRan { get; set; }
}

/// <summary>Hands back a report it built itself — valid WITH a warning — and records the
/// instance, so a test can prove the stashed aggregate carries the very issue instances the
/// validator produced rather than a re-run's copies.</summary>
public sealed class RecordingSampleOrderValidator(ReportCapture capture) : IModelValidator<SampleOrder>
{
    public Task<ValidationReport> ValidateAsync(
        SampleOrder model, ValidationProfile profile, CancellationToken cancellationToken = default)
    {
        capture.Computed = new ValidationReport(
            [new ValidationIssue(nameof(SampleOrder.Description), "Avoid hyphens", ValidationSeverity.Warning)]);
        return Task.FromResult(capture.Computed);
    }

    public ValidationReport Validate(SampleOrder model, ValidationProfile profile) =>
        new([new ValidationIssue(nameof(SampleOrder.Description), "Avoid hyphens", ValidationSeverity.Warning)]);
}

[ApiController]
[Route("mvc3")]
public sealed class ReportAccessorController : ControllerBase
{
    [HttpPost("accepted")]
    [Validate]
    public IActionResult Accepted([FromBody] SampleOrder order)
    {
        var capture = HttpContext.RequestServices.GetRequiredService<ReportCapture>();
        capture.ActionRan = true;
        capture.SeenByAction = HttpContext.GetFormidableValidationReport();
        return Ok(order);
    }

    [HttpPost("unvalidated")]
    [Validate]
    public IActionResult Unvalidated([FromBody] UnregisteredModel model)
    {
        var capture = HttpContext.RequestServices.GetRequiredService<ReportCapture>();
        capture.ActionRan = true;
        capture.SeenByAction = HttpContext.GetFormidableValidationReport();
        return Ok(model);
    }
}

[ApiController]
[Route("mvc2")]
[Validate]
public sealed class ScopedController : ControllerBase
{
    [HttpPost("class-level")]
    public IActionResult ClassLevel([FromBody] SampleOrder order) => Ok(order);

    [HttpPost("multi")]
    public IActionResult Multi([FromBody] SampleOrder order, [FromQuery] SampleFilter filter) =>
        Ok(new { order.Description, filter.Region });
}

/// <summary>
/// No class-level <see cref="ValidateAttribute"/>, deliberately: <see cref="EscalatedNoteValidator"/>
/// registers only its own "Escalated" ruleset (no "Submit" shape at all), so stacking a
/// class-level catch-all — which discovers and validates under its own default "Submit"
/// profile — alongside this action's explicit <c>Profile = "Escalated"</c> would validate the
/// same argument against a ruleset this validator never declared.
/// </summary>
[ApiController]
[Route("mvc2")]
public sealed class EscalatedController : ControllerBase
{
    [HttpPost("escalated")]
    [Validate(typeof(EscalatedNote), Profile = "Escalated")]
    public IActionResult Escalated([FromBody] EscalatedNote note) => Ok(note);
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
                services.AddSingleton<IModelValidator<ManyMessageModel>>(new ManyMessageValidator());
                services.AddSingleton<IModelValidator<EmptyMessageModel>>(new EmptyMessageValidator());
                services.AddScoped<FluentValidation.IValidator<PolymorphicSampleOrder>, PolymorphicSampleOrderValidator>();
                services.AddScoped<FluentValidation.IValidator<RushOnlyPolymorphicOrder>, RushOnlyPolymorphicOrderValidator>();
            });

    private static Task<Microsoft.AspNetCore.Builder.WebApplication> StartMvc2AppAsync() =>
        TestApp.StartAsync(
            app => app.MapControllers(),
            services =>
            {
                services.AddScoped<FluentValidation.IValidator<SampleFilter>, SampleFilterValidator>();
                services.AddScoped<FluentValidation.IValidator<EscalatedNote>, EscalatedNoteValidator>();
                services.AddControllers().AddApplicationPart(typeof(ScopedController).Assembly);
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
        Assert.Contains(problem.Advisories, w => w.Path == "Description" && w.Severity == "Warning");

        using var document = JsonDocument.Parse(body);
        Assert.NotNull(document.RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public async Task Errors_without_advisories_omit_the_extension_key_entirely()
    {
        await using var app = await StartMvcAppAsync();
        var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync("mvc/orders",
            new SampleOrder { Description = "", Items = [new SampleItem { Sku = "A" }] }); // error only, no advisories

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        Assert.False(document.RootElement.TryGetProperty("advisories", out _));
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

    [Fact]
    public async Task Class_level_validate_applies_to_actions()
    {
        await using var app = await StartMvc2AppAsync();
        var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync("mvc2/class-level", new SampleOrder { Description = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Multiple_validatable_arguments_aggregate_into_one_problem()
    {
        await using var app = await StartMvc2AppAsync();
        var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync("mvc2/multi?Region=",
            new SampleOrder { Description = "", Items = [new SampleItem { Sku = "A" }] });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<FormidableValidationProblem>();
        Assert.Contains("Required", problem!.Errors["Description"]);
        Assert.Contains("Region is required", problem.Errors["Region"]);
    }

    [Fact]
    public async Task Custom_profile_name_runs_the_same_named_ruleset()
    {
        await using var app = await StartMvc2AppAsync();
        var client = app.GetTestClient();

        var blocked = await client.PostAsJsonAsync("mvc2/escalated", new EscalatedNote { Reason = "" });
        var allowed = await client.PostAsJsonAsync("mvc2/escalated", new EscalatedNote { Reason = "fraud" });

        Assert.Equal(HttpStatusCode.BadRequest, blocked.StatusCode);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [Fact]
    public async Task Declared_parameter_type_drives_validator_selection_under_polymorphic_binding()
    {
        await using var app = await StartMvcAppAsync();
        var client = app.GetTestClient();

        // The $type discriminator binds a derived RushPolymorphicSampleOrder instance, but the
        // [FromBody] parameter's DECLARED type stays the base PolymorphicSampleOrder, which is
        // what has a registered validator. Resolving by argument.GetType() (the derived runtime
        // type) would find no IValidator<RushPolymorphicSampleOrder> and silently skip the
        // argument — this request would return 200 with an empty Description instead of the 400
        // asserted below.
        var response = await client.PostAsync("mvc/polymorphic",
            new StringContent("""{"$type":"rush","description":""}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<FormidableValidationProblem>();
        Assert.Contains("Required", problem!.Errors["Description"]);
    }

    [Fact]
    public async Task RequireValidator_does_not_interfere_when_a_validator_resolves()
    {
        await using var app = await StartMvcAppAsync();
        var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync("mvc/strict-orders",
            new SampleOrder { Description = "ok", Items = [new SampleItem { Sku = "A" }] });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task RequireValidator_throws_when_the_action_validates_nothing()
    {
        await using var app = await StartMvcAppAsync();
        var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync("mvc/strict-unregistered", new UnregisteredModel { Name = "x" });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains(nameof(UnregisteredModel), body);
        Assert.Contains("RequireValidator", body);
    }

    [Fact]
    public async Task RequireValidator_does_not_throw_on_a_client_caused_empty_body()
    {
        await using var app = await StartMvcAppAsync();
        var client = app.GetTestClient();

        // A null [FromBody] argument means the action bound ZERO non-null arguments -- something
        // any anonymous client can trigger by posting an empty/null body, not a misconfiguration
        // -- so RequireValidator's strict throw must not fire even though nothing was validated.
        // (The action's own Ok(null) becomes a 204 via ASP.NET Core's own null-body output
        // formatting -- the assertion checks for "not the strict throw's 500", not one exact
        // success code, so it stays true regardless of that unrelated framework behavior.)
        var response = await client.PostAsync("mvc/strict-optional",
            new StringContent("null", Encoding.UTF8, "application/json"));

        Assert.True(response.IsSuccessStatusCode, $"Expected a success status, got {response.StatusCode}");
    }

    private static Task<Microsoft.AspNetCore.Builder.WebApplication> StartReportAccessorAppAsync(ReportCapture capture) =>
        TestApp.StartAsync(
            app => app.MapControllers(),
            services =>
            {
                services.AddControllers().AddApplicationPart(typeof(ReportAccessorController).Assembly);
                services.AddSingleton(capture);
                // Registered AFTER AddFormidable's open-generic adapter, so this closed
                // registration wins resolution; discovery still probes IValidator<SampleOrder>,
                // which the shared TestApp registers.
                services.AddSingleton<IModelValidator<SampleOrder>>(new RecordingSampleOrderValidator(capture));
            });

    [Fact]
    public async Task Action_reads_the_aggregate_report_the_attribute_computed()
    {
        var capture = new ReportCapture();
        await using var app = await StartReportAccessorAppAsync(capture);
        var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync("mvc3/accepted", new SampleOrder { Description = "ok" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(capture.ActionRan);
        Assert.NotNull(capture.Computed); // positive control: the recorder is what validated
        Assert.NotNull(capture.SeenByAction);
        // The attribute aggregates per-argument reports into one; the aggregate's issues are the
        // very instances the validator produced, so reference identity here proves the stash IS
        // the computed verdict rather than a second validation's copy.
        var issue = Assert.Single(capture.SeenByAction!.Issues);
        Assert.Same(Assert.Single(capture.Computed!.Issues), issue);
        // Valid WITH a warning: the advisory is reachable inside the action on the 200 path.
        Assert.True(capture.SeenByAction.IsValid);
        Assert.Equal(ValidationSeverity.Warning, issue.Severity);
    }

    [Fact]
    public async Task A_rejected_action_still_carries_the_report_for_middleware_to_read()
    {
        ValidationReport? seenAfterPipeline = null;
        await using var app = await TestApp.StartAsync(
            a =>
            {
                a.Use(async (context, next) =>
                {
                    await next(context);
                    seenAfterPipeline = context.GetFormidableValidationReport();
                });
                a.MapControllers();
            },
            services => services.AddControllers().AddApplicationPart(typeof(OrdersController).Assembly));
        var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync("mvc/orders",
            new SampleOrder { Description = "", Items = [new SampleItem()] });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        // The action never ran, but the stash happened before the short-circuit — middleware
        // reads the rejection's full severity detail without parsing the response body.
        Assert.NotNull(seenAfterPipeline);
        Assert.False(seenAfterPipeline!.IsValid);
    }

    [Fact]
    public async Task Accessor_is_null_when_the_attribute_validated_nothing()
    {
        var capture = new ReportCapture { SeenByAction = new ValidationReport([]) }; // sentinel — must be overwritten
        await using var app = await StartReportAccessorAppAsync(capture);
        var client = app.GetTestClient();

        // UnregisteredModel resolves no validator, so [Validate] validates nothing — the
        // accessor must answer null, not an empty report implying rules ran and passed.
        var response = await client.PostAsJsonAsync("mvc3/unvalidated", new UnregisteredModel { Name = "x" });

        Assert.True(response.IsSuccessStatusCode, $"Expected a success status, got {response.StatusCode}");
        Assert.True(capture.ActionRan);
        Assert.Null(capture.SeenByAction);
    }

    [Fact]
    public async Task Runtime_type_fallback_validates_when_the_declared_type_has_no_validator()
    {
        await using var app = await StartMvcAppAsync();
        var client = app.GetTestClient();

        // OnlyDerivedPolymorphicOrder (the DECLARED parameter type) has no registered validator
        // at all; RushOnlyPolymorphicOrder (the $type-selected runtime type) does. Resolving by
        // the declared type alone would find nothing and silently skip the argument -- the
        // fallback probe of the runtime type is what makes this 400 instead of 200.
        var response = await client.PostAsync("mvc/polymorphic-derived-only",
            new StringContent("""{"$type":"rush","description":""}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<FormidableValidationProblem>();
        Assert.Contains("Required", problem!.Errors["Description"]);
    }

    [Fact]
    public async Task An_explicit_type_with_no_FluentValidation_validator_names_the_missing_registration()
    {
        // The explicit-types path never probes IValidator<T> -- naming the type is the whole
        // point of it -- so resolving the adapter for a type FluentValidation knows nothing
        // about fails during activation, and the message a consumer sees told them to call
        // AddFormidable(), which this app does call. What is actually missing is the validator.
        await using var app = await StartMvcAppAsync();
        var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync("mvc/explicit-unregistered", new UnregisteredModel());

        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains(nameof(UnregisteredModel), body);
        Assert.Contains("AddValidatorsFromAssembly", body);
        Assert.DoesNotContain("AddFormidable", body);
    }

    private static Task<Microsoft.AspNetCore.Builder.WebApplication> StartProblemDetailsAppAsync() =>
        TestApp.StartAsync(
            app => app.MapControllers(),
            services =>
            {
                services.AddControllers().AddApplicationPart(typeof(OrdersController).Assembly);
                services.AddSingleton<IModelValidator<ExplicitModel>>(new ExplicitModelValidator());
                services.AddSingleton<IModelValidator<ManyMessageModel>>(new ManyMessageValidator());
                services.AddSingleton<IModelValidator<EmptyMessageModel>>(new EmptyMessageValidator());
                services.AddScoped<FluentValidation.IValidator<PolymorphicSampleOrder>, PolymorphicSampleOrderValidator>();
                services.AddScoped<FluentValidation.IValidator<RushOnlyPolymorphicOrder>, RushOnlyPolymorphicOrderValidator>();
                services.AddProblemDetails();
                services.Configure<ApiBehaviorOptions>(options =>
                    options.ClientErrorMapping[StatusCodes.Status400BadRequest].Link =
                        "https://example.test/bad-request");
            });

    [Fact]
    public async Task The_mvc_400_is_built_the_way_the_apps_other_400s_are()
    {
        // A hand-built ObjectResult carries whatever the filter puts in it and nothing else:
        // no traceId, and none of the customisation ControllerBase.ValidationProblem() honours,
        // so a 400 from this filter reads differently from every other 400 the same app
        // returns. Building it through the app's own ProblemDetailsFactory is what closes that.
        await using var app = await StartProblemDetailsAppAsync();
        var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync("mvc/orders",
            new SampleOrder { Description = "a-b", Items = [new SampleItem()] });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);

        Assert.True(document.RootElement.TryGetProperty("traceId", out var traceId));
        Assert.False(string.IsNullOrEmpty(traceId.GetString()));
        Assert.Equal("https://example.test/bad-request", document.RootElement.GetProperty("type").GetString());

        // The parts the filter owns are untouched by the change of builder.
        var problem = JsonSerializer.Deserialize<FormidableValidationProblem>(body, JsonSerializerOptions.Web);
        Assert.Contains("Sku required", problem!.Errors["Items[0].Sku"]);
        Assert.Contains(problem.Advisories, advisory => advisory.Path == "Description");
        Assert.Equal(400, document.RootElement.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task The_mvc_400_keeps_every_message_for_one_path_in_report_order()
    {
        // The error dictionary reaches the response through a ModelStateDictionary, which holds
        // a LIST per key: a path carrying more than one message keeps all of them, in the order
        // the report gave them. Two messages under one path is what makes this an assertion
        // rather than a restatement — with one message each, keeping the first and keeping all
        // of them are the same answer.
        await using var app = await StartProblemDetailsAppAsync();
        var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync("mvc/many-messages", new ManyMessageModel());

        var problem = await response.Content.ReadFromJsonAsync<FormidableValidationProblem>(
            JsonSerializerOptions.Web);

        Assert.Equal(["a first", "a second"], problem!.Errors["Apple"]);
        Assert.Equal(["z first"], problem.Errors["Zebra"]);
    }

    [Fact]
    public async Task The_two_adapters_order_the_error_keys_differently()
    {
        // A divergence the wire contract does not cover and the corpus records rather than
        // hides. The endpoint filter serves the dictionary the mapper built, so its keys come
        // out in report order; the action filter's keys come back from a ModelStateDictionary,
        // which is a prefix trie and enumerates its own way. Every key and every message is
        // present on both sides — it is the key SEQUENCE that belongs to each framework half.
        await using var mvc = await StartProblemDetailsAppAsync();
        var mvcResponse = await mvc.GetTestClient().PostAsJsonAsync("mvc/many-messages", new ManyMessageModel());
        var mvcProblem = await mvcResponse.Content.ReadFromJsonAsync<FormidableValidationProblem>(
            JsonSerializerOptions.Web);

        await using var minimal = await TestApp.StartAsync(
            app => app.MapPost("/many-messages", (ManyMessageModel model) => Results.Ok())
                .Validate<ManyMessageModel>(),
            services => services.AddSingleton<IModelValidator<ManyMessageModel>>(new ManyMessageValidator()));
        var minimalResponse = await minimal.GetTestClient()
            .PostAsJsonAsync("/many-messages", new ManyMessageModel());
        var minimalProblem = await minimalResponse.Content.ReadFromJsonAsync<FormidableValidationProblem>(
            JsonSerializerOptions.Web);

        Assert.Equal(["Zebra", "Apple"], minimalProblem!.Errors.Keys);
        Assert.Equal(["Apple", "Zebra"], mvcProblem!.Errors.Keys);

        // Same keys, same messages under each: only the sequence parts company.
        Assert.Equal(
            minimalProblem.Errors.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Value),
            mvcProblem.Errors.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Value));
    }

    [Fact]
    public async Task The_mvc_400_carries_a_trace_id_without_AddProblemDetails()
    {
        // The asymmetry the corpus states: MVC's ProblemDetailsFactory writes the trace
        // identifier whatever the host configured, while the endpoint filter's
        // TypedResults.ValidationProblem gets one only where an IProblemDetailsService exists.
        // Neither app here calls AddProblemDetails(), which is the whole point of the pair.
        await using var mvc = await StartMvcAppAsync();
        var mvcResponse = await mvc.GetTestClient().PostAsJsonAsync("mvc/many-messages", new ManyMessageModel());
        using var mvcBody = JsonDocument.Parse(await mvcResponse.Content.ReadAsStringAsync());
        Assert.True(mvcBody.RootElement.TryGetProperty("traceId", out var traceId));
        Assert.False(string.IsNullOrEmpty(traceId.GetString()));

        await using var minimal = await TestApp.StartAsync(
            app => app.MapPost("/many-messages", (ManyMessageModel model) => Results.Ok())
                .Validate<ManyMessageModel>(),
            services => services.AddSingleton<IModelValidator<ManyMessageModel>>(new ManyMessageValidator()));
        var minimalResponse = await minimal.GetTestClient()
            .PostAsJsonAsync("/many-messages", new ManyMessageModel());
        using var minimalBody = JsonDocument.Parse(await minimalResponse.Content.ReadAsStringAsync());
        Assert.False(minimalBody.RootElement.TryGetProperty("traceId", out _));
    }

    [Fact]
    public async Task An_empty_message_reaches_the_two_adapters_differently()
    {
        // The one place the errors dictionary itself parts company, and it is reachable through
        // the tolerance the mapper applies to a null message: ValidationProblemDetails built
        // from a ModelStateDictionary substitutes its own text for an empty one, so what the
        // endpoint filter sends as "" arrives from the action filter as a sentence of the
        // framework's. The exact wording is MVC's to choose; that it is not the empty string is
        // the divergence the corpus records.
        await using var mvc = await StartProblemDetailsAppAsync();
        var mvcResponse = await mvc.GetTestClient().PostAsJsonAsync("mvc/empty-message", new EmptyMessageModel());
        var mvcProblem = await mvcResponse.Content.ReadFromJsonAsync<FormidableValidationProblem>(
            JsonSerializerOptions.Web);

        await using var minimal = await TestApp.StartAsync(
            app => app.MapPost("/empty-message", (EmptyMessageModel model) => Results.Ok())
                .Validate<EmptyMessageModel>(),
            services => services.AddSingleton<IModelValidator<EmptyMessageModel>>(new EmptyMessageValidator()));
        var minimalResponse = await minimal.GetTestClient()
            .PostAsJsonAsync("/empty-message", new EmptyMessageModel());
        var minimalProblem = await minimalResponse.Content.ReadFromJsonAsync<FormidableValidationProblem>(
            JsonSerializerOptions.Web);

        Assert.Equal([string.Empty], minimalProblem!.Errors["Field"]);
        Assert.NotEqual([string.Empty], mvcProblem!.Errors["Field"]);
        Assert.NotEmpty(Assert.Single(mvcProblem.Errors["Field"]));
    }
}
