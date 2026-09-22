using System.Diagnostics.CodeAnalysis;
using Formidable.Introspection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Formidable;

/// <summary>Dependency-injection registration for Formidable's core services.</summary>
public static class FormidableServiceCollectionExtensions
{
    /// <summary>Registers the model introspector and the FluentValidation adapter, leaving any registration already present in place.</summary>
    /// <param name="services">The service collection to register into.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// The adapter, <see cref="FluentValidationModelValidator{TModel}"/>, is registered transient
    /// as the open generic <see cref="IModelValidator{TModel}"/> and resolves the consumer's own
    /// <c>IValidator&lt;T&gt;</c> registration. The introspector is registered as a singleton
    /// <see cref="IModelIntrospector"/>; register your own before calling this to replace the
    /// reflection-based <see cref="ReflectionModelIntrospector"/>, the replacement a publish with full
    /// assembly trimming requires.
    /// </remarks>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "ReflectionModelIntrospector walks TModel's members via reflection over " +
            "whatever model type each consumer validates; this is safe under Blazor WebAssembly's " +
            "default partial trimming, which leaves the consumer's own model assemblies untrimmed. " +
            "A consumer publishing with full trimming must register a custom IModelIntrospector " +
            "(see the remarks above) before calling this method.")]
    public static IServiceCollection AddFormidable(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IModelIntrospector, ReflectionModelIntrospector>();
        // Transient so the adapter can consume a validator of any lifetime: FluentValidation's
        // AddValidatorsFromAssembly registers validators Scoped by default, and a singleton
        // adapter would capture a Scoped validator as a captive dependency, which throws under
        // scope validation in ASP.NET Core hosts.
        services.TryAddTransient(typeof(IModelValidator<>), typeof(FluentValidationModelValidator<>));
        return services;
    }
}
