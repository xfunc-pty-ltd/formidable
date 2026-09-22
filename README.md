<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="docs/assets/hero-dark.png">
    <img src="docs/assets/hero-light.png" alt="The Formidable sample app after a blocked submit: a validation summary listing several errors and an advisory, with red-bordered required fields below it." width="820">
  </picture>
</p>

<h1 align="center">Formidable</h1>
<p align="center">Form validation for Blazor, built on FluentValidation — profiles, progressive disclosure, row-stable collections, and one wire format shared by client and server.</p>

<p align="center">
  <a href="https://github.com/xfunc/formidable/actions/workflows/ci.yml"><img src="https://github.com/xfunc/formidable/actions/workflows/ci.yml/badge.svg" alt="CI status"></a><!-- publish-day: verify -->
  <a href="https://github.com/xfunc/formidable/actions/workflows/deploy-pages.yml"><img src="https://github.com/xfunc/formidable/actions/workflows/deploy-pages.yml/badge.svg" alt="Deploy Pages status"></a><!-- publish-day: verify -->
  <a href="https://www.nuget.org/packages/Formidable.Blazor/"><img src="https://img.shields.io/nuget/v/Formidable.Blazor.svg" alt="NuGet version"></a><!-- publish-day: verify -->
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-blue.svg" alt="MIT License"></a><!-- publish-day: verify -->
</p>

## What it is

- **Draft/Submit orthogonality** — one validator definition, two built-in lifecycles: a lenient
  Draft profile for save-as-you-go, and a strict Submit profile for the real thing. Define
  further custom profiles (wizard steps, approval stages) from the same validator.
- **A headless component kit** — `FormidableForm`, `FormidableField`, `FieldMessage`, and
  `FormSummary` own the `EditContext` and render whatever the validator reports. Formidable
  ships no CSS; it drops into any UI library or plain HTML.
- **One validator, client and server** — the same FluentValidation rules run in the browser and
  on the server; a `ValidationProblemDetails` response applies straight into the engine so a
  server-rejected save lights up the exact fields inline.
- **Progressive disclosure** — validation errors follow what's actually rendered; a field the
  user can't see never nags, and a defensive gate catches the case where every failure would
  otherwise go unseen.

## Packages

| Package | Depends on | Contents |
|---|---|---|
| `Formidable` | FluentValidation only | Validation profiles, `ProfiledValidator<T>` / `DraftSubmitValidator<T>`, the `IModelValidator` seam, `ValidationIssue` / `ValidationReport`, `INormalizableModel`, model introspection. No Blazor dependency. |
| `Formidable.Blazor` | `Formidable` + `Microsoft.AspNetCore.Components.Web` | The validation engine (EditContext integration, field registry, validation flows) and the headless component kit (`FormidableForm`, `FormidableField`, `FieldMessage`, `FormSummary`, …). |
| `Formidable.AspNetCore` | `Formidable` + ASP.NET Core | Minimal-API endpoint filter and MVC `[Validate]` action filter, returning `ValidationProblemDetails` in the same path format the Blazor client consumes. |

A shared contracts assembly (your models and validators) references `Formidable` only and
stays free of Blazor and ASP.NET Core dependencies. A Blazor client adds `Formidable.Blazor`;
an API adds `Formidable.AspNetCore`. Neither leaf package depends on the other — both depend
only on `Formidable`.

## 5-minute quickstart

Install the Blazor package:

```bash
dotnet add package Formidable.Blazor
```

Register it and your FluentValidation validators:

```csharp
builder.Services.AddFormidableBlazor();
builder.Services.AddScoped<IValidator<QuickContact>, QuickContactValidator>();
```

The model and validator — a plain `AbstractValidator<T>`, no profiles required:

```csharp
using FluentValidation;

namespace Formidable.Sample.Shared;

public class QuickContact
{
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
}

// The five-minute experience: one plain FluentValidation validator, no profiles.
// Formidable's Draft/Submit profiles both include default rules, so an ordinary
// AbstractValidator works unchanged. Email field uses Cascade.Stop so a missing value
// shows one message; invalid format shows another.
public class QuickContactValidator : AbstractValidator<QuickContact>
{
    public QuickContactValidator()
    {
        RuleFor(c => c.Name).NotEmpty().WithMessage("Name is required");
        RuleFor(c => c.Email).Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("Email is required")
            .EmailAddress().WithMessage("A valid email is required");
    }
}
```

