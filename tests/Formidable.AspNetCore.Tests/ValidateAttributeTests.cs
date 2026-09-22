using System.Net;
using System.Reflection;
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
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
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

    [HttpPost("strict-derived-only")]
    [Validate(RequireValidator = true)]
    public IActionResult StrictDerivedOnly([FromBody] OnlyDerivedPolymorphicOrder order) => Ok(order);

    [HttpPost("strict-explicit-unregistered")]
    [Validate(typeof(UnregisteredModel), RequireValidator = true)]
    public IActionResult StrictExplicitUnregistered([FromBody] UnregisteredModel model) => Ok(model);

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

/// <summary>A plain <see cref="Controller"/> — no <c>[ApiController]</c>, so none of the
/// framework's automatic 400s stand between a request and the action. Its strict action binds a
/// route value beside its body, which is the shape that used to make a null body a 500: the route
/// value counted as "the action bound something", the null body validated nothing, and strict mode
/// read the pair as a misconfiguration.</summary>
[Route("plain")]
public sealed class PlainOrdersController : Controller
{
    [HttpPost("strict/{id:int}")]
    [Validate(RequireValidator = true)]
    public IActionResult StrictWithRouteValue(int id, [FromBody] SampleOrder order) =>
        Ok(new { id, bodyWasNull = order is null });
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

/// <summary>Never has a validator registered anywhere in this file's test apps — pins where a
/// missing registration is reported: naming the type on the attribute makes the resolution fail
/// loudly, discovery mode alone skips the argument, and discovery mode under
/// <c>RequireValidator</c> refuses the request because no type the action declares resolves a
/// validator.</summary>
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

        // The positive control for the request-time discovery probe as well as for the filter: the
        // declared parameter type has a registered validator, so strict mode has nothing to say
        // and the request runs to the action.
        var response = await client.PostAsJsonAsync("mvc/strict-orders",
            new SampleOrder { Description = "ok", Items = [new SampleItem { Sku = "A" }] });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task RequireValidator_fails_the_host_when_no_parameter_could_carry_the_named_model()
    {
        // The misconfiguration strict mode exists to catch: a named type that matches no declared
        // parameter. It is decided from the parameter list alone, so it is settled while MVC
        // builds its application model -- the host never starts, and no request is ever made.
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => StartFixedControllerAppAsync(typeof(NamedTypeMatchesNothing)));

