# Server integration

**You should already know:** why the server needs to run the same validator at all, and roughly
what `ApplyServerIssues` does with what comes back
([Async and server](async-and-server.md)), plus the draft/submit split that decides
which profile a request runs under ([Core concepts](core-concepts.md)).

A form's client-side code is never something a server can trust on its own. A request can skip
the browser entirely, replay old values, or arrive from a client that never ran a single rule.
So the server validates again, every time, no matter how thorough the checks in front of the
user already were. That's the easy half. The harder one is what happens when the server
disagrees. A rejection shaped differently from a client-side one — a raw string with no field
attached, a status code and nothing else — teaches the user nothing they can act on. And a form
that looked fine a second ago suddenly isn't, for reasons the page can't show. Formidable's
server story answers both problems with one move: the same FluentValidation rules and profile
definitions that drive the client run again on the server, and a rejected request comes back in
exactly the shape the client already knows how to apply.

## Need to know

One call wires an endpoint into that same validator, and on the sample's minimal API it looks
like this:

```csharp
// Group-level validation: every endpoint in the group runs the submit profile.
var orders = app.MapGroup("/api/orders").Validate<RoundTripOrder>();
orders.MapPost("/", (RoundTripOrder order) => Results.Ok(new { accepted = true, lines = order.Lines.Count }));
```

*Source: `samples/Formidable.Sample.Api/Program.cs`*

MVC gets the same thing from `[Validate]`, an action filter instead of an endpoint filter. Both
adapters funnel into one wire format, defined once in the dependency-free core package, so
whichever one rejects a request, the shape it sends back is identical. On the client side,
closing the loop is three calls: deserialize the 400 body, flatten it with `ToIssues()`, and
hand the result to `IFormValidationEngine.ApplyServerIssues`. That third call replaces what its
own previous call added rather than piling onto it, so resubmitting the same or a corrected
payload never leaves a stale duplicate error behind. That's the whole authoring surface: pick an
adapter, apply what it sends back. What follows is the wire format underneath both of them, each
adapter's own shape, and the normalize step both run before they validate anything.

## The wire contract

A rejected request returns a 400 `ValidationProblemDetails`. Its `errors` dictionary is keyed by
the same property-path format the client uses internally (`Items[0].Sku`, and so on — see
[Collections and row identity](collections-and-row-identity.md)). An `advisories` extension
alongside `errors` carries every non-error issue from the same report. The client-side shape of
that body is one type in the core `Formidable` package — no ASP.NET Core or Blazor dependency
required to read it:

```csharp
namespace Formidable;

/// <summary>
/// Client-side shape of the validation ProblemDetails body produced by Formidable.AspNetCore:
/// the standard <c>errors</c> dictionary keyed by property path plus an <c>advisories</c>
/// extension for non-error issues. Deserialize an HTTP 400 body into this (web JSON defaults,
/// e.g. <c>ReadFromJsonAsync</c>) and pass <see cref="ToIssues"/> to the Blazor engine's
/// server-issue application.
/// </summary>
public sealed class FormidableValidationProblem
{
    /// <summary>Error messages keyed by property path (standard ValidationProblemDetails shape).</summary>
    public Dictionary<string, string[]> Errors { get; set; } = [];

    /// <summary>Non-error issues from the <c>advisories</c> extension.</summary>
    public List<ValidationProblemAdvisory> Advisories { get; set; } = [];

    /// <summary>
    /// Flattens the payload into engine-ready issues: error entries first (one issue per
    /// message), then advisories with their severity parsed case-insensitively — unknown or
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
        var advisories = Advisories ?? [];

        foreach (var (path, messages) in errors)
        {
            issues.AddRange((messages ?? []).Select(message => new ValidationIssue(path, message)));
        }

        foreach (var advisory in advisories)
        {
            var severity =
                Enum.TryParse<ValidationSeverity>(advisory.Severity, ignoreCase: true, out var parsed)
                && parsed != ValidationSeverity.Error
                    ? parsed
                    : ValidationSeverity.Warning;

            issues.Add(new ValidationIssue(advisory.Path, advisory.Message, severity, advisory.Code, advisory.DisplayName));
        }

        return issues;
    }
}
```

