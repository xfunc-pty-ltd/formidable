using FluentValidation;
using Formidable.Blazor;
using Formidable.WebApp.Fixture;
using Formidable.WebApp.Fixture.Components;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Both registrations live in the one container this single-project Web App resolves everything
// from — the prerender pass, the circuit and the static page alike.
builder.Services.AddFormidableBlazor();
builder.Services.AddScoped<IValidator<Contact>, ContactValidator>();

var app = builder.Build();

// The kit imports its script from _content/Formidable.Blazor/, which is a referenced library's
// static web asset rather than a file in this project.
app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
