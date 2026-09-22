using System.Globalization;
using Formidable.Blazor;
using Formidable.Sample;
using Formidable.Sample.Services;
using Formidable.Sample.Shared;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddFormidableBlazor();
builder.Services.AddValidatorsFromSharedAssembly();
builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri("http://localhost:5180") });
builder.Services.AddSingleton<SampleSourceReader>();

var host = builder.Build();

// The culture must be current before RunAsync: that is the point at which WebAssembly
// downloads the satellite resource assemblies for it. Setting it any later leaves the app
// on the culture it booted with, which is why the localization page reloads on a switch.
var js = host.Services.GetRequiredService<IJSRuntime>();
var stored = await js.InvokeAsync<string?>("formidableSample.getCulture");
CultureInfo culture;
try
{
    culture = CultureInfo.GetCultureInfo(string.IsNullOrWhiteSpace(stored) ? "en-AU" : stored);
}
catch (CultureNotFoundException)
{
    // A corrupted or stale stored value must not stop the app from booting.
    culture = CultureInfo.GetCultureInfo("en-AU");
}

CultureInfo.DefaultThreadCurrentCulture = culture;
CultureInfo.DefaultThreadCurrentUICulture = culture;

await host.RunAsync();
