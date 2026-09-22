using FluentValidation;
using Formidable.Blazor;
using Formidable.Tutorial;
using Formidable.Tutorial.Pages;
using Formidable.Tutorial.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddFormidableBlazor();
builder.Services.AddScoped<IValidator<Stage1.Contact>, Stage1.ContactValidator>();
builder.Services.AddScoped<IValidator<Stage2.Contact>, Stage2.ContactValidator>();
builder.Services.AddScoped<IValidator<Stage3.Contact>, Stage3.ContactValidator>();
builder.Services.AddScoped<IValidator<Stage4.Contact>, Stage4.ContactValidator>();
builder.Services.AddScoped<IValidator<Stage5.Contact>, Stage5.ContactValidator>();
builder.Services.AddScoped<IValidator<Stage6.Contact>, Stage6.ContactValidator>();

builder.Services.AddScoped<IEmailDirectory, InMemoryEmailDirectory>();
builder.Services.AddScoped(sp => new HttpClient(new FakeRegistrationApi()) { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

await builder.Build().RunAsync();