*Source: `samples/Formidable.Sample.Shared/QuickContact.cs`*

The page — routed at `@page "/"` in the sample:

```razor
<FormidableForm Model="_contact" OnValidSubmit="HandleValid">
    <FormSummary />

    <div class="field"><label>Name <FormidableInputText For="() => _contact.Name" @bind-Value="_contact.Name" /></label>
        <FieldMessage For="() => _contact.Name" /></div>
    <div class="field"><label>Email <FormidableInputText For="() => _contact.Email" @bind-Value="_contact.Email" /></label>
        <FieldMessage For="() => _contact.Email" /></div>

    <div class="actions"><button type="submit">Submit</button></div>
</FormidableForm>

@if (_submitted)
{
    <p role="status">Submitted — thanks, @_contact.Name!</p>
}
```

*Excerpt from `samples/Formidable.Sample/Pages/Quickstart.razor`* — the `_contact` field and
`HandleValid` handler live alongside in `Quickstart.razor.cs`. The `class` attributes are the
sample app's own styling; the library ships none.

That's it — `FormidableForm` owns the `EditContext`, `FormidableInputText` registers the field
and applies validation CSS classes, and `FieldMessage`/`FormSummary` render whatever the
validator reports.

## Server validation in two lines

The same validator runs server-side and returns errors in the format the client expects to
apply. Minimal APIs:

```csharp
var orders = app.MapGroup("/api/orders").Validate<RoundTripOrder>();
```

*Source: `samples/Formidable.Sample.Api/Program.cs`*

MVC controllers:

```csharp
[Validate] // class-level: every action's validatable arguments run the Submit profile
```

*Source: `samples/Formidable.Sample.Api/Controllers/AgreementsController.cs`*

## Documentation

| Topic | Doc |
|---|---|
| Recipes (behaviour → configuration) and troubleshooting | [`docs/recipes.md`](docs/recipes.md) |
| Validation profiles (Draft/Submit and custom) | [`docs/profiles.md`](docs/profiles.md) |
| `FormidableOptions` reference | [`docs/options.md`](docs/options.md) |
| Progressive disclosure | [`docs/disclosure.md`](docs/disclosure.md) |
| Collections and row-stable error identity | [`docs/collections-and-row-identity.md`](docs/collections-and-row-identity.md) |
| Async validation rules | [`docs/async-validation.md`](docs/async-validation.md) |
| Severity levels | [`docs/severity.md`](docs/severity.md) |
| Server integration (minimal APIs, MVC, wire format) | [`docs/server-integration.md`](docs/server-integration.md) |
| The headless component kit | [`docs/component-kit.md`](docs/component-kit.md) |
| CSS and accessibility | [`docs/css-and-accessibility.md`](docs/css-and-accessibility.md) |
| Migrating from another integration layer | [`docs/migration-guide.md`](docs/migration-guide.md) |
| Acceptance benchmark (workaround → mechanism) | [`docs/benchmark.md`](docs/benchmark.md) |
| Testing (suite shape, how to run each layer, the release gate) | [`docs/testing.md`](docs/testing.md) |
| Releasing a version (the maintainer runbook) | [`docs/releasing.md`](docs/releasing.md) |
| Manual walkthrough of the sample (the eyes-on pass) | [`samples/MANUAL-CHECKLIST.md`](samples/MANUAL-CHECKLIST.md) |

## Run the sample locally

The sample app is a runnable tour, one page per feature.

```bash
git clone https://github.com/xfunc/formidable.git
cd formidable
```
<!-- publish-day: verify (clone URL above) -->

From the repo root, in two terminals:

```bash
dotnet run --project samples/Formidable.Sample.Api
dotnet run --project samples/Formidable.Sample
```

The API listens on `http://localhost:5180`; open the Blazor app at
`http://localhost:5181`.

## Live demo

The same sample is mirrored to GitHub Pages, deployed from `main`, with a simulated in-browser
API standing in for the real server — every other page behaves exactly as it does locally.

**[xfunc.github.io/formidable](https://xfunc.github.io/formidable/)** <!-- publish-day: verify -->

## Contributing, security, and license

Contributions are welcome — see [CONTRIBUTING.md](CONTRIBUTING.md) before opening a pull
request. Found a security issue? See [SECURITY.md](SECURITY.md) for private disclosure.

Formidable is licensed under the [MIT License](LICENSE).
