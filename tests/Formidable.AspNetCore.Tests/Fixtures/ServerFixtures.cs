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
            catch (Exception) when (!context.Response.HasStarted)
            {
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            }
        });

        map(app);
        await app.StartAsync();
        return app;
    }
}
