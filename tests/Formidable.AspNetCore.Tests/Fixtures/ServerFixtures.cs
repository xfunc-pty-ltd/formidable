using FluentValidation;
using Formidable;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Formidable.AspNetCore.Tests.Fixtures;

public class SampleItem
{
    public string Sku { get; set; } = string.Empty;
}

public class SampleOrder : INormalizableModel
{
    public string Description { get; set; } = string.Empty;
    public List<SampleItem> Items { get; set; } = [];

    // Strips whitespace-only rows (accidental blank entries) but leaves a truly-untouched
    // (empty-string) row in place so the per-row "Sku required" submit rule can still flag it —
    // IsNullOrWhiteSpace alone can't tell "never touched" from "typed then cleared" apart.
    public void Normalize() => Items.RemoveAll(item => item.Sku.Length > 0 && string.IsNullOrWhiteSpace(item.Sku));
}

public class SampleOrderValidator : DraftSubmitValidator<SampleOrder>
{
    protected override void ConfigureDraftRules()
    {
        RuleFor(order => order.Description).MaximumLength(10).WithMessage("Too long");
    }

    protected override void ConfigureSubmitRules()
    {
        RuleFor(order => order.Description).NotEmpty().WithMessage("Required");
        RuleFor(order => order.Description)
            .Must(description => description is null || !description.Contains('-'))
            .WithSeverity(Severity.Warning)
            .WithMessage("Avoid hyphens");
        RuleForEach(order => order.Items).ChildRules(item =>
            item.RuleFor(x => x.Sku).NotEmpty().WithMessage("Sku required"));
    }
}

// Region is deliberately nullable: ASP.NET Core infers an implicit [Required] for non-nullable
// reference-type properties on [ApiController] actions, and — for [FromQuery] complex-type
// binding specifically — an empty-but-present query value ("Region=") binds to null, tripping
// that implicit check. That fires the framework's own automatic-400 short-circuit (an action
// filter — ModelStateInvalidFilter, order -2000) BEFORE the Formidable [Validate] action filter
// ever runs, so the response carries ASP.NET Core's built-in "The Region field is required."
// message instead of Formidable's, and
// SampleOrder is never validated at all. A nullable property opts out of the implicit inference
// so FluentValidation's NotEmpty() rule below is what actually enforces "required" here.
public class SampleFilter
{
    public string? Region { get; set; }
}

public class SampleFilterValidator : DraftSubmitValidator<SampleFilter>
{
    protected override void ConfigureDraftRules()
    {
    }

    protected override void ConfigureSubmitRules()
    {
        RuleFor(f => f.Region).NotEmpty().WithMessage("Region is required");
    }
}

public class EscalatedNote
{
    public string Reason { get; set; } = string.Empty;
}

public class EscalatedNoteValidator : ProfiledValidator<EscalatedNote>
{
    protected override void ConfigureCommonRules()
    {
    }

    protected override void ConfigureProfiles() =>
        Profile("Escalated", () =>
            RuleFor(n => n.Reason).NotEmpty().WithMessage("Escalations must state a reason"));
}

public static class TestApp
{
    /// <summary>Builds and starts an in-memory app; the caller maps endpoints and disposes it.</summary>
    public static async Task<WebApplication> StartAsync(
        Action<WebApplication> map,
        Action<IServiceCollection>? services = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddFormidable();
        builder.Services.AddScoped<IValidator<SampleOrder>, SampleOrderValidator>();
        services?.Invoke(builder.Services);

        var app = builder.Build();

        // TestServer (unlike Kestrel) does not convert an unhandled exception escaping the
        // pipeline into a 500 response — it rethrows it out of HttpClient.SendAsync instead.
        // Mirror Kestrel's fallback so filter-thrown configuration errors surface as an HTTP
        // response the way they would in a real deployment.
        app.Use(async (context, next) =>
        {
            try
            {
                await next(context);
            }
            catch (Exception ex) when (!context.Response.HasStarted)
            {
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                await context.Response.WriteAsync($"{ex.GetType().Name}: {ex.Message}");
            }
        });

        map(app);
        await app.StartAsync();
        return app;
    }
}
