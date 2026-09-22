using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Formidable.Blazor;

/// <summary>Dependency-injection registration for Formidable's Blazor integration.</summary>
public static class FormidableBlazorServiceCollectionExtensions
{
    /// <summary>
    /// Registers Formidable's core services (see <see cref="FormidableServiceCollectionExtensions.AddFormidable"/>)
    /// plus <see cref="IFormidableFocusService"/>. The one-call registration for Blazor consumers.
    /// Existing registrations are respected.
    /// </summary>
    public static IServiceCollection AddFormidableBlazor(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddFormidable();
        services.TryAddScoped<IFormidableFocusService, FormidableFocusService>();
        return services;
    }

    /// <summary>
    /// Registers everything <see cref="AddFormidableBlazor(IServiceCollection)"/> does, plus a
    /// <see cref="FormidableOptions"/> singleton configured by <paramref name="configureDefaults"/>.
    /// Every Formidable form that omits its own <c>Options</c> parameter uses that instance, so a
    /// design system's class names or a team's debounce are stated once for the whole app instead
    /// of on every form. A form's own <c>Options</c> parameter still wins where it is passed.
    /// Existing registrations are respected.
    /// </summary>
    /// <remarks>
    /// The configured instance is a singleton the whole app shares, and the engine re-reads its
    /// properties on every pass — so mutating it at runtime changes behaviour in every live form,
    /// not just the one being looked at.
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
