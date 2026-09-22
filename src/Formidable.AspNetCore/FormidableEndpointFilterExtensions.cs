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
    /// as a 500 on the one broken route. Whether a declared <typeparamref name="TModel"/>
    /// parameter bound to <see langword="null"/> is acceptable is left to the PLATFORM, which
    /// decides it from the declaration: a parameter the handler declared optional — nullable, or
    /// carrying a default — reaches the handler with <see langword="null"/> exactly as it would
    /// without this filter, and one the platform refuses gets the standard 400 validation shape
    /// with a model-level "A request body is required." error in place of the bare, bodiless 400
    /// the platform writes for it. When the handler declares more than one parameter of type
    /// <typeparamref name="TModel"/>, only the first one is validated.
    /// </remarks>
    public static RouteHandlerBuilder Validate<TModel>(this RouteHandlerBuilder builder, ValidationProfile? profile = null)
        where TModel : class =>
        AddValidation<RouteHandlerBuilder, TModel>(builder, profile);

    /// <inheritdoc cref="Validate{TModel}(RouteHandlerBuilder, ValidationProfile)"/>
    /// <param name="builder">The route group to validate.</param>
    /// <param name="profile">The profile to run; defaults to <see cref="ValidationProfile.Submit"/>.</param>
    /// <remarks>
    /// Every endpoint in the group must bind a <typeparamref name="TModel"/>-typed parameter —
    /// checked endpoint by endpoint, and an endpoint without one throws
    /// <see cref="InvalidOperationException"/> when its request pipeline is built (a wiring
    /// bug), which fails route materialization as a whole: a group carrying a mis-wired
    /// endpoint fails every request to the application, loudly, rather than leaving that one
    /// endpoint to 500 among working siblings. An endpoint that HAS the parameter but received
    /// <see langword="null"/> for it leaves that to the platform and enriches only a refusal,
    /// per the single-handler overload above. When an endpoint declares more than one parameter
    /// of type <typeparamref name="TModel"/>, only the first one is validated.
    /// </remarks>
    public static RouteGroupBuilder Validate<TModel>(this RouteGroupBuilder builder, ValidationProfile? profile = null)
        where TModel : class =>
        AddValidation<RouteGroupBuilder, TModel>(builder, profile);

    // The one factory both overloads install. AddEndpointFilterFactory is itself a single
    // method generic over TBuilder : IEndpointConventionBuilder returning that same TBuilder, so
    // both builders reach one shared framework method either way; this mirrors the framework's
    // shape rather than inventing one.
    private static TBuilder AddValidation<TBuilder, TModel>(TBuilder builder, ValidationProfile? profile)
        where TBuilder : IEndpointConventionBuilder
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
    // argument as exactly one thing: "the declared argument was bound null" — which it hands
    // straight back to the platform to judge, since the declaration that decides it is not
    // readable from the argument.
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
