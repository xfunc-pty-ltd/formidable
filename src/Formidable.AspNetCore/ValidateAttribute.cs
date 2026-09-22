using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.DependencyInjection;

namespace Formidable.AspNetCore;

/// <summary>Action filter that validates the action's model arguments with a validation profile and answers a report with errors as a 400 validation problem.</summary>
/// <remarks>
/// A model implementing <see cref="INormalizableModel"/> is normalized first. A
/// <see langword="null"/> argument is skipped; whether the action runs with one is MVC's decision,
/// made before this filter, and it does for a nullable parameter under <c>[ApiController]</c> and
/// for any body parameter on a plain <c>Controller</c>. Several validated arguments aggregate into
/// one report, readable through
/// <see cref="FormidableHttpContextExtensions.GetFormidableValidationReport"/>, and, on
/// rejection, into one 400: <c>errors</c> keyed by property path with no per-argument prefix, so
/// two models sharing a property name merge under one key, and warnings and infos under the
/// <c>advisories</c> extension.
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

    /// <summary>Validates every non-null argument whose declared type, or failing that its runtime type, has a registered FluentValidation <c>IValidator&lt;T&gt;</c>.</summary>
    public ValidateAttribute() => _modelTypes = [];

    /// <summary>Validates every non-null argument whose declared type, or failing that its runtime type, is one of <paramref name="modelTypes"/>.</summary>
    /// <param name="modelTypes">The model types to validate; an argument resolving to one with no registered validator makes <see cref="OnActionExecutionAsync"/> throw.</param>
    public ValidateAttribute(params Type[] modelTypes) => _modelTypes = modelTypes;

    /// <summary>Name of the profile to validate with, resolved on every request through <see cref="ValidationProfile.FromName(string)"/>: "Draft" or "Submit" matched case-insensitively, or a custom name. Defaults to "Submit".</summary>
    public string Profile { get; set; } = "Submit";

    /// <summary>Whether an action whose declared parameters could never hand this filter a model to validate throws <see cref="InvalidOperationException"/>, naming the action and what it declares, instead of running. Defaults to <see langword="false"/>.</summary>
    /// <remarks>
    /// With constructor types, an action none of whose parameters can carry a named type throws
    /// while MVC builds its application model. Without them, an action declaring no parameters
    /// throws at model build, and one whose declared parameter types resolve no FluentValidation
    /// validator throws on every request; a base-typed parameter whose only validator is for a
    /// derived type is refused there, although the filter would validate a derived instance, so
    /// name the derived type to admit it.
    /// </remarks>
    // Off by default because an action mixing validatable models with ordinary parameters (route
    // values, query strings, injected services) is free to declare no model at all, which is not
    // a misconfiguration.
    public bool RequireValidator { get; set; }

    /// <summary>Validates the action's arguments as the attribute is configured and short-circuits to the 400 when the aggregate report has an error.</summary>
    /// <param name="context">The action's execution context.</param>
    /// <param name="next">The rest of the action pipeline.</param>
    /// <returns>A task that completes when the action, or the 400 in its place, has run.</returns>
    /// <exception cref="ArgumentNullException"><see cref="Profile"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><see cref="Profile"/> is blank or joins several names with <c>,</c> or <c>;</c>.</exception>
    /// <exception cref="InvalidOperationException">No <see cref="IModelValidator{TModel}"/> resolves for the type an argument is validated as, the message naming the missing FluentValidation validator or, when one is registered, the missing <c>AddFormidable()</c> registration; or <see cref="RequireValidator"/> finds no declared parameter type with a registered validator.</exception>
    public override async Task OnActionExecutionAsync(
        ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var profile = ValidationProfile.FromName(Profile);
        var services = context.HttpContext.RequestServices;
        ThrowIfDiscoveryResolvesNoValidator(context.ActionDescriptor, services);

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
            context.HttpContext.SetFormidableValidationReport(aggregate);
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
    // Building the ValidationProblemDetails by hand carries none of that. The factory takes a
    // ModelStateDictionary, and it is handed an EMPTY one; the mapper's dictionary is put into
    // Errors afterward and never routed through it, for two reasons. A ModelStateDictionary is
    // a prefix trie that refuses a key deeper than 32 nodes, and the path an ordinary recursive
    // validator produces crosses that at sixteen collection levels — a few hundred bytes of
    // request body. MVC's own JsonOptions.MaxDepth and MvcOptions.MaxValidationDepth stop a
    // body that deep before any action filter runs, and an app that raises both to admit it
    // must not then meet a 500 here, on an internal limit nothing it configures can move. And the
    // framework's ValidationProblemDetails(ModelStateDictionary) constructor is quadratic in
    // the number of keys once the trie's default error cap is lifted, which one issue per
    // collection row demands — the shape every sample teaches, at a size an anonymous client
    // chooses. Setting Errors directly also keeps both adapters serving one dictionary: the
    // same key order, an empty message kept empty, case-differing paths kept apart. The errors
    // are out of the factory's reach, and that touches two consumer seams the same way: a
    // consumer's own ProblemDetailsFactory override receives the empty dictionary, and a
    // ProblemDetailsOptions.CustomizeProblemDetails hook runs inside the factory before Errors
    // is set. Either sees an empty Errors on this path, where the endpoint filter's
    // TypedResults.ValidationProblem shows the hook the full set and asks no
    // ProblemDetailsFactory for anything, so anything either derives from the errors is derived
    // from nothing, and an Errors entry either writes is replaced by the assignment below.
    // Everything else either does — reading the request, adding an extension — lands on the
    // response exactly as it does for the app's other 400s.
    private static ValidationProblemDetails BuildProblem(HttpContext httpContext, ValidationReport report)
    {
        var problem = httpContext.RequestServices
            .GetRequiredService<ProblemDetailsFactory>()
            .CreateValidationProblemDetails(
                httpContext, new ModelStateDictionary(), StatusCodes.Status400BadRequest);
        problem.Errors = ValidationReportProblemMapper.ToErrorDictionary(report);
        return problem;
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

    // Strict mode is decided from the action's DECLARED parameters, so a misconfiguration check
    // can never be reached by a request's shape. Everything decidable without a container is
    // decided here, while MVC builds its application model — before the host serves anything;
    // the registration half discovery mode needs waits for a request, because that is where a
    // container first exists (ThrowIfDiscoveryResolvesNoValidator below). It reports through the
    // same convention seam the
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
            // parameters at all is still decidable here, which is the whole of what this half
            // can claim; the registration itself is probed on the action's first request.
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

    // Strict DISCOVERY mode's other half, and the reason it runs here rather than beside the
    // application-model check above: with no type named, what makes a parameter validatable is a
    // validator REGISTRATION, and an ActionModel carries no IServiceProvider to ask. So the
    // convention settles what it can — the action declares parameters at all — and the rest is
    // settled the first time a request reaches the action.
    //
    // What is read is ActionDescriptor.Parameters: the action's DECLARED parameter list, built
    // once with the application model and handed to every request for that action unchanged.
    // That is the whole distinction from reading ActionArguments, which is what a request
    // BOUND — a shape any anonymous client controls, and reading it would turn a
    // misconfiguration check into a 500 the client decides the timing of. Declared parameters
    // make the verdict identical on every request, so no request can provoke the throw and none
    // can suppress it; a wiring bug fails every request to the action, which is the posture the
    // minimal-API half takes for the same class of fault
    // (FormidableEndpointFilterExtensions.ThrowIfNoDeclaredParameter), reached later than its
    // pipeline-build time only because DI is not readable before a request exists.
    private void ThrowIfDiscoveryResolvesNoValidator(ActionDescriptor actionDescriptor, IServiceProvider services)
    {
        if (!RequireValidator || _modelTypes.Length > 0)
        {
            return;
        }

        if (DiscoversAValidator(actionDescriptor, services))
        {
            return;
        }

        var declared = actionDescriptor.Parameters.Count == 0
            ? "no parameters at all"
            : string.Join(", ", actionDescriptor.Parameters.Select(p => FriendlyTypeName.Of(p.ParameterType)));

        throw new InvalidOperationException(
            $"[Validate(RequireValidator = true)] on {Describe(actionDescriptor)} found no registered validator for any " +
            $"type it declares — it declares {declared}. Register one with " +
            "services.AddScoped<IValidator<T>, TValidator>() or services.AddValidatorsFromAssembly(), name the model " +
            "types on the attribute ([Validate(typeof(T), RequireValidator = true)]), or set RequireValidator = false.");
    }

    // One verdict per action, computed on that action's first request and reused for every later
    // one: it is built from the declared parameter list and the container's registrations,
    // neither of which changes while the app runs, so probing them per request would repeat
    // identical reflection for the life of the process — the same reasoning the closed-generic
    // ValidateAsync memo below is built on. Keyed by the descriptor instance, which MVC builds
    // once with the application model; an instance field rather than a static one, so a
    // class-level attribute shared by a controller's actions still answers per action while the
    // memo lives and dies with the attribute MVC caches beside them.
    private readonly ConcurrentDictionary<ActionDescriptor, bool> _discoveredValidators = new();

    private bool DiscoversAValidator(ActionDescriptor actionDescriptor, IServiceProvider services) =>
        _discoveredValidators.GetOrAdd(
            actionDescriptor,
            static (descriptor, state) => descriptor.Parameters.Any(
                parameter => Probeable(parameter.ParameterType)
                    && state.Attribute.ShouldValidate(parameter.ParameterType, state.Services)),
            (Attribute: this, Services: services));

    // MakeGenericType cannot close IValidator<> over a by-ref, pointer or still-open type, and an
    // ActionDescriptor can be hand-built outside MVC's own pipeline — the case
    // DeclaredParameterType above already allows for — so the probe declines such a parameter
    // rather than throwing a second, unrelated exception at it.
    private static bool Probeable(Type parameterType) =>
        !parameterType.IsByRef && !parameterType.IsPointer && !parameterType.ContainsGenericParameters;

    private static string Describe(ActionModel action) =>
        $"{FriendlyTypeName.Of(action.Controller.ControllerType)}.{action.ActionMethod.Name}";

    // The same name the model-build message builds, from the descriptor MVC derived from that
    // model. DisplayName covers a descriptor that is not a controller action's at all, which the
    // request-time path can be handed where the convention could not be.
    private static string Describe(ActionDescriptor actionDescriptor) =>
        actionDescriptor is ControllerActionDescriptor controllerAction
            ? $"{FriendlyTypeName.Of(controllerAction.ControllerTypeInfo)}.{controllerAction.MethodInfo.Name}"
            : actionDescriptor.DisplayName ?? "this action";

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

        // DoNotWrapExceptions: a hand-rolled validator that throws before returning its task
        // surfaces its own exception type, exactly as it does through the endpoint filter's
        // direct call, rather than a TargetInvocationException that middleware mapping the
        // validator's exception to a status or a log category would not recognise.
        return (Task<ValidationReport>)method.Invoke(
            validator,
            BindingFlags.DoNotWrapExceptions,
            binder: null,
            [model, profile, cancellationToken],
            culture: null)!;
    }
}