*Source: `src/Formidable/FormidableValidationProblem.cs`*

`ToIssues()` deliberately tolerates a null or hostile payload shape rather than throwing — a
foreign 400 body (a proxy, a gateway, a handwritten test double) can carry explicit JSON nulls
that survive deserialization and override the property initializers above.

```csharp
/// <summary>
/// The wire shape of one non-error issue carried on the <c>advisories</c> extension of a
/// validation ProblemDetails payload. <paramref name="Severity"/> is the
/// <see cref="ValidationSeverity"/> member name as a string ("Warning" or "Info").
/// </summary>
/// <param name="Path">Property path in the client's format, e.g. <c>Items[0].Sku</c>.</param>
/// <param name="Message">Human-readable message.</param>
/// <param name="Severity">Severity name; unknown values are read as Warning.</param>
/// <param name="Code">Optional machine-readable code.</param>
/// <param name="DisplayName">Optional user-facing field name.</param>
public sealed record ValidationProblemAdvisory(
    string Path,
    string Message,
    string Severity,
    string? Code = null,
    string? DisplayName = null);
```

*Source: `src/Formidable/ValidationProblemAdvisory.cs`*

Building the other side of that contract — turning a `ValidationReport` into the two wire pieces
— is one static mapper in `Formidable.AspNetCore`, shared by both server adapters below:

```csharp
namespace Formidable.AspNetCore;

/// <summary>
/// Maps a <see cref="ValidationReport"/> to the wire shape shared with the client:
/// error messages keyed by property path plus non-error issues for the
/// <see cref="AdvisoriesExtensionKey"/> ProblemDetails extension.
/// </summary>
public static class ValidationReportProblemMapper
{
    /// <summary>The ProblemDetails extension key carrying non-error issues.</summary>
    public const string AdvisoriesExtensionKey = "advisories";

    /// <summary>Error messages grouped by path, preserving issue order within each path.</summary>
    public static Dictionary<string, string[]> ToErrorDictionary(ValidationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return report.Errors
            .GroupBy(issue => issue.Path)
            .ToDictionary(group => group.Key, group => group.Select(issue => issue.Message).ToArray());
    }

    /// <summary>Non-error issues as the advisories-extension payload, in issue order.</summary>
    public static List<ValidationProblemAdvisory> ToAdvisories(ValidationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return report.Issues
            .Where(issue => issue.Severity != ValidationSeverity.Error)
            .Select(issue => new ValidationProblemAdvisory(
                issue.Path, issue.Message, issue.Severity.ToString(), issue.Code, issue.DisplayName))
            .ToList();
    }

    // The ProblemDetails extensions dictionary for `report`, keyed under
    // AdvisoriesExtensionKey -- or null when there are no advisories to carry, so a caller can
    // attach it only when non-empty rather than repeating that count check itself. Both server
    // adapters (the minimal-API filter and the MVC action filter) share this one step.
    internal static Dictionary<string, object?>? ToAdvisoriesExtensions(ValidationReport report)
    {
        var advisories = ToAdvisories(report);
        return advisories.Count > 0
            ? new Dictionary<string, object?> { [AdvisoriesExtensionKey] = advisories }
            : null;
    }
}
```

*Source: `src/Formidable.AspNetCore/ValidationReportProblemMapper.cs`*

**Messages can echo user input.** A FluentValidation message built with `{PropertyValue}` embeds
the field's own value into the response body verbatim. Formidable's own components already render
every message as text — `FormidableFieldMessage`, `FormidableCollectionMessage`, and
`FormidableSummary` write it through Blazor's own encoding (`AddContent`, never `MarkupString`) —
so nothing in the kit turns that text into markup. A consumer reading the same
`errors`/`advisories` payload outside Formidable's components needs to do the same: render each
message as text, never interpolate it into HTML.

