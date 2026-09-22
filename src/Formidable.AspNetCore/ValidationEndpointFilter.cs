using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Formidable.AspNetCore;

/// <summary>
/// Runs normalize + profile validation for one endpoint argument type. Always fails closed: a
/// declared parameter bound to null 400s, and an unresolvable
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

        if (report.IsValid)
        {
            return await next(context);
        }

        return TypedResults.ValidationProblem(
            ValidationReportProblemMapper.ToErrorDictionary(report),
            extensions: ValidationReportProblemMapper.ToAdvisoriesExtensions(report));
    }
}
