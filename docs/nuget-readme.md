# Formidable

Form validation for Blazor, built on FluentValidation — profiles, progressive disclosure,
row-stable collections, and one wire format shared by client and server.

## What it is

- Draft/Submit orthogonality — one validator definition, two built-in lifecycles: a lenient
  Draft profile for save-as-you-go, and a strict Submit profile for the real thing. Define
  further custom profiles from the same validator.
- A headless component kit — FormidableForm, FormidableField, FieldMessage, and FormSummary
  own the EditContext and render whatever the validator reports. Formidable ships no CSS; it
  drops into any UI library or plain HTML.
- One validator, client and server — the same FluentValidation rules run in the browser and on
  the server; a ValidationProblemDetails response applies straight into the engine so a
  server-rejected save lights up the exact fields inline.
- Progressive disclosure — validation errors follow what's actually rendered; a field the user
  can't see never nags, and a defensive gate catches the case where every failure would
  otherwise go unseen.

## Packages

- Formidable — validation profiles, ProfiledValidator/DraftSubmitValidator, the model
  validator seam, ValidationIssue/ValidationReport, INormalizableModel. FluentValidation only,
  no Blazor dependency.
- Formidable.Blazor — the validation engine (EditContext integration, field registry,
  validation flows) and the headless component kit.
- Formidable.AspNetCore — minimal-API endpoint filter and MVC action filter, returning
  ValidationProblemDetails in the same path format the Blazor client consumes.

## Quickstart

Install the Blazor package:

    dotnet add package Formidable.Blazor

Register it and your FluentValidation validators:

    builder.Services.AddFormidableBlazor();
    builder.Services.AddScoped<IValidator<QuickContact>, QuickContactValidator>();

Then wrap a model in FormidableForm and let FormidableInputText / FieldMessage / FormSummary
render whatever the validator reports.

## Links

Full documentation, the runnable sample app, and every recipe live in the repository:

- Repository: https://github.com/xfunc/formidable <!-- publish-day: verify -->
- Documentation index: https://github.com/xfunc/formidable/tree/main/docs <!-- publish-day: verify -->
- Recipes (behaviour to configuration): https://github.com/xfunc/formidable/blob/main/docs/recipes.md <!-- publish-day: verify -->
- Live demo: https://xfunc.github.io/formidable/ <!-- publish-day: verify -->
- Issues and contributions: https://github.com/xfunc/formidable/blob/main/CONTRIBUTING.md <!-- publish-day: verify -->

Licensed under the MIT License.
