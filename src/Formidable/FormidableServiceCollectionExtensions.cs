using Formidable.Introspection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Formidable;

/// <summary>Dependency-injection registration for Formidable core services.</summary>
public static class FormidableServiceCollectionExtensions
{
    /// <summary>
    /// Registers the model introspector and the FluentValidation adapter (open generic).
    /// Validators themselves (<c>IValidator&lt;T&gt;</c>) are registered by the consumer.
    /// Existing registrations are respected.
    /// </summary>
    /// <remarks>
    /// The adapter is registered transient so it can consume validators of any lifetime.
    /// FluentValidation's <c>AddValidatorsFromAssembly</c> registers validators Scoped by
    /// default; a singleton adapter would capture a Scoped validator as a captive dependency,
    /// which throws under scope validation in ASP.NET Core hosts.
    /// </remarks>
    public static IServiceCollection AddFormidable(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IModelIntrospector, ReflectionModelIntrospector>();
        services.TryAddTransient(typeof(IModelValidator<>), typeof(FluentValidationModelValidator<>));
        return services;
    }
}
