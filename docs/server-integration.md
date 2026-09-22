# Server integration

Formidable's server story is one validator run twice: the same FluentValidation rules and
profile definitions that drive the client run again on the server, and a rejected request comes
back in exactly the shape the client already knows how to apply. Two adapters cover the two
common hosting models — a minimal-API endpoint filter and an MVC action filter — and both funnel
into the same wire format, defined once, in the dependency-free core package.

## The wire contract

A rejected request returns a 400 `ValidationProblemDetails`. Its `errors` dictionary is keyed by
the same property-path format the client uses internally (`Items[0].Sku`, and so on — see
[`docs/collections-and-row-identity.md`](collections-and-row-identity.md)), and a `warnings`
extension alongside it carries every non-error issue from the same report. The client-side shape
of that body is one type in the core `Formidable` package — no ASP.NET Core or Blazor dependency
required to read it:

```csharp
namespace Formidable;

/// <summary>
/// Client-side shape of the validation ProblemDetails body produced by Formidable.AspNetCore:
/// the standard <c>errors</c> dictionary keyed by property path plus a <c>warnings</c>
/// extension for non-error issues. Deserialize an HTTP 400 body into this (web JSON defaults,
/// e.g. <c>ReadFromJsonAsync</c>) and pass <see cref="ToIssues"/> to the Blazor engine's
/// server-issue application.
/// </summary>
public sealed class FormidableValidationProblem
{
    /// <summary>Error messages keyed by property path (standard ValidationProblemDetails shape).</summary>
    public Dictionary<string, string[]> Errors { get; set; } = [];

    /// <summary>Non-error issues from the <c>warnings</c> extension.</summary>
    public List<ValidationProblemWarning> Warnings { get; set; } = [];

    /// <summary>
    /// Flattens the payload into engine-ready issues: error entries first (one issue per
    /// message), then warnings with their severity parsed case-insensitively — unknown or
    /// "Error" severities read as <see cref="ValidationSeverity.Warning"/>, because the
    /// errors dictionary is the only error channel.
    /// </summary>
    public IReadOnlyList<ValidationIssue> ToIssues()
    {
        var issues = new List<ValidationIssue>();

        // A foreign 400 body can carry explicit JSON nulls that override the property
        // initializers below (the deserializer doesn't enforce nullable-reference annotations),
        // and a key's message array itself can be null — tolerate both rather than throw.
        var errors = Errors ?? new Dictionary<string, string[]>();
        var warnings = Warnings ?? [];

        foreach (var (path, messages) in errors)
        {
            issues.AddRange((messages ?? []).Select(message => new ValidationIssue(path, message)));
        }

        foreach (var warning in warnings)
        {
            var severity =
                Enum.TryParse<ValidationSeverity>(warning.Severity, ignoreCase: true, out var parsed)
                && parsed != ValidationSeverity.Error
                    ? parsed
                    : ValidationSeverity.Warning;

            issues.Add(new ValidationIssue(warning.Path, warning.Message, severity, warning.Code, warning.DisplayName));
        }

        return issues;
    }
}
```

*Source: `src/Formidable/FormidableValidationProblem.cs`*

```csharp
/// <summary>
/// The wire shape of one non-error issue carried on the <c>warnings</c> extension of a
/// validation ProblemDetails payload. <paramref name="Severity"/> is the
/// <see cref="ValidationSeverity"/> member name as a string ("Warning" or "Info").
/// </summary>
/// <param name="Path">Property path in the client's format, e.g. <c>Items[0].Sku</c>.</param>
/// <param name="Message">Human-readable message.</param>
/// <param name="Severity">Severity name; unknown values are read as Warning.</param>
/// <param name="Code">Optional machine-readable code.</param>
/// <param name="DisplayName">Optional user-facing field name.</param>
public sealed record ValidationProblemWarning(
    string Path,
    string Message,
    string Severity,
    string? Code = null,
    string? DisplayName = null);
```

