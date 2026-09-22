using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

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

        var advisories = ValidationReportProblemMapper.ToAdvisories(report);
        return TypedResults.ValidationProblem(
            ValidationReportProblemMapper.ToErrorDictionary(report),
            extensions: advisories.Count > 0
                ? new Dictionary<string, object?> { [ValidationReportProblemMapper.AdvisoriesExtensionKey] = advisories }
                : null);
    }
}