## Minimal APIs

`Validate<TModel>(profile?)` is an endpoint-filter extension with two overloads — one route
handler at a time, or every handler in a route group at once, the shape the recipe above uses:

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
    /// use the client's path format; non-error issues ride the <c>advisories</c> extension and
    /// never block on their own.
    /// </summary>
    /// <param name="builder">The route handler to validate.</param>
    /// <param name="profile">The profile to run; defaults to <see cref="ValidationProfile.Submit"/>.</param>
    /// <remarks>
    /// Always fails closed, with no silent-skip mode to opt out of: a handler with no
    /// <typeparamref name="TModel"/> parameter at all throws
    /// <see cref="InvalidOperationException"/> the first time the endpoint runs (a wiring bug);
    /// a declared <typeparamref name="TModel"/> parameter bound to <see langword="null"/> — e.g.
    /// a nullable body parameter posted the JSON literal <c>null</c> — returns the standard 400
    /// validation shape with a model-level "A request body is required." error instead, since a
    /// client can trigger that on every request. When the handler declares more than one
    /// parameter of type <typeparamref name="TModel"/>, only the first one is validated.
    /// </remarks>
    public static RouteHandlerBuilder Validate<TModel>(this RouteHandlerBuilder builder, ValidationProfile? profile = null)
        where TModel : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        var resolvedProfile = profile ?? ValidationProfile.Submit;
        return builder.AddEndpointFilterFactory((factoryContext, next) =>
        {
            var filter = new ValidationEndpointFilter<TModel>(resolvedProfile, HasDeclaredParameter<TModel>(factoryContext.MethodInfo));
            return invocationContext => filter.InvokeAsync(invocationContext, next);
        });
    }

    /// <summary>
    /// Normalizes (when <typeparamref name="TModel"/> implements
    /// <see cref="INormalizableModel"/>) and validates the endpoint's
    /// <typeparamref name="TModel"/> argument with the given profile before the handler runs.
    /// Error issues short-circuit to a 400 ValidationProblemDetails whose <c>errors</c> keys
    /// use the client's path format; non-error issues ride the <c>advisories</c> extension and
    /// never block on their own.
    /// </summary>
    /// <param name="builder">The route group to validate.</param>
    /// <param name="profile">The profile to run; defaults to <see cref="ValidationProfile.Submit"/>.</param>
    /// <remarks>
    /// Every endpoint in the group must bind a <typeparamref name="TModel"/>-typed parameter —
    /// checked per endpoint, so one endpoint in the group missing it doesn't affect the rest.
    /// Endpoints without one throw <see cref="InvalidOperationException"/> at request time (a
    /// wiring bug); an endpoint that HAS the parameter but received <see langword="null"/> for
    /// it gets the standard 400 validation shape instead, per the single-handler overload above.
    /// When an endpoint declares more than one parameter of type <typeparamref name="TModel"/>,
    /// only the first one is validated.
    /// </remarks>
    public static RouteGroupBuilder Validate<TModel>(this RouteGroupBuilder builder, ValidationProfile? profile = null)
        where TModel : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        var resolvedProfile = profile ?? ValidationProfile.Submit;
        return builder.AddEndpointFilterFactory((factoryContext, next) =>
        {
            var filter = new ValidationEndpointFilter<TModel>(resolvedProfile, HasDeclaredParameter<TModel>(factoryContext.MethodInfo));
            return invocationContext => filter.InvokeAsync(invocationContext, next);
        });
    }

    // Checked once per endpoint at filter-build time (EndpointFilterFactoryContext.MethodInfo is
    // the endpoint's own handler, even for a filter attached at the group level) rather than at
    // every request, so ValidationEndpointFilter<TModel> can tell "no argument of this type was
    // ever declared" (throw — a wiring bug) apart from "the declared argument was bound null"
    // (400 — a client can trigger this on every request) without re-reflecting per request.
    // Assignability, not exact-type equality: a handler may declare a MORE DERIVED parameter
    // type than TModel, and InvokeAsync's own retrieval (context.Arguments.OfType<TModel>())
    // already treats that as a match — an exact-type check here would disagree and misreport a
    // declared-but-null derived parameter as "no parameter of this type at all".
    private static bool HasDeclaredParameter<TModel>(MethodInfo methodInfo) =>
        methodInfo.GetParameters().Any(p => typeof(TModel).IsAssignableFrom(p.ParameterType));
}
```

*Source: `src/Formidable.AspNetCore/FormidableEndpointFilterExtensions.cs`*

Both overloads default to `ValidationProfile.Submit` and install the same filter — the group
overload just attaches it to every endpoint the group defines, checking each one's own handler
for a `TModel` parameter at filter-build time rather than sharing one answer across the whole
group. That answer decides which of two very different outcomes a null model gets:

```csharp
namespace Formidable.AspNetCore;

