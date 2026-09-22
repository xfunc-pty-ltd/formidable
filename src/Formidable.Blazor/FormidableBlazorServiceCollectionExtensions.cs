using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Formidable.Blazor;

/// <summary>Dependency-injection registration for Formidable's Blazor integration.</summary>
public static class FormidableBlazorServiceCollectionExtensions
{
    /// <summary>Registers everything <see cref="FormidableServiceCollectionExtensions.AddFormidable"/> does plus <see cref="IFormidableFocusService"/>, <see cref="IFormidableDomValueSync"/> and <see cref="IFormidableFieldOrderService"/>, leaving any existing registration in place.</summary>
    /// <param name="services">The service collection to add to.</param>
    /// <returns>The same collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddFormidableBlazor(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddFormidable();
        services.TryAddScoped<IFormidableFocusService, FormidableFocusService>();
        services.TryAddScoped<IFormidableDomValueSync, FormidableDomValueSync>();
        services.TryAddScoped<IFormidableFieldOrderService, FormidableFieldOrderService>();
        return services;
    }

    /// <summary>Registers everything <see cref="AddFormidableBlazor(IServiceCollection)"/> does plus an app-wide <see cref="FormidableOptions"/> default that every form without its own <c>Options</c> uses.</summary>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="configureDefaults">Configures the shared default instance before it is registered.</param>
    /// <returns>The same collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> or <paramref name="configureDefaults"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// A form's own <c>Options</c> replaces the instance whole; <see cref="FormidableOptions(FormidableOptions)"/>
    /// copies it for a form that differs in one setting. An existing <see cref="FormidableOptions"/>
    /// registration stays, and the configured instance is then discarded.
    /// </remarks>
    public static IServiceCollection AddFormidableBlazor(
        this IServiceCollection services, Action<FormidableOptions> configureDefaults)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configureDefaults);
        services.AddFormidableBlazor();

        var defaults = new FormidableOptions();
        configureDefaults(defaults);
        services.TryAddSingleton(defaults);
        return services;
    }
}
