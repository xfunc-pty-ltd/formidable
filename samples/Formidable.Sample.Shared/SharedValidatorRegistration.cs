using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace Formidable.Sample.Shared;

public static class SharedValidatorRegistration
{
    // Each sample page's validator is registered here; the Api reuses this so the
    // client and server run identical rules from one assembly.
    public static IServiceCollection AddValidatorsFromSharedAssembly(this IServiceCollection services)
    {
        services.AddScoped<IValidator<QuickContact>, QuickContactValidator>();
        services.AddScoped<IValidator<DraftedBrief>, DraftedBriefValidator>();
        services.AddScoped<IValidator<TravelRequest>, TravelRequestValidator>();
        services.AddScoped<IValidator<Roster>, RosterValidator>();
        services.AddScoped<IValidator<Handle>, HandleValidator>();
        services.AddScoped<IValidator<Listing>, ListingValidator>();
        services.AddScoped<IValidator<RoundTripOrder>, RoundTripOrderValidator>();
        services.AddScoped<IValidator<GadgetOrder>, GadgetOrderValidator>();
        services.AddScoped<IValidator<TrimmedNote>, TrimmedNoteValidator>();
        services.AddScoped<IValidator<ReviewedPost>, ReviewedPostValidator>();
        services.AddScoped<IValidator<LocalizedProfile>, LocalizedProfileValidator>();
        services.AddScoped<IValidator<EventRegistration>, EventRegistrationValidator>();
        services.AddScoped<IValidator<ExpenseReport>, ExpenseReportValidator>();

        return services;
    }
}