/// <summary>
/// Runs normalize + profile validation for one endpoint argument type. Always fails closed: a
/// declared parameter bound to null 400s, a genuinely absent parameter or an unresolvable
/// <see cref="IModelValidator{TModel}"/> throws.
/// </summary>
internal sealed class ValidationEndpointFilter<TModel> : IEndpointFilter
    where TModel : class
{
    private readonly ValidationProfile _profile;
    private readonly bool _hasDeclaredParameter;

    public ValidationEndpointFilter(ValidationProfile profile, bool hasDeclaredParameter)
    {
        _profile = profile;
        _hasDeclaredParameter = hasDeclaredParameter;
    }

    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var model = context.Arguments.OfType<TModel>().FirstOrDefault();
        if (model is null)
        {
            if (!_hasDeclaredParameter)
            {
                // No parameter of this type exists on the endpoint at all — a wiring bug (e.g. a
                // group-validated endpoint that never declared the argument), not something a
                // client can trigger by shaping a request.
                throw new InvalidOperationException(
                    $"Validate<{FriendlyTypeName.Of(typeof(TModel))}>() found no endpoint argument of that type — there is nothing to validate.");
            }

            // A parameter of this type IS declared on the handler but bound to null — e.g. a
            // nullable body parameter posted the JSON literal `null`. Any anonymous client can
            // trigger this on every request, so it gets the standard 400 validation shape
            // instead of an exception.
            var missingBody = new ValidationReport([new ValidationIssue(string.Empty, "A request body is required.")]);
            return TypedResults.ValidationProblem(ValidationReportProblemMapper.ToErrorDictionary(missingBody));
        }

        (model as INormalizableModel)?.Normalize();

        var validator = context.HttpContext.RequestServices.GetRequiredService<IModelValidator<TModel>>();
        var report = await validator.ValidateAsync(model, _profile, context.HttpContext.RequestAborted);

        if (report.IsValid)
        {
            return await next(context);
        }

        return TypedResults.ValidationProblem(
            ValidationReportProblemMapper.ToErrorDictionary(report),
            extensions: ValidationReportProblemMapper.ToAdvisoriesExtensions(report));
    }
}
```

*Source: `src/Formidable.AspNetCore/ValidationEndpointFilter.cs`*

Two different reasons produce the same `model is null`, and they get two different responses. A
group-validated endpoint with no `TModel`-typed argument at all is a wiring bug, not something a
request can influence, so it still fails every request with an `InvalidOperationException` rather
than a silent skip. A declared `TModel` argument bound to `null` (a nullable body parameter posted
the JSON literal `null`) is something any anonymous client can trigger on every request, so that
gets the standard 400 validation shape instead: a model-level `"A request body is required."`
error, using the same `ValidationReportProblemMapper.ToErrorDictionary` mapping every other
rejection in this document uses, not a one-off shape. Neither filter has a discovery mode to
silently skip a resolvable-but-unregistered validator either:
`GetRequiredService<IModelValidator<TModel>>()` throws on its own if `AddFormidable()` was never
called, so there is no equivalent to `[Validate]`'s `RequireValidator` needed here (see below).
`report.IsValid` is `true` whenever the report has no error-severity issues; warnings and infos
don't affect it (see [Severity](severity.md)). That is why an all-warnings report falls straight
through to `next(context)` and the handler's own return value, unmodified.

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
        var profile = ValidationProfile.FromName(Profile);
        var services = context.HttpContext.RequestServices;
        var issues = new List<ValidationIssue>();
        var validatedAny = false;
        var boundAnyArgument = false;

        foreach (var (name, argument) in context.ActionArguments)
        {
            if (argument is null)
            {
                continue;
            }

            boundAnyArgument = true;

            var argumentType = ResolveValidatedType(context.ActionDescriptor, name, argument, services);
            if (argumentType is null)
            {
                continue;
            }

            validatedAny = true;
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

        if (RequireValidator && boundAnyArgument && !validatedAny)
        {
            throw new InvalidOperationException(BuildRequireValidatorMessage(context));
        }

        var aggregate = new ValidationReport(issues);
        if (!aggregate.IsValid)
        {
            var problem = new ValidationProblemDetails(ValidationReportProblemMapper.ToErrorDictionary(aggregate))
            {
                Status = StatusCodes.Status400BadRequest,
                Type = "https://tools.ietf.org/html/rfc9110#section-15.5.1"
            };

            var extensions = ValidationReportProblemMapper.ToAdvisoriesExtensions(aggregate);
            if (extensions is not null)
            {
                foreach (var (key, value) in extensions)
                {
                    problem.Extensions[key] = value;
                }
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

`null` arguments are skipped entirely — neither normalized nor validated — before the aggregate's
`IsValid` gate runs once, after the loop; a `null` argument does not count toward
`RequireValidator`'s "did this action bind anything at all" check below. Every non-null argument's
type is resolved by `ResolveValidatedType`:

```csharp
    private Type? ResolveValidatedType(ActionDescriptor actionDescriptor, string parameterName, object argument, IServiceProvider services)
    {
        var declaredType = DeclaredParameterType(actionDescriptor, parameterName) ?? argument.GetType();
        if (ShouldValidate(declaredType, services))
        {
            return declaredType;
        }

        var runtimeType = argument.GetType();
        return runtimeType != declaredType && ShouldValidate(runtimeType, services) ? runtimeType : null;
    }
