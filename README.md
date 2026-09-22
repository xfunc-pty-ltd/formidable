# Formidable

Formidable is a Blazor form-validation library built on FluentValidation. It adds what
FluentValidation and Blazor's `EditForm` don't give you out of the box: multiple validation
profiles (draft vs. submit lifecycles from one validator definition), progressive disclosure
(errors only surface for fields that are actually rendered), row-stable collection error
identity (add, remove, and reorder rows without errors jumping to the wrong row), severity
levels for warnings and info messages, and one wire format shared by the client and the server.
The component kit is headless — Formidable owns no visual design, so it drops into any UI
library or plain HTML.

## Features

- **Multiple named validation profiles** — Draft and Submit ship as conventions; define
  arbitrary custom profiles (wizard steps, approval stages) from the same validator.
- **Progressive disclosure** — validation errors follow what's actually rendered; fields the
  user can't see never nag.
- **Row-stable collection errors** — indexed and nested collection paths resolve by instance,
  so error identity survives add, remove, and reorder.
- **Client + server validation from one validator** — the same FluentValidation rules run in
  the browser and on the server.
- **Async validation rules** — debounced, cancellable, with per-field `IsValidating` state.
- **Severity levels** — warnings and info render distinctly and never block submit.
- **Server-error round-trip** — apply a `ValidationProblemDetails` response straight into the
  engine so a server-rejected save lights up the exact fields inline.
- **Field state API** — `GetFieldState` reports touched, modified, validating, and error/warning
  state per field; `ValidationReport.IsValid` and `SubmitOutcome.CanProceed` give the form-level
  verdict.
- **Normalize hook** — `INormalizableModel.Normalize()` is enforced at the server boundary
  (both server adapters call it before validation); call `model.Normalize()` yourself on the
  client (e.g. before a lenient draft save) if you want normalized data pre-submit — v0.x
  doesn't ship a built-in client-side invocation hook.
- **Localization pass-through** — FluentValidation localization and `WithName` display names
  flow straight through.
- **Focus and scroll to the first error**, with accessible messaging built in.
- **Configurable validation triggers** — on-change or on-blur, debounce interval, and live
  profile choice.
- **Model swap and draft load** handled by the root component — no manual EditContext rebuild.
- **Vanilla Blazor interop** — plain `InputBase` descendants and `ValidationMessage` keep
  working alongside Formidable's own components.

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

## Learn more

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

The sample app is a runnable tour, one page per feature. From the repo root:

```bash
dotnet run --project samples/Formidable.Sample.Api
dotnet run --project samples/Formidable.Sample
```

The API listens on `http://localhost:5180`; open the Blazor app at
`http://localhost:5181`.

## License

Formidable is licensed under the [MIT License](LICENSE).

Contributions are welcome — see [CONTRIBUTING.md](CONTRIBUTING.md) before opening a pull
request.
