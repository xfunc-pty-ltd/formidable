using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
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
/// skips the argument silently — name the base type on the attribute
/// (<c>[Validate(typeof(Order))]</c>) to close it, which is the recommended shape for any action
/// that accepts polymorphic model binding: an explicit type is validated as the declared type
/// whatever the runtime type turns out to be, and a missing <c>IValidator&lt;T&gt;</c> for it
/// throws rather than being skipped.
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
public sealed class ValidateAttribute : ActionFilterAttribute, IActionModelConvention, IControllerModelConvention
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
    /// action and the model type it declares no parameter for — if the action could never hand
    /// this filter anything to validate. Decided from the action's DECLARED parameters when MVC
    /// builds its application model, so it fires once, before the host serves a request, and no
    /// request shape can reach it: an explicit constructor type
    /// (<c>[Validate(typeof(Order), RequireValidator = true)]</c>) that matches no declared
    /// parameter is the case it catches, and it catches it whether or not any request ever
    /// arrives. Off by default: an action mixing validatable models with ordinary parameters
    /// (route values, query strings, injected services) is free to declare no model at all,
    /// which is not a misconfiguration.
    /// </summary>
    /// <remarks>
    /// With no explicit types this attribute discovers what to validate from validator
    /// REGISTRATION, which an application-model convention cannot see — there is no DI at model
    /// build — so strict mode there can only insist the action declares parameters at all. The
    /// registration failures are reported where they already are, when a resolution actually
    /// fails: a named type with no <c>IValidator&lt;T&gt;</c> names
    /// <c>AddValidatorsFromAssembly</c>, and an unwired adapter names <c>AddFormidable()</c>.
    /// Naming the model types is therefore what makes strict mode strict.
    /// </remarks>
    public bool RequireValidator { get; set; }

    /// <inheritdoc />
    public override async Task OnActionExecutionAsync(
        ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var profile = ValidationProfile.FromName(Profile);
        var services = context.HttpContext.RequestServices;
        var issues = new List<ValidationIssue>();
        var validatedAny = false;

        foreach (var (name, argument) in context.ActionArguments)
        {
            if (argument is null)
            {
                continue;
            }

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

    // Strict mode is decided from the action's DECLARED parameters while MVC builds its
    // application model — before the host serves anything — so a misconfiguration check can
    // never be reached by a request's shape. It reports through the same convention seam the
    // framework gives any attribute: MVC applies IActionModelConvention for a METHOD-level
    // attribute and IControllerModelConvention for a CLASS-level one, both without an
    // AddControllers(options => ...) registration, so [Validate] needs neither a startup hook
    // nor DI to be checked. This is the shape the minimal-API half already ships
    // (FormidableEndpointFilterExtensions.ThrowIfNoDeclaredParameter, which reads the handler's
    // MethodInfo at filter-build time for the same reason).
    void IActionModelConvention.Apply(ActionModel action) => ThrowIfNothingToValidate(action);

    // A class-level [Validate] reaches MVC as a CONTROLLER convention and never as an action
    // one: ActionModel.Attributes carries only the action method's own attributes, so
    // IActionModelConvention alone would leave every class-level placement unchecked. Each
    // action is judged on its own parameters, exactly as the group overload of the minimal-API
    // extension checks each endpoint's own signature rather than sharing one answer.
    void IControllerModelConvention.Apply(ControllerModel controller)
    {
        foreach (var action in controller.Actions)
        {
            ThrowIfNothingToValidate(action);
        }
    }

    private void ThrowIfNothingToValidate(ActionModel action)
    {
        if (!RequireValidator)
        {
            return;
        }

        if (_modelTypes.Length == 0)
        {
            // Discovery mode names no type: what makes a parameter validatable is a validator
            // REGISTRATION, and no application-model convention can read DI. An action with no
            // parameters at all is still decidable, and is the whole of what strict discovery
            // mode can honestly claim.
            if (action.Parameters.Count == 0)
            {
                throw new InvalidOperationException(
                    $"[Validate(RequireValidator = true)] on {Describe(action)} declares no parameters at all — " +
                    "there is nothing to validate. Declare the model as a parameter, or set RequireValidator = false.");
            }

            return;
        }

        // Assignability, and in THIS direction, because it has to admit exactly what
        // ResolveValidatedType would validate: an argument is validated as its DECLARED type
        // when that type is one of _modelTypes, and otherwise as its own RUNTIME type when THAT
        // is — and a bound instance's runtime type is always assignable to the parameter it
        // bound to. So a parameter can carry a validated model exactly when some model type is
        // assignable TO it, which covers the exact match and the base-typed parameter of a
        // polymorphic hierarchy alike. (The minimal-API check tests the opposite direction
        // because OfType<TModel> there matches a more DERIVED declared parameter; both checks
        // say one thing — accept exactly what the runtime path would validate.)
        foreach (var parameter in action.Parameters)
        {
            foreach (var modelType in _modelTypes)
            {
                if (parameter.ParameterInfo.ParameterType.IsAssignableFrom(modelType))
                {
                    return;
                }
            }
        }

        var subject = string.Join(", ", _modelTypes.Select(FriendlyTypeName.Of));
        var declared = action.Parameters.Count == 0
            ? "no parameters at all"
            : string.Join(", ", action.Parameters.Select(p => FriendlyTypeName.Of(p.ParameterInfo.ParameterType)));

        throw new InvalidOperationException(
            $"[Validate(RequireValidator = true)] on {Describe(action)} found no parameter that could carry {subject} — " +
            $"it declares {declared}. Declare the model as a parameter, drop the type from the attribute, " +
            "or set RequireValidator = false.");
    }

    private static string Describe(ActionModel action) =>
        $"{FriendlyTypeName.Of(action.Controller.ControllerType)}.{action.ActionMethod.Name}";

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
