using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;

namespace Formidable.AspNetCore;

/// <summary>
/// Validates action arguments with a Formidable profile before the action runs: normalize
/// (when a model implements <see cref="INormalizableModel"/>), validate, and short-circuit to
/// a 400 ValidationProblemDetails — errors keyed by the client's path format, non-error
/// issues on the <c>warnings</c> extension — when any error issue exists.
/// </summary>
/// <remarks>
/// Without constructor arguments, every non-null action argument whose type has a registered
/// FluentValidation <c>IValidator&lt;T&gt;</c> is validated (the shipped
/// <c>AddFormidable()</c> adapter path). Pass explicit model types to validate those types
/// regardless of how their <see cref="IModelValidator{TModel}"/> is registered.
/// </remarks>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
[RequiresUnreferencedCode(
    "Resolves IModelValidator<T> for each action argument's runtime type via " +
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

    /// <summary>Profile name: "Draft", "Submit" (default), or a custom profile name. The two
    /// built-in names match case-insensitively (e.g. "draft" and "DRAFT" both resolve to the
    /// built-in <see cref="ValidationProfile.Draft"/>); custom names run default rules plus the
    /// same-named rule set, mirroring Submit's shape.</summary>
    public string Profile { get; set; } = "Submit";

    /// <inheritdoc />
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

    private static Task<ValidationReport> InvokeValidateAsync(
        object validator, Type argumentType, object model, ValidationProfile profile, CancellationToken cancellationToken)
    {
        // Resolve ValidateAsync from the IModelValidator<T> interface, not the validator's
        // concrete type: GetType().GetMethod only finds implicitly implemented interface
        // members, so a consumer's explicit interface implementation (idiomatic C#) would make
        // that lookup return null. Interface-typed dispatch works for implicit and explicit
        // implementations alike.
        var method = typeof(IModelValidator<>).MakeGenericType(argumentType)
            .GetMethod(nameof(IModelValidator<object>.ValidateAsync))!;
        return (Task<ValidationReport>)method.Invoke(validator, [model, profile, cancellationToken])!;
    }
}
