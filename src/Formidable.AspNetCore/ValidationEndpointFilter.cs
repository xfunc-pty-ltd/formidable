using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

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

        var advisories = ValidationReportProblemMapper.ToAdvisories(report);
        return TypedResults.ValidationProblem(
            ValidationReportProblemMapper.ToErrorDictionary(report),
            extensions: advisories.Count > 0
                ? new Dictionary<string, object?> { [ValidationReportProblemMapper.AdvisoriesExtensionKey] = advisories }
                : null);
    }
}
