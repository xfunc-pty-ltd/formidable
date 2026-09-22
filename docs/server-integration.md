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
whichever one rejects a request, the same paths carry the same messages and the `advisories`
extension is identical. Each then builds the ProblemDetails around them the way its own half of
the framework does — the endpoint filter through `TypedResults.ValidationProblem`, the action
filter through the app's `ProblemDetailsFactory` — and three differences follow from that, all of
them the framework's rather than Formidable's. The action filter's `errors` reach the response
through a `ModelStateDictionary`, which is a prefix trie, so it **orders its keys its own way**
where the endpoint filter serves them in report order; a `ModelStateDictionary` also **substitutes
its own text for an empty message**, so an issue carrying none arrives as `""` from the endpoint
filter and as a sentence of MVC's from the action filter; and MVC's factory writes a **`traceId`
whatever the host configured**, where `TypedResults.ValidationProblem` writes one only under
`AddProblemDetails()`. A client keying on paths and reading messages is unaffected by all three.
On the client side,
closing the loop is two calls: deserialize the 400 body, and hand it to
`FormidableForm.ApplyServerIssues`. That second call applies the server's verdict at the severity
it carries — errors block and mark their fields `formidable-invalid`, and warnings and infos land
as advisories that paint `formidable-warning` or `formidable-info` once the field has been touched
or modified — and it replaces what its own previous call applied rather
than piling onto it, so resubmitting the same or a corrected payload never leaves a stale
duplicate behind. That's the whole authoring surface: pick an adapter, apply what it sends back.
What follows is the wire format underneath both of them, each adapter's own shape, what a
handler can read back from a passing report, and the normalize step both run before
they validate anything.

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

        // A foreign 400 body can carry explicit JSON nulls anywhere its shape admits one — the
        // collections themselves, a key's message array, an element inside that array, a whole
        // advisory entry, or an advisory's fields (the deserializer doesn't enforce
        // nullable-reference annotations) — tolerate every shape rather than throw. A null
        // advisory entry carries nothing to show, so it is skipped; a null path reads as ""
        // (the model-level path); a null message reads as "".
        var errors = Errors ?? new Dictionary<string, string[]>();
        var advisories = Advisories ?? [];

        foreach (var (path, messages) in errors)
        {
            issues.AddRange((messages ?? []).Select(message => new ValidationIssue(path, message ?? string.Empty)));
        }

        foreach (var advisory in advisories)
        {
            if (advisory is null)
            {
                continue;
            }

            // Enum.TryParse admits a numeric string ("99", "-1") and a comma-joined list
            // ("Info, Warning") as readily as a member name, so the parse alone would let a
            // foreign body name a severity no member defines — one that then reaches every
            // severity switch and the field-state class provider as an advisory of no band.
            // IsDefined is what keeps "unknown reads as Warning" true of every unknown.
            var severity =
                Enum.TryParse<ValidationSeverity>(advisory.Severity, ignoreCase: true, out var parsed)
                && Enum.IsDefined(parsed)
                && parsed != ValidationSeverity.Error
                    ? parsed
                    : ValidationSeverity.Warning;

            issues.Add(new ValidationIssue(
                advisory.Path ?? string.Empty,
                advisory.Message ?? string.Empty,
                severity,
                advisory.Code,
                advisory.DisplayName));
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

    /// <summary>
    /// Error messages grouped by path, preserving issue order within each path. A
    /// <see langword="null"/> path reads as <c>""</c>, the model-level path, and a
    /// <see langword="null"/> message as <c>""</c> — the same tolerance
    /// <see cref="FormidableValidationProblem.ToIssues"/> applies on the client, because
    /// <see cref="IModelValidator{TModel}"/> is a consumer-implementable seam and a hand-rolled
    /// one can hand back either however the type is annotated.
    /// </summary>
    public static Dictionary<string, string[]> ToErrorDictionary(ValidationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return report.Errors
            .GroupBy(issue => issue.Path ?? string.Empty)
            .ToDictionary(group => group.Key, group => group.Select(issue => issue.Message ?? string.Empty).ToArray());
    }

    /// <summary>
    /// Non-error issues as the advisories-extension payload, in issue order. Null paths and
    /// messages are tolerated exactly as in <see cref="ToErrorDictionary"/>.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// An issue carries a <see cref="ValidationSeverity"/> value no member defines. The wire
    /// field is a member NAME, so there is nothing honest to write: the value's own
    /// <c>ToString</c> would put a number there, which the client reads as
    /// <see cref="ValidationSeverity.Warning"/> — relabelling a caller's bug rather than
    /// reporting it. Nothing shipped can produce one (the FluentValidation adapter maps
    /// exhaustively), so the value comes from a cast in a hand-rolled validator, and naming it
    /// is the only way its author learns of it.
    /// </exception>
    public static List<ValidationProblemAdvisory> ToAdvisories(ValidationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return report.Issues
            .Where(issue => issue.Severity != ValidationSeverity.Error)
            .Select(issue => new ValidationProblemAdvisory(
                issue.Path ?? string.Empty,
                issue.Message ?? string.Empty,
                SeverityName(issue, nameof(report)),
                issue.Code,
                issue.DisplayName))
            .ToList();
    }

    private static string SeverityName(ValidationIssue issue, string parameterName) =>
        Enum.IsDefined(issue.Severity)
            ? issue.Severity.ToString()
            : throw new ArgumentException(
                $"Issue '{issue.Path}' carries severity {(int)issue.Severity}, which no ValidationSeverity member " +
                "defines. The advisories extension carries a member name, so there is no name to write — give the " +
                "issue Error, Warning or Info.",
                parameterName);

    // The ProblemDetails extensions dictionary for `report`, keyed under
    // AdvisoriesExtensionKey -- or null when there are no advisories to carry, so a caller can
    // attach it only when non-empty rather than repeating that count check itself. Both server
    // adapters (the minimal-API filter and the MVC action filter) share this one step, so
    // ToAdvisories' rejection of an undefined severity is a rejection on both: a report carrying
    // one fails the request it was built for, whichever adapter is serving it.
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
every message as text — `FormidableFieldMessage`, `FormidableCollectionMessage`,
`FormidableModelMessage`, and `FormidableSummary` write it through Blazor's own encoding
(`AddContent`, never `MarkupString`) — so nothing in the kit turns that text into markup. A
consumer reading the same `errors`/`advisories` payload outside Formidable's components needs to
do the same: render each message as text, never interpolate it into HTML.

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
    /// use the client's path format and whose <c>advisories</c> extension carries the report's
    /// non-error issues. Warnings and infos never block on their own: a report carrying only
    /// them passes through to the handler, which can read it via
    /// <see cref="FormidableHttpContextExtensions.GetFormidableValidationReport"/>.
    /// </summary>
    /// <param name="builder">The route handler to validate.</param>
    /// <param name="profile">The profile to run; defaults to <see cref="ValidationProfile.Submit"/>.</param>
    /// <remarks>
    /// Always fails closed, with no silent-skip mode to opt out of: a handler with no
    /// <typeparamref name="TModel"/> parameter at all throws
    /// <see cref="InvalidOperationException"/> when the endpoint's request pipeline is built (a
    /// wiring bug) — routing materializes every mapped endpoint before it can match any
    /// request, so the throw fails every request to the application, loudly, rather than hiding
    /// as a 500 on the one broken route; a declared <typeparamref name="TModel"/> parameter
    /// bound to <see langword="null"/> — e.g. a nullable body parameter posted the JSON literal
    /// <c>null</c> — returns the standard 400 validation shape with a model-level "A request
    /// body is required." error instead, since a client can trigger that on every request. When
    /// the handler declares more than one parameter of type <typeparamref name="TModel"/>, only
    /// the first one is validated.
    /// </remarks>
    public static RouteHandlerBuilder Validate<TModel>(this RouteHandlerBuilder builder, ValidationProfile? profile = null)
        where TModel : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        var resolvedProfile = profile ?? ValidationProfile.Submit;
        return builder.AddEndpointFilterFactory((factoryContext, next) =>
        {
            ThrowIfNoDeclaredParameter<TModel>(factoryContext.MethodInfo);
            var filter = new ValidationEndpointFilter<TModel>(resolvedProfile);
            return invocationContext => filter.InvokeAsync(invocationContext, next);
        });
    }

    /// <summary>
    /// Normalizes (when <typeparamref name="TModel"/> implements
    /// <see cref="INormalizableModel"/>) and validates the endpoint's
    /// <typeparamref name="TModel"/> argument with the given profile before the handler runs.
    /// Error issues short-circuit to a 400 ValidationProblemDetails whose <c>errors</c> keys
    /// use the client's path format and whose <c>advisories</c> extension carries the report's
    /// non-error issues. Warnings and infos never block on their own: a report carrying only
    /// them passes through to the handler, which can read it via
    /// <see cref="FormidableHttpContextExtensions.GetFormidableValidationReport"/>.
    /// </summary>
    /// <param name="builder">The route group to validate.</param>
    /// <param name="profile">The profile to run; defaults to <see cref="ValidationProfile.Submit"/>.</param>
    /// <remarks>
    /// Every endpoint in the group must bind a <typeparamref name="TModel"/>-typed parameter —
    /// checked endpoint by endpoint, and an endpoint without one throws
    /// <see cref="InvalidOperationException"/> when its request pipeline is built (a wiring
    /// bug), which fails route materialization as a whole: a group carrying a mis-wired
    /// endpoint fails every request to the application, loudly, rather than leaving that one
    /// endpoint to 500 among working siblings. An endpoint that HAS the parameter but received
    /// <see langword="null"/> for it gets the standard 400 validation shape instead, per the
    /// single-handler overload above. When an endpoint declares more than one parameter of type
    /// <typeparamref name="TModel"/>, only the first one is validated.
    /// </remarks>
    public static RouteGroupBuilder Validate<TModel>(this RouteGroupBuilder builder, ValidationProfile? profile = null)
        where TModel : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        var resolvedProfile = profile ?? ValidationProfile.Submit;
        return builder.AddEndpointFilterFactory((factoryContext, next) =>
        {
            ThrowIfNoDeclaredParameter<TModel>(factoryContext.MethodInfo);
            var filter = new ValidationEndpointFilter<TModel>(resolvedProfile);
            return invocationContext => filter.InvokeAsync(invocationContext, next);
        });
    }

    // Checked once per endpoint at filter-build time (EndpointFilterFactoryContext.MethodInfo is
    // the endpoint's own handler, even for a filter attached at the group level) rather than at
    // every request. Throwing here, while the endpoint's request pipeline is being built, means
    // "no argument of this type was ever declared" (a wiring bug no request shape can influence)
    // cannot hide as a 500 on one rarely-hit route: routing materializes every mapped endpoint
    // before it can match any request, so the mis-wiring fails every request to the application
    // until it is fixed. It also leaves ValidationEndpointFilter<TModel> free to read a null
    // argument as exactly one thing: "the declared argument was bound null" (400 — a client can
    // trigger this on every request).
    // Assignability, not exact-type equality: a handler may declare a MORE DERIVED parameter
    // type than TModel, and InvokeAsync's own retrieval (context.Arguments.OfType<TModel>())
    // already treats that as a match — an exact-type check here would disagree and misreport a
    // declared-but-null derived parameter as "no parameter of this type at all".
    private static void ThrowIfNoDeclaredParameter<TModel>(MethodInfo methodInfo)
    {
        if (!methodInfo.GetParameters().Any(p => typeof(TModel).IsAssignableFrom(p.ParameterType)))
        {
            throw new InvalidOperationException(
                $"Validate<{FriendlyTypeName.Of(typeof(TModel))}>() found no endpoint argument of that type — there is nothing to validate.");
        }
    }
}
```

*Source: `src/Formidable.AspNetCore/FormidableEndpointFilterExtensions.cs`*

Both overloads default to `ValidationProfile.Submit` and install the same filter — the group
overload just attaches it to every endpoint the group defines, checking each handler's own
signature for a `TModel` parameter rather than sharing one answer across the whole group. An
endpoint that fails that check never gets a filter at all: the factory throws instead of
building one, so a null model at request time can only mean one thing:

```csharp
namespace Formidable.AspNetCore;