        // Names the DECLARED model type. The old message listed the runtime types of whatever
        // happened to bind, so an action taking a route value reported "Int32" and never the
        // model whose validator was the point.
        Assert.Contains(nameof(SampleOrder), exception.Message);
        Assert.Contains(nameof(NamedTypeMatchesNothing.NoModel), exception.Message);
        Assert.Contains("RequireValidator", exception.Message);
    }

    [Fact]
    public async Task A_class_level_RequireValidator_is_checked_for_every_action_of_the_controller()
    {
        // A class-level attribute reaches MVC as a CONTROLLER convention and never as an action
        // one, so covering only IActionModelConvention would leave this placement unchecked.
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => StartFixedControllerAppAsync(typeof(ClassLevelStrict)));

        Assert.Contains(nameof(ClassLevelStrict.Ping), exception.Message);
    }

    [Fact]
    public async Task RequireValidator_without_named_types_still_refuses_an_action_with_no_parameters()
    {
        // Discovery mode names no type, so what makes a parameter validatable is a registration a
        // convention cannot read. An action with no parameters at all is decidable without it.
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => StartFixedControllerAppAsync(typeof(DiscoveryStrictNoParameters)));

        Assert.Contains("no parameters at all", exception.Message);
    }

    [Fact]
    public async Task A_base_typed_parameter_satisfies_RequireValidator_for_a_named_derived_model()
    {
        // The assignability direction is load-bearing and runs the opposite way from the
        // minimal-API check. A [FromBody] base-typed parameter can bind a derived instance, and
        // ResolveValidatedType validates that instance as its RUNTIME type, so the parameter can
        // carry the named model even though the parameter's own type is not it.
        await using var app = await StartFixedControllerAppAsync(typeof(BaseTypedParameter));

        Assert.NotNull(app);

        // And a request to it runs. Naming the types settles strictness at model build, so
        // nothing re-decides it per request: the declared parameter here is not one of the named
        // types, and a request-time check that did not know it was in named-type mode would read
        // that as the misconfiguration the model build just cleared.
        var response = await app.GetTestClient().PostAsJsonAsync("base-typed-parameter",
            new PolymorphicSampleOrder { Description = "ok" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_derived_typed_parameter_does_not_satisfy_RequireValidator_for_a_named_base_model()
    {
        // The other side of that direction, and why it cannot be assignability either way: a
        // parameter declared as the DERIVED type never resolves the named base type at run time --
        // ShouldValidate compares the explicit list by exact type, and the runtime type of
        // anything bound to that parameter is the derived one too. Nothing would validate.
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => StartFixedControllerAppAsync(typeof(DerivedTypedParameter)));

        Assert.Contains(nameof(PolymorphicSampleOrder), exception.Message);
    }

    [Fact]
    public async Task A_null_body_beside_a_route_value_reaches_a_plain_controller_action()
    {
        await using var app = await StartMvcAppAsync();
        var client = app.GetTestClient();

        // A plain Controller has none of [ApiController]'s automatic 400s, so a null body reaches
        // the action with a null model beside a bound route value -- a request shape any anonymous
        // client can send. Strict mode must not read that as a misconfiguration: it is decided
        // from the action's declared parameters, which no request can influence.
        var response = await client.PostAsync("plain/strict/42",
            new StringContent("null", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"bodyWasNull\":true", body);
    }

    [Fact]
    public async Task Strict_discovery_mode_reports_a_declared_model_no_registration_covers()
    {
        await using var app = await StartMvcAppAsync();
        var client = app.GetTestClient();

        // The catch strict discovery mode exists for, and the one an application-model convention
        // cannot make: no IValidator<T> covers UnregisteredModel, which is what this action
        // declares -- the shape a dropped AddValidatorsFromAssembly leaves behind. It is read from
        // the DECLARED parameter list, which is why the two requests below differ only in what
        // they send and fail identically; reading what they BOUND would put the timing of a
        // configuration error in a client's hands.
        var first = await client.PostAsJsonAsync("mvc/strict-unregistered", new UnregisteredModel { Name = "x" });
        var second = await client.PostAsJsonAsync("mvc/strict-unregistered", new UnregisteredModel());

        Assert.Equal(HttpStatusCode.InternalServerError, first.StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, second.StatusCode);

        var body = await first.Content.ReadAsStringAsync();
        Assert.Contains(nameof(UnregisteredModel), body);
        Assert.Contains(nameof(OrdersController.StrictUnregistered), body);
        Assert.Contains("RequireValidator", body);
        Assert.Equal(body, await second.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Strict_discovery_mode_refuses_a_model_only_a_runtime_type_would_reach()
    {
        await using var app = await StartMvcAppAsync();
        var client = app.GetTestClient();

        // The one shape strict discovery mode refuses that the filter would have validated, kept
        // deliberate. Runtime_type_fallback_validates_when_the_declared_type_has_no_validator
        // posts this exact body to the same parameter shape WITHOUT RequireValidator and gets a
        // 400: the runtime-type fallback really does reach RushOnlyPolymorphicOrder's validator.
        // Strict mode reads the DECLARED type, OnlyDerivedPolymorphicOrder resolves nothing, and
        // seeing the difference would mean reading what the request bound -- the client-reachable
        // check this design exists to avoid. Naming the type keeps the check and the validation
        // both.
        var response = await client.PostAsync("mvc/strict-derived-only",
            new StringContent("""{"$type":"rush","description":""}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains(nameof(OnlyDerivedPolymorphicOrder), await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Strict_named_type_mode_still_reports_the_missing_validator_registration()
    {
        await using var app = await StartMvcAppAsync();
        var client = app.GetTestClient();

        // Naming the types settles strictness entirely at model build, so the request-time probe
        // is gated off and what a request meets is the resolution failure the explicit path has
        // always reported. A probe that ran for named types too would answer first and say
        // something else, which is what the second assertion holds it to.
        var response = await client.PostAsJsonAsync("mvc/strict-explicit-unregistered", new UnregisteredModel());

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("No FluentValidation validator for", body);
        Assert.DoesNotContain("RequireValidator", body);
    }

    [Fact]
    public async Task RequireValidator_does_not_throw_on_a_client_caused_empty_body()
    {
        await using var app = await StartMvcAppAsync();
        var client = app.GetTestClient();

        // The [ApiController] sibling of the plain-controller case above: a nullable body posted
        // the JSON literal `null` binds null and runs the action, so nothing is validated on a
        // request any anonymous client can send. (The action's own Ok(null) becomes a 204 via
        // ASP.NET Core's own null-body output formatting -- the assertion checks for "not a
        // strict-mode 500", not one exact success code, so it stays true regardless of that
        // unrelated framework behavior.)
        var response = await client.PostAsync("mvc/strict-optional",
            new StringContent("null", Encoding.UTF8, "application/json"));

        Assert.True(response.IsSuccessStatusCode, $"Expected a success status, got {response.StatusCode}");
    }

    // Controllers for the strict-mode checks live nested and are handed to MVC one at a time.
    // Nested public types are invisible to MVC's own ControllerFeatureProvider (Type.IsPublic is
    // false for them), so a controller written to fail the application-model build cannot leak
    // into any other app in this file and take its tests down with it.
    private static Task<Microsoft.AspNetCore.Builder.WebApplication> StartFixedControllerAppAsync(Type controllerType) =>
        TestApp.StartAsync(
            app => app.MapControllers(),
            services => services.AddControllers().ConfigureApplicationPartManager(
                manager => manager.FeatureProviders.Add(new FixedControllerFeature(controllerType))));

    private sealed class FixedControllerFeature(Type controllerType) : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature) =>
            feature.Controllers.Add(controllerType.GetTypeInfo());
    }

    public sealed class NamedTypeMatchesNothing : ControllerBase
    {
        [HttpPost("named-type-matches-nothing/{id:int}")]
        [Validate(typeof(SampleOrder), RequireValidator = true)]
        public IActionResult NoModel(int id) => Ok(id);
    }

    [Validate(typeof(SampleOrder), RequireValidator = true)]
    public sealed class ClassLevelStrict : ControllerBase
    {
        [HttpGet("class-level-strict")]
        public IActionResult Ping() => Ok();
    }

    public sealed class DiscoveryStrictNoParameters : ControllerBase
    {
        [HttpGet("discovery-strict-no-parameters")]
        [Validate(RequireValidator = true)]
        public IActionResult Ping() => Ok();
    }

    public sealed class BaseTypedParameter : ControllerBase
    {
        [HttpPost("base-typed-parameter")]
        [Validate(typeof(RushPolymorphicSampleOrder), RequireValidator = true)]
        public IActionResult Submit([FromBody] PolymorphicSampleOrder order) => Ok(order);
    }

    public sealed class DerivedTypedParameter : ControllerBase
    {
        [HttpPost("derived-typed-parameter")]
        [Validate(typeof(PolymorphicSampleOrder), RequireValidator = true)]
        public IActionResult Submit([FromBody] RushPolymorphicSampleOrder order) => Ok(order);
    }

    [Validate(RequireValidator = true)]
    public sealed class ClassLevelDiscoveryStrict : ControllerBase
    {
        [HttpPost("class-level-discovery/covered")]
        public IActionResult Covered([FromBody] SampleOrder order) => Ok(order);

        [HttpPost("class-level-discovery/uncovered")]
        public IActionResult Uncovered([FromBody] UnregisteredModel model) => Ok(model);
    }

    [Fact]
    public async Task A_class_level_discovery_strict_attribute_answers_per_action()
    {
        await using var app = await StartFixedControllerAppAsync(typeof(ClassLevelDiscoveryStrict));
        var client = app.GetTestClient();

        // One attribute instance serves every action of the controller it sits on, so the
        // registration answer it computes has to belong to the action rather than to itself. Both
        // actions declare a parameter, so the model-build half passes for each; only the second
        // declares a type nothing validates.
        var covered = await client.PostAsJsonAsync("class-level-discovery/covered",
            new SampleOrder { Description = "ok" });
        var uncovered = await client.PostAsJsonAsync("class-level-discovery/uncovered", new UnregisteredModel());

        Assert.Equal(HttpStatusCode.OK, covered.StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, uncovered.StatusCode);
        Assert.Contains(nameof(UnregisteredModel), await uncovered.Content.ReadAsStringAsync());
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
