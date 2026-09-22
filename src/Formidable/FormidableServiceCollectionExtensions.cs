using System.Diagnostics.CodeAnalysis;
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
    /// <para>
    /// <see cref="IModelIntrospector"/> is a swappable seam: register your own implementation
    /// before calling this method (respected via <c>TryAdd</c>) to replace the reflection-based
    /// default — the seam a consumer publishing with full assembly trimming needs, since the
    /// default walks <c>TModel</c>'s members via reflection.
    /// </para>
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
        services.TryAddTransient(typeof(IModelValidator<>), typeof(FluentValidationModelValidator<>));
        return services;
    }
}
