using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.DependencyInjection;

namespace Formidable.AspNetCore;

/// <summary>
/// Validates action arguments with a Formidable profile before the action runs: normalize
/// (when a model implements <see cref="INormalizableModel"/>), validate, and short-circuit to
/// a 400 ValidationProblemDetails — errors keyed by the client's path format, non-error
/// issues on the <c>advisories</c> extension — when any error issue exists.
/// </summary>
/// <remarks>
/// Without constructor arguments, resolution prefers each action argument's DECLARED parameter
/// type: when that type has a registered FluentValidation <c>IValidator&lt;T&gt;</c> (the
/// shipped <c>AddFormidable()</c> adapter path), it wins outright, so a base-typed parameter is
/// always validated under the base validator even when polymorphic model binding (e.g. System.
/// Text.Json's <c>$type</c> discriminator) materializes a derived runtime instance the client
/// controls. Only when the declared type resolves no validator does resolution fall back to
/// probing the argument's own runtime type, so a validator registered only for a derived type
/// still runs; when the action descriptor carries no matching declared parameter at all (e.g. a
/// hand-built <see cref="ActionDescriptor"/> outside MVC's own pipeline), the runtime type is
/// used directly, with no declared type to prefer. Pass explicit model types to validate those
/// types regardless of how their <see cref="IModelValidator{TModel}"/> is registered. The one
/// residual this resolution order leaves: a derived-only validator registration, or an
/// unregistered <c>$type</c>, combined with no validator for the declared type either, still
/// skips the argument silently — set <see cref="RequireValidator"/> to turn that into a thrown
/// misconfiguration instead, recommended for any endpoint that accepts polymorphic model
/// binding.
/// An action can bind more than one validatable argument; their issues aggregate into a single
/// report and, on rejection, a single <c>errors</c> dictionary keyed by each issue's own
/// property path with no per-argument prefix, so two validated models sharing a property name
/// merge under one key: that un-prefixed shape is the wire contract. The same aggregate
/// is readable for the rest of the request through
/// <see cref="FormidableHttpContextExtensions.GetFormidableValidationReport"/>, the action body
/// included.
/// </remarks>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
[RequiresUnreferencedCode(
    "Resolves IModelValidator<T> for each action argument's declared parameter type via " +
    "Type.MakeGenericType and invokes ValidateAsync via MethodInfo.Invoke; trimming can " +
    "remove the closed generic instantiation or the ValidateAsync method for argument " +
    "types not otherwise statically referenced, breaking validation for those types.")]
public sealed class ValidateAttribute : ActionFilterAttribute
{
    private readonly Type[] _modelTypes;

    /// <summary>Validates arguments discovered by validator registration.</summary>
    public ValidateAttribute() => _modelTypes = [];

    /// <summary>Validates the arguments of exactly these model types.</summary>
    public ValidateAttribute(params Type[] modelTypes) => _modelTypes = modelTypes;

    /// <summary>Profile name: "Draft", "Submit" (default), or a custom profile name, resolved
    /// once per request via <see cref="ValidationProfile.FromName(string)"/>. The two built-in
    /// names match case-insensitively (e.g. "draft" and "DRAFT" both resolve to the built-in
    /// <see cref="ValidationProfile.Draft"/>); custom names run default rules plus the
    /// same-named rule set, mirroring Submit's shape.</summary>
    public string Profile { get; set; } = "Submit";

    /// <summary>
    /// When <see langword="true"/>, throws <see cref="InvalidOperationException"/> — naming the
    /// argument type(s) considered and how to register a validator for them — if this filter
    /// would otherwise validate none of the action's arguments at all (e.g. a dropped
    /// <c>AddValidatorsFromAssembly</c> call, or an explicit constructor type that no longer
    /// matches any parameter). Off by default: an action mixing validatable models with
    /// ordinary parameters (route values, query strings, injected services) legitimately
    /// validates nothing on every request when it has no model argument, which is not a
    /// misconfiguration. The throw only fires when the action bound at least one non-null
    /// argument: a request that binds nothing at all (e.g. a null or empty body for a nullable
    /// parameter) is a client-triggerable condition, not a misconfiguration, so strict mode
    /// can't flag anything for it either — it only catches a genuinely bound-but-unvalidated
    /// argument.
    /// </summary>
    public bool RequireValidator { get; set; }

    /// <inheritdoc />
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

