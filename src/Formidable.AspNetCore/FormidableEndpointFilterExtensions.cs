using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

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
