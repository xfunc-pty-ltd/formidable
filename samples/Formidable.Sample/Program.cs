using System.Globalization;
using Formidable.Blazor;
using Formidable.Sample;
using Formidable.Sample.Services;
using Formidable.Sample.Shared;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
#if HOSTED_DEMO
using Formidable;
#endif

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddFormidableBlazor();
builder.Services.AddValidatorsFromSharedAssembly();

#if HOSTED_DEMO
// The hosted demo has no real API behind it (GitHub Pages is static-only), so the HttpClient's
// handler is the in-browser stand-in instead of the default network handler - same validators,
// same wire shapes, no server. See HostedDemoApiHandler for the rationale and the three
// endpoint paths it answers.
builder.Services.AddScoped(sp =>
{
    var handler = new HostedDemoApiHandler(
        sp.GetRequiredService<IModelValidator<RoundTripOrder>>(),
        sp.GetRequiredService<IModelValidator<EventRegistration>>());
    return new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5180") };
});
#else
builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri("http://localhost:5180") });
#endif

builder.Services.AddSingleton<SampleSourceReader>();

var host = builder.Build();

// The culture must be current before RunAsync: that is the point at which WebAssembly
// downloads the satellite resource assemblies for it. Setting it any later leaves the app
// on the culture it booted with, which is why the localization page reloads on a switch.
var js = host.Services.GetRequiredService<IJSRuntime>();
await FormidableCultureBootstrap.ApplyStoredCultureAsync(js, fallback: CultureInfo.GetCultureInfo("en-AU"));

await host.RunAsync();