*Source: `src/Formidable/ValidationProblemWarning.cs`*

`ToIssues()` deliberately tolerates a null or hostile payload shape rather than throwing — a
foreign 400 body (a proxy, a gateway, a handwritten test double) can carry explicit JSON nulls
that survive deserialization and override the property initializers above.

Building the other side of that contract — turning a `ValidationReport` into the two wire pieces
— is one static mapper in `Formidable.AspNetCore`, shared by both server adapters below:

```csharp
namespace Formidable.AspNetCore;

/// <summary>
/// Maps a <see cref="ValidationReport"/> to the wire shape shared with the client:
/// error messages keyed by property path plus non-error issues for the
/// <see cref="WarningsExtensionKey"/> ProblemDetails extension.
/// </summary>
public static class ValidationReportProblemMapper
{
    /// <summary>The ProblemDetails extension key carrying non-error issues.</summary>
    public const string WarningsExtensionKey = "warnings";

    /// <summary>Error messages grouped by path, preserving issue order within each path.</summary>
    public static Dictionary<string, string[]> ToErrorDictionary(ValidationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return report.Errors
            .GroupBy(issue => issue.Path)
            .ToDictionary(group => group.Key, group => group.Select(issue => issue.Message).ToArray());
    }

    /// <summary>Non-error issues as the warnings-extension payload, in issue order.</summary>
    public static List<ValidationProblemWarning> ToWarnings(ValidationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return report.Issues
            .Where(issue => issue.Severity != ValidationSeverity.Error)
            .Select(issue => new ValidationProblemWarning(
                issue.Path, issue.Message, issue.Severity.ToString(), issue.Code, issue.DisplayName))
            .ToList();
    }
}
```

*Source: `src/Formidable.AspNetCore/ValidationReportProblemMapper.cs`*

## Minimal APIs

`Validate<TModel>(profile?)` is an endpoint-filter extension with two overloads — one route
handler at a time, or every handler in a route group at once:

```csharp
namespace Formidable.AspNetCore;

/// <summary>Minimal-API validation conventions.</summary>
public static class FormidableEndpointFilterExtensions
{
    /// <summary>
    /// Normalizes (when <typeparamref name="TModel"/> implements
    /// <see cref="INormalizableModel"/>) and validates the endpoint's
    /// <typeparamref name="TModel"/> argument with the given profile before the handler runs.
    /// Error issues short-circuit to a 400 ValidationProblemDetails whose <c>errors</c> keys
    /// use the client's path format; non-error issues ride the <c>warnings</c> extension and
    /// never block on their own.
    /// </summary>
    /// <param name="builder">The route handler to validate.</param>
    /// <param name="profile">The profile to run; defaults to <see cref="ValidationProfile.Submit"/>.</param>
    public static RouteHandlerBuilder Validate<TModel>(this RouteHandlerBuilder builder, ValidationProfile? profile = null)
        where TModel : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddEndpointFilter(new ValidationEndpointFilter<TModel>(profile ?? ValidationProfile.Submit));
    }

    /// <summary>
    /// Normalizes (when <typeparamref name="TModel"/> implements
    /// <see cref="INormalizableModel"/>) and validates the endpoint's
    /// <typeparamref name="TModel"/> argument with the given profile before the handler runs.
    /// Error issues short-circuit to a 400 ValidationProblemDetails whose <c>errors</c> keys
    /// use the client's path format; non-error issues ride the <c>warnings</c> extension and
    /// never block on their own.
    /// </summary>
    /// <param name="builder">The route group to validate.</param>
    /// <param name="profile">The profile to run; defaults to <see cref="ValidationProfile.Submit"/>.</param>
    /// <remarks>
    /// Every endpoint in the group must bind an argument assignable to <typeparamref name="TModel"/> —
    /// endpoints without one throw at request time.
    /// </remarks>
    public static RouteGroupBuilder Validate<TModel>(this RouteGroupBuilder builder, ValidationProfile? profile = null)
        where TModel : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddEndpointFilter(new ValidationEndpointFilter<TModel>(profile ?? ValidationProfile.Submit));
    }
}
```

