# Formidable

Form validation for Blazor, built on FluentValidation — profiles, progressive disclosure,
row-stable collections, and one wire format shared by client and server.

I wrote it because the layer between Blazor's EditForm and FluentValidation is one I kept
rebuilding by hand, one client form at a time. This is that layer, built once and covered by
tests, running a real project's forms today.

## What it is

- Draft and Submit from one validator — two lifecycles ship built in: a lenient Draft profile
  for save-as-you-go, a strict Submit profile for the real thing, both defined once in the same
  FluentValidation class. Wizard steps and approval stages are custom profiles over that same
  definition.
- A headless component kit — FormidableForm, FormidableField, FieldMessage, and FormSummary own
  the EditContext and render exactly what the validator reports. The library ships no CSS, so
  the kit drops into a UI library, a design system, or plain HTML.
- One validator, client and server — the same FluentValidation rules run in the browser and
  again on the server, and the server's ValidationProblemDetails response applies straight into
  the engine, so a rejected save lights up the exact fields inline.
- Progressive disclosure — errors follow what is actually on screen. A field the visitor cannot
  see never nags, and a defensive gate catches the case where every failure would otherwise go
  unseen.
- Collections that keep their errors — a message belongs to the row object, not to the row
  number, so adding, removing, and reordering rows can never move an error onto the wrong line.

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

Then wrap a model in FormidableForm and let FormidableInputText, FieldMessage, and FormSummary
render whatever the validator reports. That is a working form.

## Links

The full documentation, the runnable sample app, and every recipe live in the repository:

- Repository: https://github.com/xfunc/formidable <!-- publish-day: verify -->
- Documentation index: https://github.com/xfunc/formidable/tree/main/docs <!-- publish-day: verify -->
- Recipes (behaviour to configuration): https://github.com/xfunc/formidable/blob/main/docs/recipes.md <!-- publish-day: verify -->
- Live demo: https://xfunc.github.io/formidable/ <!-- publish-day: verify -->
- Issues and contributions: https://github.com/xfunc/formidable/blob/main/CONTRIBUTING.md <!-- publish-day: verify -->

Licensed under the MIT License.