    // Built through the app's own ProblemDetailsFactory, which is what ControllerBase's
    // ValidationProblem() uses: the response then carries the trace identifier, the
    // ApiBehaviorOptions.ClientErrorMapping type link, and any consumer-registered factory's
    // own additions, so a 400 from this filter reads like every other 400 the same app returns.
    // Building the ValidationProblemDetails by hand carried none of that. The errors travel
    // through a ModelStateDictionary because that is the shape the factory takes; its error cap
    // is lifted, since a validation report legitimately runs to one issue per collection row and
    // silently dropping the overflow is exactly what a report is for.
    private static ValidationProblemDetails BuildProblem(HttpContext httpContext, ValidationReport report)
    {
        var modelState = new ModelStateDictionary { MaxAllowedErrors = int.MaxValue };

        foreach (var (path, messages) in ValidationReportProblemMapper.ToErrorDictionary(report))
        {
            foreach (var message in messages)
            {
                modelState.AddModelError(path, message);
            }
        }

        return httpContext.RequestServices
            .GetRequiredService<ProblemDetailsFactory>()
            .CreateValidationProblemDetails(httpContext, modelState, StatusCodes.Status400BadRequest);
    }

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

    // Resolves the type `argument` should be validated as. The DECLARED parameter type is tried
    // FIRST and wins outright when it has a registered validator — the base type of a polymorphic
    // hierarchy must always win over a $type-steered derived runtime instance, or a hostile
    // client could pick an unregistered derived type and skip validation entirely even though
    // the base type it's declared as has a validator (the security-relevant case). Only when the
    // declared type resolves NO validator does resolution fall back to probing the argument's
    // own runtime type, so a validator registered only for a derived type still runs. Returns
    // null when neither type resolves a validator.
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

    // Looks up the DECLARED type of the action parameter bound to `argument`. Returns null when
    // the descriptor carries no matching parameter (e.g. a hand-built ActionDescriptor outside
    // MVC's own pipeline); ResolveValidatedType above falls back to the argument's runtime type
    // in that case, since there is no declared type to prefer.
    private static Type? DeclaredParameterType(ActionDescriptor actionDescriptor, string parameterName) =>
        actionDescriptor.Parameters.FirstOrDefault(p => p.Name == parameterName)?.ParameterType;

    private static string BuildRequireValidatorMessage(ActionExecutingContext context)
    {
        var consideredTypes = context.ActionArguments
            .Where(pair => pair.Value is not null)
            .Select(pair => DeclaredParameterType(context.ActionDescriptor, pair.Key) ?? pair.Value!.GetType())
            .Distinct()
            .Select(FriendlyTypeName.Of)
            .ToArray();
        var subject = consideredTypes.Length > 0 ? string.Join(", ", consideredTypes) : "any argument";

        return $"[Validate(RequireValidator = true)] on {context.ActionDescriptor.DisplayName ?? "this action"} " +
            $"found no registered validator for {subject} — register a FluentValidation IValidator<T> for at " +
            "least one argument type and call services.AddFormidable() to install the adapter, or set RequireValidator = false.";
    }

    // Closed-generic ValidateAsync MethodInfo per argument type, built once and reused for
    // every later request: the argument type set for a running app is small and fixed (it's
    // whatever types the app declares validated arguments as), so paying MakeGenericType +
    // GetMethod on the first request per type and caching the result avoids repeating both on
    // every single request thereafter.
    private static readonly ConcurrentDictionary<Type, MethodInfo> ValidateAsyncMethods = new();

    private static Task<ValidationReport> InvokeValidateAsync(
        object validator, Type argumentType, object model, ValidationProfile profile, CancellationToken cancellationToken)
    {
        // Resolve ValidateAsync from the IModelValidator<T> interface, not the validator's
        // concrete type: GetType().GetMethod only finds implicitly implemented interface
        // members, so a consumer's explicit interface implementation (idiomatic C#) would make
        // that lookup return null. Interface-typed dispatch works for implicit and explicit
        // implementations alike.
        var method = ValidateAsyncMethods.GetOrAdd(argumentType, static type =>
            typeof(IModelValidator<>).MakeGenericType(type).GetMethod(nameof(IModelValidator<object>.ValidateAsync))!);
        return (Task<ValidationReport>)method.Invoke(validator, [model, profile, cancellationToken])!;
    }
}
