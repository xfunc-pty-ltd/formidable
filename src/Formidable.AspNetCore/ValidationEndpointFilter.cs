using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;

namespace Formidable.AspNetCore;

/// <summary>
/// Runs normalize + profile validation for one endpoint argument type, stashing the computed
/// report on the request for <see cref="FormidableHttpContextExtensions.GetFormidableValidationReport"/>.
/// Fails closed on wiring and never on a declaration: an unresolvable
/// <see cref="IModelValidator{TModel}"/> throws, and an endpoint with no parameter of this type
/// at all never reaches the filter — the endpoint filter factory in
/// <see cref="FormidableEndpointFilterExtensions"/> throws for it while the endpoint's request
/// pipeline is being built. A parameter bound to <see langword="null"/> is the platform's
/// decision, not this filter's: it is passed on, and only the refusal is enriched.
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
            // means the declared parameter itself was bound to null. Whether that is acceptable
            // is the PLATFORM's decision and is made from the DECLARATION — nullability, a
            // default value, an EmptyBodyBehavior — none of which a filter can read from a null
            // argument: the null branch is reached for a non-nullable parameter and a nullable
            // one alike. So the decision is delegated by calling next, exactly as if this filter
            // were not installed, and a consumer who declared the parameter optional is never
            // second-guessed. Reproducing the rule instead would mean owning a replica of it
            // that already differs between the two hosting models in one framework version.
            var passed = await next(context);
            var response = context.HttpContext.Response;

            // Enriched only where the platform is SEEN to have refused: the handler was skipped
            // (an empty result), a 400 stands on the response, and nothing has gone out yet. The
            // platform's own refusal is a bare Content-Length: 0 — cause-blind even with
            // ProblemDetails configured — so this restores the standard validation shape without
            // deciding anything. Everything else falls through as whatever next produced, which
            // is why the degrade path cannot be a wrong decision: it is pure delegation.
            if (passed is EmptyHttpResult
                && response.StatusCode == StatusCodes.Status400BadRequest
                && !response.HasStarted)
            {
                var missingBody = new ValidationReport([new ValidationIssue(string.Empty, "A request body is required.")]);
                return TypedResults.ValidationProblem(ValidationReportProblemMapper.ToErrorDictionary(missingBody));
            }

            return passed;
        }

        (model as INormalizableModel)?.Normalize();

        var validator = context.HttpContext.RequestServices.GetRequiredService<IModelValidator<TModel>>();
        var report = await validator.ValidateAsync(model, _profile, context.HttpContext.RequestAborted);

        // Stashed before the 400/pass-through decision so the request can always read the
        // verdict a validator produced: the handler composes a "saved, but note…" 200 from a
        // passing report's advisories, and middleware reads a rejection's full severity detail
        // without parsing the response body.
        context.HttpContext.SetFormidableValidationReport(report);

        if (report.IsValid)
        {
            return await next(context);
        }

        return TypedResults.ValidationProblem(
            ValidationReportProblemMapper.ToErrorDictionary(report),
            extensions: ValidationReportProblemMapper.ToAdvisoriesExtensions(report));
    }
}