*Source: `src/Formidable.AspNetCore/FormidableEndpointFilterExtensions.cs`*

Both overloads default to `ValidationProfile.Submit` and install the same filter — the group
overload just attaches it to every endpoint the group defines. That's also its one caveat: since
the filter looks for a `TModel` argument on whichever endpoint actually runs, an endpoint added to
a validated group without one has nothing for the filter to validate.

```csharp
namespace Formidable.AspNetCore;

/// <summary>Runs normalize + profile validation for one endpoint argument type.</summary>
internal sealed class ValidationEndpointFilter<TModel> : IEndpointFilter
    where TModel : class
{
    private readonly ValidationProfile _profile;

    public ValidationEndpointFilter(ValidationProfile profile) => _profile = profile;

    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var model = context.Arguments.OfType<TModel>().FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"Validate<{FriendlyTypeName.Of(typeof(TModel))}>() found no endpoint argument of that type — there is nothing to validate.");

        (model as INormalizableModel)?.Normalize();

        var validator = context.HttpContext.RequestServices.GetRequiredService<IModelValidator<TModel>>();
        var report = await validator.ValidateAsync(model, _profile, context.HttpContext.RequestAborted);

        if (report.IsValid)
        {
            return await next(context);
        }

        var warnings = ValidationReportProblemMapper.ToWarnings(report);
        return TypedResults.ValidationProblem(
            ValidationReportProblemMapper.ToErrorDictionary(report),
            extensions: warnings.Count > 0
                ? new Dictionary<string, object?> { [ValidationReportProblemMapper.WarningsExtensionKey] = warnings }
                : null);
    }
}
```

*Source: `src/Formidable.AspNetCore/ValidationEndpointFilter.cs`*