```

*Source: `src/Formidable.AspNetCore/ValidateAttribute.cs`*

`declaredType` there is the action parameter's DECLARED type (looked up on
`context.ActionDescriptor.Parameters` by argument name), not the argument's own runtime type, and
it is tried FIRST: when it has a registered validator, it wins outright. This matters under
polymorphic model binding: `[FromBody] Order order` paired with a
`[JsonPolymorphic]`/`[JsonDerivedType]` hierarchy can bind a *derived* instance to `order` while
the parameter itself stays declared as the base `Order`. Resolving by that derived runtime type
FIRST would let a hostile `$type` pick an unregistered derived type and skip validation entirely,
even though the base type it's declared as has a validator — the declared type winning outright
closes that hole. Only when the declared type resolves no validator does resolution fall back to
probing the argument's own runtime type, so a validator registered only for a derived type still
runs; when the action descriptor carries no matching declared parameter at all (e.g. a hand-built
`ActionDescriptor` outside MVC's own pipeline), the runtime type is used directly, with no
declared type to prefer. The one residual this order leaves: a derived-only validator
registration, or an unregistered `$type`, combined with no validator for the declared type either,
still skips the argument silently — see Strict validator resolution below for the way to turn that
into a thrown misconfiguration instead. The minimal-API side gives derived instances the same
base-type guarantee for a different reason: `ValidationEndpointFilter<TModel>`'s `OfType<TModel>`
above treats `TModel` as fixed at the call site rather than probed from the argument, so there's
no runtime type to steer in the first place.

### Strict validator resolution

`RequireValidator` (default `false`) makes `[Validate]` throw `InvalidOperationException`,
naming the argument type(s) it considered and how to register a validator for them, when the
action bound at least one non-null argument but validated none of them. Off by default: an action
mixing validatable models with ordinary parameters (route values, query strings, injected
services) legitimately validates nothing on a request with no model argument, and that is not a
misconfiguration worth failing loudly over. A request that binds nothing at all — a null or empty
body for a nullable parameter — is likewise not a misconfiguration, since any anonymous client can
trigger it, so `RequireValidator` stays silent for that case too: it only fires when something was
actually bound and none of it validated. Turn it on once every argument on an action *should*
carry a validator, so a lost registration — a refactor that silently drops
`AddValidatorsFromAssembly()`, or an explicit constructor type (`[Validate(typeof(Order))]`) that
no longer matches any parameter — announces itself as a 500 instead of quietly validating
nothing. It is also the recommended setting for any endpoint that accepts polymorphic model
binding, turning the declared-type/runtime-type resolution's residual (above) into a thrown
misconfiguration rather than a silent skip.

### Profile string mapping

`[Validate]`'s `Profile` property is a string (`"Submit"` by default), resolved once per request
against the same two conventional profiles the client uses (see [Profiles](profiles.md)) through
`ValidationProfile.FromName` — the one caller-facing entry point for a profile carried as a
string, shared by any other string-typed configuration surface too:

```csharp
    public static ValidationProfile FromName(string name)
    {
        if (string.Equals(name, "Draft", StringComparison.OrdinalIgnoreCase))
        {
            return Draft;
        }

        if (string.Equals(name, "Submit", StringComparison.OrdinalIgnoreCase))
        {
            return Submit;
        }

        return Named(name, includeDefaultRules: true, name);
    }