/// <summary>
/// Runs normalize + profile validation for one endpoint argument type, stashing the computed
/// report on the request for <see cref="FormidableHttpContextExtensions.GetFormidableValidationReport"/>.
/// Always fails closed: a declared parameter bound to null 400s, and an unresolvable
/// <see cref="IModelValidator{TModel}"/> throws. An endpoint with no parameter of this type at
/// all never reaches the filter — the endpoint filter factory in
/// <see cref="FormidableEndpointFilterExtensions"/> throws for it while the endpoint's request
/// pipeline is being built.
/// </summary>
internal sealed class ValidationEndpointFilter<TModel> : IEndpointFilter
    where TModel : class
{
    private readonly ValidationProfile _profile;

    public ValidationEndpointFilter(ValidationProfile profile)
    {
        _profile = profile;
    }

    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var model = context.Arguments.OfType<TModel>().FirstOrDefault();
        if (model is null)
        {
            // The filter factory refuses endpoints with no parameter of this type, so null here
            // can only mean the declared parameter was bound to null — e.g. a nullable body
            // parameter posted the JSON literal `null`. Any anonymous client can trigger this
            // on every request, so it gets the standard 400 validation shape instead of an
            // exception.
            var missingBody = new ValidationReport([new ValidationIssue(string.Empty, "A request body is required.")]);
            return TypedResults.ValidationProblem(ValidationReportProblemMapper.ToErrorDictionary(missingBody));
        }

        (model as INormalizableModel)?.Normalize();

        var validator = context.HttpContext.RequestServices.GetRequiredService<IModelValidator<TModel>>();
        var report = await validator.ValidateAsync(model, _profile, context.HttpContext.RequestAborted);

        // Stashed before the 400/pass-through decision so the request can always read the
        // verdict a validator produced: the handler composes a "saved, but note…" 200 from a
        // passing report's advisories, and middleware reads a rejection's full severity detail
        // without parsing the response body.
        context.HttpContext.Items[FormidableHttpContextExtensions.ValidationReportKey] = report;

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

A missing parameter and a null-bound one are different problems, and they surface at different
moments. An endpoint with no `TModel`-typed argument at all is a wiring bug, not something a
request can influence, so the factory refuses it with an `InvalidOperationException` when the
endpoint's request pipeline is built. Routing materializes every mapped endpoint before it can
match any request, which makes the refusal loud on purpose: one mis-wired endpoint fails every
request to the application until it is fixed, rather than hiding as a 500 on the one broken
route. A declared `TModel` argument bound to `null` (a nullable body parameter posted the JSON
literal `null`) is something any anonymous client can trigger on every request, so it is
answered at request time with the standard 400 validation shape: a model-level `"A request body
is required."` error, using the same `ValidationReportProblemMapper.ToErrorDictionary` mapping
every other rejection in this document uses, not a one-off shape. Neither filter has a discovery
mode to silently skip a resolvable-but-unregistered validator either:
`GetRequiredService<IModelValidator<TModel>>()` throws on its own if `AddFormidable()` was never
called, so there is no equivalent to `[Validate]`'s `RequireValidator` needed here (see below).
`report.IsValid` is `true` whenever the report has no error-severity issues; warnings and infos
don't affect it (see [Severity](severity.md)). That is why an all-warnings report falls straight
through to `next(context)` and the handler's own return value, unmodified — though the report
itself is not lost: [Returning warnings beside a 200](#returning-warnings-beside-a-200) below
shows the handler reading it back.

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
                when (services.GetService(typeof(FluentValidation.IValidator<>).MakeGenericType(argumentType)) is null)
            {
                // The open-generic adapter is registered but the validator it wraps is not, so
                // resolving it throws during activation rather than returning null. Reached
                // through the explicit-types path, which names the type instead of probing for
                // a validator — the discovery path cannot get here, because ShouldValidate only
                // returns true for a type whose IValidator<T> it just found.
                throw new InvalidOperationException(MissingFluentValidatorMessage.For(argumentType), ex);
            }
            catch (InvalidOperationException ex)
            {
                // A registered IValidator<T> contradicts the diagnosis above, so this failure
                // has some other cause — the adapter itself was never wired up, almost always
                // because AddFormidable() was never called.
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

        if (validatedAny)
        {
            // Stashed before the 400/pass-through decision so the request can always read the
            // verdict the validators produced. Gated on validatedAny: when nothing was
            // validated, the accessor answers null rather than serving an empty report that
            // implies rules ran and passed.
            context.HttpContext.Items[FormidableHttpContextExtensions.ValidationReportKey] = aggregate;
        }

        if (!aggregate.IsValid)
        {
            var problem = BuildProblem(context.HttpContext, aggregate);

            var extensions = ValidationReportProblemMapper.ToAdvisoriesExtensions(aggregate);
            if (extensions is not null)
            {
                foreach (var (key, value) in extensions)
                {
                    problem.Extensions[key] = value;
                }
            }

            // The media type is set outright rather than left to BadRequestObjectResult's
            // implicit content negotiation, which is what makes this a problem+json response
            // whatever the request's Accept header asks for.
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
`RequireValidator`'s "did this action bind anything at all" check below. `BuildProblem`, just out
of view above, is where the errors become a response: it copies the mapper's dictionary into a
`ModelStateDictionary` — lifting that dictionary's default error cap, since a validation report
legitimately runs to one issue per collection row — and hands it to the app's own
`ProblemDetailsFactory`, which is what `ControllerBase.ValidationProblem()` uses. That is where
this filter's 400 picks up the trace identifier, the `ApiBehaviorOptions.ClientErrorMapping` type
link and any consumer factory's own additions, and it is the hop the three differences at the top
of this page come from. The merge is
deliberately flat: the 400's `errors` dictionary keys each issue by its own property path with
no per-argument prefix, so two validated models sharing a property name land under one key. The
stash just above the `IsValid` gate is what `GetFormidableValidationReport` reads back: the
aggregate, whether the request went on to a 400 or to the action (see
[Returning warnings beside a 200](#returning-warnings-beside-a-200)). Every non-null argument's
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
That is the whole reason the two entry points name the same thing in two types: an attribute
argument has to be a compile-time constant, and a `ValidationProfile` is a value built at runtime.
So `[Validate(Profile = "Submit")]` and `Validate<Order>(ValidationProfile.Submit)` select exactly
the same rules — read the string as the name of the profile the value names directly, and the same
pairing holds for `"Draft"` and for any custom profile name.

## Returning warnings beside a 200

A report with no errors never blocks, so the wire contract above has nothing to say about its
warnings and infos: the handler's own response goes out untouched. The report is not gone,
though. Both adapters stash the report the moment validation computes it, before the
400/pass-through decision, and `GetFormidableValidationReport` reads it back anywhere the
`HttpContext` is in reach — most usefully inside the handler a passing request went on to run:

```csharp
var orders = app.MapGroup("/api/orders").Validate<RoundTripOrder>();
orders.MapPost("/", (RoundTripOrder order, HttpContext http) =>
{
    // Behind Validate<RoundTripOrder>() the report is always present here: the filter
    // validated before the handler could run.
    var report = http.GetFormidableValidationReport()!;
    return Results.Ok(new
    {
        accepted = true,
        lines = order.Lines.Count,
        // The same advisory shape the 400 path puts on its `advisories` extension, so the
        // client parses a "saved, but note…" 200 with the type it already has.
        advisories = ValidationReportProblemMapper.ToAdvisories(report)
    });
});
```

The `ToAdvisories` call is the point of the composition: it is the same public mapping the 400
path uses, so a "saved, but note…" response carries its warnings in the exact wire shape the
client already parses into `FormidableValidationProblem.Advisories`. An MVC action does the same
through its own `HttpContext` property; for `[Validate]` the report is the aggregate across
every validated argument. On a request no Formidable adapter validated — an endpoint outside
the filter, an action whose arguments resolved no validator — the accessor returns `null`, so
code shared across both kinds of route checks before reading.

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
/// <remarks>
/// Implemented by consumer models, so it grows accordingly: a member added after v1 carries a
/// default implementation, and a model that does not override it keeps compiling with its
/// normalization unchanged — the default clears nothing the model's own
/// <see cref="Normalize"/> does not.
/// </remarks>
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
    /// submit results: the server's verdict applies at the severity it carries. Errors land on
    /// their fields and reach the EditContext's message store; warnings and infos land as
    /// advisories, which the reads above surface and the store — an error-only surface — does not.
    /// The payload is treated as the server's CURRENT verdict: it replaces the server's previous
    /// one outright rather than accumulating with it, so re-submitting the same or a corrected
    /// payload does not duplicate inline messages. The server's issues are held apart from the
    /// client's own, so a replace cannot disturb a client-sourced issue on the same field, and an
    /// advisory whose message a client rule already disclosed for that field shows once, as the
    /// client's copy. Applying is itself a disclosure event for the fields it names: a client
    /// error the last submit computed but had nowhere to show surfaces alongside the server's.
    /// The server's verdict stands until a newer whole-model answer supersedes it — the next
    /// debounced refresh, the next submit, or a page saying what its freshly loaded values have
    /// earned through <see cref="DiscloseLoadedValuesAsync"/> — at which point a server-only
    /// issue with no matching client rule goes, while one a client rule agrees with keeps showing
    /// through the client's own answer. Because the payload is treated as a submit result,
    /// applying one also sets <see cref="HasSubmitted"/> — a page whose only validation is
    /// server-side reaches the submitted state through this call alone — and it clears any
    /// standing incomplete-validation fault, whatever the client is doing: a verdict has arrived
    /// to stand in for the one a faulted pass could not finish. Call from the renderer's
    /// synchronization context (a Blazor event handler or <c>InvokeAsync</c>) — it mutates
    /// validation state and triggers renders. <paramref name="issues"/> is enumerated exactly
    /// once.
    /// </summary>
    /// <remarks>
    /// Errors bypass the field registry: the server judged what was actually submitted, so an error
    /// shows whether or not the client rendered its field, and only a disclosure override returning
    /// <see langword="false"/> hides one. Advisories defer to the registry exactly as the client's
    /// own do — one with no rendered field is not shown, and the suppressed-issue diagnostic reports
    /// it — because an advisory blocks nothing, so hiding one strands no verdict. A payload
    /// carrying the same message twice for one field at one severity lands it once: a reader has
    /// no use for it twice.
    /// </remarks>
    void ApplyServerIssues(IEnumerable<ValidationIssue> issues);
```

*Source: `src/Formidable.Blazor/IFormValidationEngine.cs`*

**Replace, not accumulate.** Each call is the server's current verdict, full stop. The server's
issues live apart from the client's own answer, so applying one swaps that set outright and
touches nothing the client said: call it twice in a row with the same or a corrected body and no
stale duplicate is left behind, and a client rule failing on the same field keeps its own message
throughout. An apply is also a disclosure event for the fields it names, so a client error the
last submit computed but had nowhere to show surfaces alongside the server's (see
[Disclosure](disclosure.md)). The server's verdict then stands until a newer whole-model answer
supersedes it — the debounced refresh behind the next edit, the next submit, or a page saying what
its freshly loaded values have earned — at which point a server-only issue with no matching client
rule goes, and one the client agrees with carries on through the client's own answer.

**The severity is the server's to set.** An error lands on its field, blocks the submit and reaches
the EditContext's message store. A warning or an info lands on the same field as an advisory:
visible in Formidable's own message components and in the summary, blocking nothing, and never
written to the store, which carries errors only. The page writes no advisory plumbing of its own.

**The store is the compatibility bridge, by contract.** It exists so a page that already renders a
native `ValidationSummary` or `ValidationMessage`, or calls `GetValidationMessages` directly,
keeps working without swapping in Formidable's own summary and message components. That is a
promise rather than a side effect: the store is a projection of the same channel views the
engine's own reads answer from, rebuilt whenever one of them moves, so a native component reads
the same answer `FormidableFieldMessage` does, as far as the store is able to carry it — errors
only, at no severity it can express, and with repeats collapsed on its own terms rather than the
issue reads'. The errors those
views disclose reach it, and the projection asks nothing further about registration: a field the
submit channel is watching keeps its store entry after it leaves the page, until a later pass
answers for it again, and the live channel's default policy discloses an engaged field's error
whether or not anything renders it. That last one matters most to a form with no Formidable
components at all. Nothing registers its fields, so a live channel deferring to registration would
leave a native page's own errors out of the only surface it reads. A page that wants the narrower
behaviour opts into it with
[`FormidableOptions.LiveDisclosure`](options.md#livedisclosure), which moves every surface
together rather than splitting them. What the store does not carry is the curated reading
experience: severities, disclosure, document order and focus are what Formidable's own summary and
message components provide, by reading the engine directly rather than the store.

**A rejection moves focus, the way a blocked submit does.** `FormidableForm.ApplyServerIssues`
is a submit's verdict arriving late, so a payload carrying an error lands the visitor on the first
error on the page — the target a blocked client submit gets, under the same
`FocusFirstErrorOnInvalidSubmit` switch (see
[Component kit](component-kit.md#formidableformtmodel)). A payload with no error in it moves
nothing, since nothing about it was rejected. `Engine.ApplyServerIssues(...)` is the quiet path
for an apply nobody just asked for, and `FormidableValidator`'s forwarders are quiet for a
narrower reason: attach mode does focus a blocked submit's first error, through its own
`ValidateForSubmitAsync()`, but the round trip is the page's own, so what happens after a
rejection is the page's to choose. `FocusFirstErrorAsync()` on the validator is how it chooses the
same move, once the applied verdict is on screen.

The sample deliberately skips client-side submit validation so the round-trip is visible end to
end — press Send and the server's 400 lands on the exact fields:

```csharp
    private async Task Send()
    {
        // Normalizing before the POST keeps the client's line list identical to what the
        // server validates (its filter normalizes too) - so issue paths always match rows.
        _order.Normalize();
        var response = await Http.PostAsJsonAsync(_endpoint, _order);

        if (response.IsSuccessStatusCode)
        {
            _status = "Server accepted the order.";
            return;
        }

        // One call for the whole verdict: every issue lands on the field it names, at the
        // severity it carries, so the page needs no advisory plumbing of its own. Each call
        // replaces the previous server verdict — pressing Send again with new input swaps the
        // old messages for the new ones, rather than accumulating them, so a corrected
        // resubmission cannot leave a stale one behind.
        var problem = await response.Content.ReadFromJsonAsync<FormidableValidationProblem>();
        _form!.ApplyServerIssues(problem!);
        _status = "Server rejected the order — its verdict is now inline.";
    }
```

*Source: `samples/Formidable.Sample/Pages/ServerRoundTrip.razor.cs`*

Errors reach the form's fields the moment `ApplyServerIssues` runs, bypassing the field-registry
disclosure check entirely (see [Disclosure](disclosure.md)). The server already validated the
submitted data, so a field the client happens not to have rendered isn't a disclosure concern —
and an error that blocks the save has to reach the user either way. Advisories in the same payload
defer to the registry exactly as the client's own advisories do: one whose field is on screen shows
there, and one whose field isn't is dropped with a suppressed-issue diagnostic naming it. An
advisory blocks nothing, so hiding one strands no verdict.

Both sides usually run the same validator, so the same advisory often arrives twice — once from the
client's own submit and once from the response. It shows once, as the client's copy, because the
views merge the client's answer first and drop the server's repeat of it. That match is on the
message text at the same severity, so a client and a server phrasing the same advisory differently
show both: keeping the two sides' message resources in step is the consumer's job, the same way
keeping their rules in step is.

**Advisories never ride a success response.** The wire contract above only defines the *rejection*
shape — the `advisories` extension exists on a 400 `ValidationProblemDetails` body. Both adapters
skip straight to the framework's ordinary success path when the report has no errors
(`return await next(context)` / `await next()`, shown in the Minimal APIs and MVC sections
above). The handler's or action's own return value passes through completely untouched, with no
advisories attached, because there is no wire contract for a successful response to carry them.
A handler that wants its 200 to say "saved, but note…" builds that response itself, from the
report the adapter already computed — see
[Returning warnings beside a 200](#returning-warnings-beside-a-200).

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
URL, so the two 400s are traceable to their source.
[`samples/Formidable.Sample.Api/requests.http`](../samples/Formidable.Sample.Api/requests.http)
has ready-made requests against both endpoints for use outside the browser. Run the API first
(`dotnet run --project samples/Formidable.Sample.Api`), then open `/server` in the Blazor sample
and press "Send to server". Pressing Enter triggers the browser's implicit form submission,
which runs the client-side submit pipeline this page deliberately skips.