That throw is exactly the caveat above made concrete — a group-validated endpoint with no
`TModel`-typed argument fails every request with an `InvalidOperationException`, not a silent
skip. `report.IsValid` is `true` whenever the report has no error-severity issues (warnings and
infos don't affect it — see [`docs/severity.md`](severity.md)), which is why an all-warnings
report falls straight through to `next(context)` and the handler's own return value, unmodified.

The sample wires the group overload:

```csharp
// Group-level validation: every endpoint in the group runs the submit profile.
var orders = app.MapGroup("/api/orders").Validate<RoundTripOrder>();
orders.MapPost("/", (RoundTripOrder order) => Results.Ok(new { accepted = true, lines = order.Lines.Count }));
```

*Source: `samples/Formidable.Sample.Api/Program.cs`*

## MVC

`[Validate]` is an `ActionFilterAttribute` usable on a method or a class. Without constructor
arguments it discovers which action arguments to validate by probing DI for a registered
FluentValidation `IValidator<T>`; passed explicit types, it validates exactly those argument
types regardless of how their `IModelValidator<T>` adapter is registered:

```csharp
    /// <summary>Validates arguments discovered by validator registration.</summary>
    public ValidateAttribute() => _modelTypes = [];

    /// <summary>Validates the arguments of exactly these model types.</summary>
    public ValidateAttribute(params Type[] modelTypes) => _modelTypes = modelTypes;
```

*Source: `src/Formidable.AspNetCore/ValidateAttribute.cs`*

```csharp
    private bool ShouldValidate(Type argumentType, IServiceProvider services)
    {
        if (_modelTypes.Length > 0)
        {
            return _modelTypes.Contains(argumentType);
        }

        // Probe the FluentValidation registration directly: resolving IModelValidator<T>
        // through the open-generic adapter THROWS when no IValidator<T> exists, so the
        // validator interface itself is the safe presence check for the shipped path.
        return services.GetService(typeof(FluentValidation.IValidator<>).MakeGenericType(argumentType)) is not null;
    }
```

*Source: `src/Formidable.AspNetCore/ValidateAttribute.cs`*

Placed on a class, `[Validate]` applies to every action on it — the sample uses exactly this
shape, with no explicit model types, relying on discovery:

```csharp
[ApiController]
[Route("api/agreements")]
[Validate] // class-level: every action's validatable arguments run the Submit profile
public class AgreementsController : ControllerBase
{
    [HttpPost]
    public IActionResult Create([FromBody] RoundTripOrder order) =>
        Ok(new { accepted = true, lines = order.Lines.Count });
}
```

*Source: `samples/Formidable.Sample.Api/Controllers/AgreementsController.cs`*

An action can bind more than one validatable argument. `[Validate]` runs normalize-then-validate
on every one of them and aggregates every issue from every argument into a single `ValidationReport`
before deciding whether to short-circuit — one 400 for the whole action, not one per argument:

```csharp
    public override async Task OnActionExecutionAsync(
        ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var profile = ResolveProfile(Profile);
        var services = context.HttpContext.RequestServices;
        var issues = new List<ValidationIssue>();

        foreach (var argument in context.ActionArguments.Values)
        {
            if (argument is null)
            {
                continue;
            }

            var argumentType = argument.GetType();
            if (!ShouldValidate(argumentType, services))
            {
                continue;
            }

            (argument as INormalizableModel)?.Normalize();

            object validator;
            try
            {
                validator = services.GetRequiredService(typeof(IModelValidator<>).MakeGenericType(argumentType));
            }
            catch (InvalidOperationException ex)
            {
                // The IValidator<T> probe in ShouldValidate succeeded, so FluentValidation is
                // registered — this failure means the IModelValidator<T> adapter itself was
                // never wired up, almost always because AddFormidable() was never called.
                throw new InvalidOperationException(
                    $"No IModelValidator<{FriendlyTypeName.Of(argumentType)}> is resolvable — call services.AddFormidable() to register the FluentValidation adapter.",
                    ex);
            }

            var report = await InvokeValidateAsync(validator, argumentType, argument, profile, context.HttpContext.RequestAborted);
            issues.AddRange(report.Issues);
        }

        var aggregate = new ValidationReport(issues);
        if (!aggregate.IsValid)
        {
            var problem = new ValidationProblemDetails(ValidationReportProblemMapper.ToErrorDictionary(aggregate))
            {
                Status = StatusCodes.Status400BadRequest,
                Type = "https://tools.ietf.org/html/rfc9110#section-15.5.1"
            };

            var warnings = ValidationReportProblemMapper.ToWarnings(aggregate);
            if (warnings.Count > 0)
            {
                problem.Extensions[ValidationReportProblemMapper.WarningsExtensionKey] = warnings;
            }

            // Match TypedResults.ValidationProblem's wire shape exactly rather than relying on
            // BadRequestObjectResult's implicit content negotiation for the media type.
            context.Result = new ObjectResult(problem)
            {
                StatusCode = StatusCodes.Status400BadRequest,
                ContentTypes = { "application/problem+json" }
            };
            return;
        }

        await next();
    }
```

*Source: `src/Formidable.AspNetCore/ValidateAttribute.cs`*

`null` arguments and arguments `ShouldValidate` doesn't select are skipped entirely — neither
normalized nor validated — before the aggregate's `IsValid` gate runs once, after the loop.

### Profile string mapping

`[Validate]`'s `Profile` property is a string (`"Submit"` by default), resolved once per request
against the same two conventional profiles the client uses (see
[`docs/profiles.md`](profiles.md)):

```csharp
    private static ValidationProfile ResolveProfile(string name)
    {
        // Match the two built-in profile names case-insensitively so "draft"/"DRAFT" resolve to
        // the canonical singletons instead of silently becoming a custom profile that requests a
        // nonexistent same-cased ruleset. Custom profile names are passed through unchanged.
        if (string.Equals(name, "Draft", StringComparison.OrdinalIgnoreCase))
        {
            return ValidationProfile.Draft;
        }

        if (string.Equals(name, "Submit", StringComparison.OrdinalIgnoreCase))
        {
            return ValidationProfile.Submit;
        }

        return ValidationProfile.Named(name, includeDefaultRules: true, name);
    }
```

*Source: `src/Formidable.AspNetCore/ValidateAttribute.cs`*

`"Draft"` and `"Submit"` match case-insensitively; any other string becomes a custom profile
shaped the same way `Submit` itself is built — default rules plus one ruleset with the same name.
The minimal-API filter takes a `ValidationProfile` value directly instead of a name string, since
`Validate<TModel>(profile?)` is a compile-time call site, not a request-time attribute property.

## Normalize pipeline

Both adapters run the model's own cleanup hook before validation, if it has one:

```csharp
namespace Formidable;

/// <summary>
/// A model that can clear values not applicable to its current selections — deselected option
/// branches, rows with no content — before persistence or validation. Called by the server
/// validation filters before validation; client code may invoke it directly before saving
/// drafts.
/// </summary>
public interface INormalizableModel
{
    /// <summary>Clears fields not applicable to the current selection state and strips empty collection rows.</summary>
    void Normalize();
}
```

*Source: `src/Formidable/INormalizableModel.cs`*

The sample's order model implements it to drop rows filled with only whitespace, keeping
truly-empty rows alone:

```csharp
public class RoundTripOrder : INormalizableModel
{
    public string Description { get; set; } = string.Empty;
    public List<OrderLine> Lines { get; set; } = [];

    public void Normalize()
    {
        // Whitespace-only SKUs are noise - drop those lines entirely. A genuinely empty
        // SKU ("") survives on purpose so NotEmpty can point at the row.
        Lines.RemoveAll(line => line.Sku.Length > 0 && string.IsNullOrWhiteSpace(line.Sku));
    }
}
```

*Excerpt from `samples/Formidable.Sample.Shared/RoundTripOrder.cs`*

Both `ValidationEndpointFilter<TModel>` and `[Validate]` call `(model as INormalizableModel)?.Normalize()`
in place, on the exact instance the framework already deserialized and bound — before that instance
is handed to `ValidateAsync`. `Normalize()` mutates the object rather than returning a copy, so the
same instance the request model binder produced is what the validator checks and, if validation
passes, what the route handler or action ultimately runs against — the handler sees the normalized
model, not the wire-deserialized one.

## Client round-trip

The client side is three calls: deserialize the 400 body, flatten it with `ToIssues()`, and hand
the result to the engine. `IFormValidationEngine.ApplyServerIssues` documents its own contract in
full:

```csharp
    /// <summary>
    /// Applies server-declared issues (e.g. from a 400 ValidationProblemDetails) as if they were
    /// submit results. The payload is treated as the server's CURRENT verdict: each call replaces
    /// the issues added by the previous call, rather than accumulating with them, so re-submitting
    /// the same or a corrected payload does not duplicate inline errors. Client-sourced submit
    /// issues on the same fields are unaffected by a replace. Only error-severity issues in
    /// <paramref name="issues"/> are applied; other severities are ignored. Applied issues also
    /// persist until the next debounced refresh replaces the submit-visible state from the client
    /// validator's report; a server-only issue with no matching client rule clears on that refresh.
    /// Call from the renderer's synchronization context (a Blazor event handler or
    /// <c>InvokeAsync</c>) — it mutates validation state and triggers renders.
    /// </summary>
    /// <remarks>
    /// Replace is value-equality-based: if a client-sourced issue on a field is value-identical
    /// to a server issue previously applied to that field, a subsequent replace may remove either
    /// of the two equal entries — the two are indistinguishable, so which one is removed is
    /// unspecified.
    /// </remarks>
    void ApplyServerIssues(IReadOnlyList<ValidationIssue> issues);
```

*Source: `src/Formidable.Blazor/IFormValidationEngine.cs`*

**Replace, not accumulate.** Each call is the server's current verdict, full stop — it undoes
exactly what its own previous call added, then applies the new payload. Calling it twice in a row
with the same or a corrected body never leaves a stale duplicate inline error behind. Only
error-severity issues in the payload are applied to fields; anything else in the payload is
ignored by the engine entirely — which is why the page, not the engine, is responsible for
presenting the warnings a 400 carries.

The sample deliberately skips client-side submit validation so the round-trip is visible end to
end — press Send and the server's 400 lands on the exact fields:

```csharp
    private async Task Send()
    {
        // Normalizing before the POST keeps the client's line list identical to what the
        // server validates (its filter normalizes too) - so issue paths always match rows.
        _order.Normalize();
        _serverWarnings.Clear();
        var response = await Http.PostAsJsonAsync(_endpoint, _order);

        if (response.IsSuccessStatusCode)
        {
            _status = "Server accepted the order.";
            return;
        }

        var problem = await response.Content.ReadFromJsonAsync<FormidableValidationProblem>();
        var issues = problem!.ToIssues();

        // The engine applies error-severity issues to fields; non-error issues are the
        // page's to present (a 400's warnings ride alongside its errors by contract).
        // Each call replaces the previous server verdict — pressing Send again with new
        // input swaps the old server errors for the new ones, rather than accumulating
        // them, so a corrected resubmission cannot leave stale errors behind.
        _form!.Engine!.ApplyServerIssues(issues);
        _serverWarnings.AddRange(issues
            .Where(i => i.Severity != ValidationSeverity.Error)
            .Select(i => i.Message));
        _status = "Server rejected the order — its errors are now inline.";
    }
```

*Source: `samples/Formidable.Sample/Pages/ServerRoundTrip.razor.cs`*

Error issues from `ToIssues()` reach the form's fields the moment `ApplyServerIssues` runs — they
bypass the field-registry disclosure check entirely (see
[`docs/disclosure.md`](disclosure.md)), because the server
already validated the submitted data and a field the client happens not to have rendered isn't a
disclosure concern. Non-error issues in the same payload are not applied to any field by the
engine — the page pulls them back out of the same `issues` list itself (`_serverWarnings` above)
and renders them however it chooses; Formidable draws no opinion about where a server warning
belongs on the page.

**Warnings never ride a success response.** The wire contract above only defines the *rejection*
shape — the `warnings` extension exists on a 400 `ValidationProblemDetails` body. Both adapters
skip straight to the framework's ordinary success path when the report has no errors
(`return await next(context)` / `await next()`, shown in the Minimal APIs and MVC sections above)
— the handler's or action's own return value passes through completely untouched, with no
warnings attached, because there is no wire contract for a successful response to carry them.

## Samples

**Samples:** [`/server`](../samples/Formidable.Sample/Pages/ServerRoundTrip.razor) and
[`samples/Formidable.Sample.Api/Program.cs`](../samples/Formidable.Sample.Api/Program.cs) for the
Minimal API endpoint, with its MVC twin at
[`samples/Formidable.Sample.Api/Controllers/OrdersController.cs`](../samples/Formidable.Sample.Api/Controllers/OrdersController.cs)
— the page's endpoint picker posts to either one, with a caption under the radios naming the live
URL so the two otherwise-identical 400s are traceable to their source.
[`samples/Formidable.Sample.Api/requests.http`](../samples/Formidable.Sample.Api/requests.http)
has ready-made requests against both endpoints for use outside the browser. Run the API first
(`dotnet run --project samples/Formidable.Sample.Api`), then open `/server` in the Blazor sample
and press "Send to server" — pressing Enter triggers the browser's implicit form submission,
which runs the client-side submit pipeline this page deliberately skips.