```

*Source: `src/Formidable/ValidationProfile.cs`*

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

Both `ValidationEndpointFilter<TModel>` and `[Validate]` call
`(model as INormalizableModel)?.Normalize()` in place, on the exact instance the framework
already deserialized and bound — before that instance is handed to `ValidateAsync`.
`Normalize()` mutates the object rather than returning a copy. So the same instance the request
model binder produced is what the validator checks and, if validation passes, what the route
handler or action ultimately runs against. The handler sees the normalized model, not the
wire-deserialized one.

## Client round-trip

`IFormValidationEngine.ApplyServerIssues` documents its own contract in full:

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
    /// Because the payload is treated as a submit result, applying one also sets
    /// <see cref="HasSubmitted"/> — a page whose only validation is server-side reaches the
    /// submitted state through this call alone. Call from the renderer's synchronization context (a
    /// Blazor event handler or <c>InvokeAsync</c>) — it mutates validation state and triggers
    /// renders. <paramref name="issues"/> is enumerated exactly once.
    /// </summary>
    /// <remarks>
    /// Replace is value-equality-based: if a client-sourced issue on a field is value-identical
    /// to a server issue previously applied to that field, a subsequent replace may remove either
    /// of the two equal entries — the two are indistinguishable, so which one is removed is
    /// unspecified.
    /// </remarks>
    void ApplyServerIssues(IEnumerable<ValidationIssue> issues);
```

*Source: `src/Formidable.Blazor/IFormValidationEngine.cs`*

**Replace, not accumulate.** Each call is the server's current verdict, full stop — it undoes
exactly what its own previous call added, then applies the new payload. Calling it twice in a
row with the same or a corrected body never leaves a stale duplicate inline error behind. Only
error-severity issues in the payload are applied to fields; anything else in the payload is
ignored by the engine entirely. That is why the page, not the engine, is responsible for
presenting the advisories a 400 carries.

The sample deliberately skips client-side submit validation so the round-trip is visible end to
end — press Send and the server's 400 lands on the exact fields:

