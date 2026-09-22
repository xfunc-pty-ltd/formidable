using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Formidable.AspNetCore;

/// <summary>Extension methods that validate a minimal-API route handler's, or a route group's, model argument before the handler runs.</summary>
public static class FormidableEndpointFilterExtensions
{
    /// <summary>Validates the handler's <typeparamref name="TModel"/> argument with <paramref name="profile"/> before the handler runs, answering a report with errors as a 400 validation problem.</summary>
    /// <typeparam name="TModel">The model type to validate; the handler must declare a parameter whose type is assignable to it.</typeparam>
    /// <param name="builder">The route handler.</param>
    /// <param name="profile">The profile to validate with. Defaults to <see cref="ValidationProfile.Submit"/>.</param>
    /// <param name="missingBodyMessage">The model-level message the 400 carries when the platform refuses a body that bound to <see langword="null"/>; <see langword="null"/> keeps "A request body is required.", any other string is used as given.</param>
    /// <returns><paramref name="builder"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The handler declares no parameter assignable to <typeparamref name="TModel"/>; thrown while the endpoint's request pipeline is built, not at a request.</exception>
    /// <remarks>
    /// A model implementing <see cref="INormalizableModel"/> is normalized first. The 400 takes
    /// <see cref="ValidationReportProblemMapper"/>'s shape. A report without errors reaches the
    /// handler, which reads it through
    /// <see cref="FormidableHttpContextExtensions.GetFormidableValidationReport"/>. A parameter
    /// bound <see langword="null"/> is left to the platform's own rule for its declaration: an
    /// optional parameter reaches the handler as <see langword="null"/> with no report recorded,
    /// and a refused one gets a 400 validation problem carrying
    /// <paramref name="missingBodyMessage"/> in place of the platform's bodiless 400. With several
    /// arguments assignable to <typeparamref name="TModel"/>, the first bound non-null is validated.
    /// </remarks>
    public static RouteHandlerBuilder Validate<TModel>(
        this RouteHandlerBuilder builder,
        ValidationProfile? profile = null,
        string? missingBodyMessage = null)
        where TModel : class =>
        AddValidation<RouteHandlerBuilder, TModel>(builder, profile, missingBodyMessage);

    /// <summary>Validates every endpoint in the group the way <see cref="Validate{TModel}(RouteHandlerBuilder, ValidationProfile, string)"/> validates one handler, with one profile and one missing-body message for all of them.</summary>
    /// <typeparam name="TModel">The model type to validate; every endpoint in the group must declare a parameter whose type is assignable to it.</typeparam>
    /// <param name="builder">The route group.</param>
    /// <param name="profile">The profile to validate with, for every endpoint in the group. Defaults to <see cref="ValidationProfile.Submit"/>.</param>
    /// <param name="missingBodyMessage">The model-level message a 400 carries when the platform refuses a body that bound to <see langword="null"/>, for every endpoint in the group; <see langword="null"/> keeps "A request body is required.", any other string is used as given.</param>
    /// <returns><paramref name="builder"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">An endpoint in the group declares no parameter assignable to <typeparamref name="TModel"/>, whatever its siblings declare; thrown while that endpoint's request pipeline is built, not at a request.</exception>
    public static RouteGroupBuilder Validate<TModel>(
        this RouteGroupBuilder builder,
        ValidationProfile? profile = null,
        string? missingBodyMessage = null)
        where TModel : class =>
        AddValidation<RouteGroupBuilder, TModel>(builder, profile, missingBodyMessage);

    // The one factory both overloads install. AddEndpointFilterFactory is itself a single
    // method generic over TBuilder : IEndpointConventionBuilder returning that same TBuilder, so
    // both builders reach one shared framework method either way; this mirrors the framework's
    // shape rather than inventing one.
    private static TBuilder AddValidation<TBuilder, TModel>(
        TBuilder builder, ValidationProfile? profile, string? missingBodyMessage)
        where TBuilder : IEndpointConventionBuilder
        where TModel : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        var resolvedProfile = profile ?? ValidationProfile.Submit;
        return builder.AddEndpointFilterFactory((factoryContext, next) =>
        {
            ThrowIfNoDeclaredParameter<TModel>(factoryContext.MethodInfo);
            var filter = new ValidationEndpointFilter<TModel>(resolvedProfile, missingBodyMessage);
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
