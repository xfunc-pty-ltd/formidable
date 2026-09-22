using Formidable.Blazor;
using Formidable.Sample;
using Formidable.Sample.Services;
using Formidable.Sample.Shared;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddFormidableBlazor();
builder.Services.AddValidatorsFromSharedAssembly();
builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri("http://localhost:5180") });
builder.Services.AddSingleton<SampleSourceReader>();

await builder.Build().RunAsync();