```csharp
    private async Task Send()
    {
        // Normalizing before the POST keeps the client's line list identical to what the
        // server validates (its filter normalizes too) - so issue paths always match rows.
        _order.Normalize();
        _serverAdvisories.Clear();
        var response = await Http.PostAsJsonAsync(_endpoint, _order);

        if (response.IsSuccessStatusCode)
        {
            _status = "Server accepted the order.";
            return;
        }

        var problem = await response.Content.ReadFromJsonAsync<FormidableValidationProblem>();
        var issues = problem!.ToIssues();

        // The engine applies error-severity issues to fields; non-error issues are the
        // page's to present (a 400's advisories ride alongside its errors by contract).
        // Each call replaces the previous server verdict — pressing Send again with new
        // input swaps the old server errors for the new ones, rather than accumulating
        // them, so a corrected resubmission cannot leave stale errors behind.
        _form!.ApplyServerIssues(issues);
        _serverAdvisories.AddRange(issues
            .Where(i => i.Severity != ValidationSeverity.Error)
            .Select(i => i.Message));
        _status = "Server rejected the order — its errors are now inline.";
    }
```

*Source: `samples/Formidable.Sample/Pages/ServerRoundTrip.razor.cs`*

Error issues from `ToIssues()` reach the form's fields the moment `ApplyServerIssues` runs,
bypassing the field-registry disclosure check entirely (see [Disclosure](disclosure.md)). The
server already validated the submitted data, so a field the client happens not to have rendered
isn't a disclosure concern. Non-error issues in the same payload are not applied to any field by
the engine. The page pulls them back out of the same `issues` list itself (`_serverAdvisories`
above) and renders them however it chooses. Formidable draws no opinion about where a server
advisory belongs on the page.

**Advisories never ride a success response.** The wire contract above only defines the *rejection*
shape — the `advisories` extension exists on a 400 `ValidationProblemDetails` body. Both adapters
skip straight to the framework's ordinary success path when the report has no errors
(`return await next(context)` / `await next()`, shown in the Minimal APIs and MVC sections
above). The handler's or action's own return value passes through completely untouched, with no
advisories attached, because there is no wire contract for a successful response to carry them.

**Collection sizes are the host's job, not the validator's.** Both sample endpoints validate
whatever collection a client sends without capping how large it can get — the request body's size
limit is the only ceiling on `RoundTripOrder.Lines` or
[`/workout`](../samples/Formidable.Sample/Pages/Workout.razor)'s `EventRegistration.Attendees`,
and that page's own attendee-count rule is `Warning` severity (see [Severity](severity.md)), so it
never blocks a submit by itself. A real API should add an error-severity cap alongside the format
rules already in place:

```csharp
RuleFor(x => x.Attendees).Must(a => a.Count <= 100).WithMessage("Too many attendees in one request");
```

Pair it with a request-size limit at the transport level too — Kestrel's `MaxRequestBodySize`, or
the equivalent on a reverse proxy in front of it — since the rule above only runs once the body
has already been deserialized.

**Samples:** [`/server`](../samples/Formidable.Sample/Pages/ServerRoundTrip.razor) and
[`samples/Formidable.Sample.Api/Program.cs`](../samples/Formidable.Sample.Api/Program.cs) for
the Minimal API endpoint, with its MVC twin at
[`samples/Formidable.Sample.Api/Controllers/OrdersController.cs`](../samples/Formidable.Sample.Api/Controllers/OrdersController.cs).
The page's endpoint picker posts to either one, with a caption under the radios naming the live
URL, so the two otherwise-identical 400s are traceable to their source.
[`samples/Formidable.Sample.Api/requests.http`](../samples/Formidable.Sample.Api/requests.http)
has ready-made requests against both endpoints for use outside the browser. Run the API first
(`dotnet run --project samples/Formidable.Sample.Api`), then open `/server` in the Blazor sample
and press "Send to server". Pressing Enter triggers the browser's implicit form submission,
which runs the client-side submit pipeline this page deliberately skips.
