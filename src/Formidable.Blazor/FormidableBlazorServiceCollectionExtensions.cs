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
}
