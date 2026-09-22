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
- A headless component kit — FormidableForm, FormidableField, FormidableFieldMessage, and
  FormidableSummary own the EditContext and render the validator's verdict. The library ships no
  CSS, so the kit drops into a UI library, a design system, or plain HTML.
- One validator, client and server — the same FluentValidation rules run in the browser and
  again on the server, and the server's ValidationProblemDetails response applies straight into
  the engine, so a rejected save lights up the exact fields inline.
- Progressive disclosure — a message waits until the visitor has earned it. A field nobody has
  touched stays silent whatever its rules say; once they have engaged it, it tells them what
  would actually block the save. A form opened on a saved draft says what those loaded values
  have already earned in one call. At submit, which errors show is decided by what is on screen
  at that moment, and a defensive gate catches the case where every failure would otherwise go
  unseen.
- Collections that keep their errors — a message belongs to the row object, not to the row
  number, so adding, removing, and reordering rows can never move an error onto the wrong line.

## Packages

- Formidable — validation profiles, ProfiledValidator/DraftSubmitValidator, the model
  validator seam, ValidationIssue/ValidationReport, INormalizableModel. FluentValidation plus
  Microsoft.Extensions.DependencyInjection.Abstractions, no Blazor dependency.
- Formidable.Blazor — the validation engine (EditContext integration, field registry,
  validation flows) and the headless component kit.
- Formidable.AspNetCore — minimal-API endpoint filter and MVC action filter, returning
  ValidationProblemDetails in the same path format the Blazor client consumes.

## Quickstart

Install the Blazor package:

    dotnet add package Formidable.Blazor

Register it and your FluentValidation validators in Program.cs, using directives included:

    using FluentValidation;
    using Formidable.Blazor;

    builder.Services.AddFormidableBlazor();
    builder.Services.AddScoped<IValidator<QuickContact>, QuickContactValidator>();

Add one line to _Imports.razor so the components resolve:

    @using Formidable.Blazor

Then wrap a model in FormidableForm and let FormidableInputText, FormidableFieldMessage, and
FormidableSummary render the validator's verdict. That is a working form, on any page
with an interactive render mode (every page of a standalone WebAssembly app, or a Blazor Web
App page carrying @rendermode InteractiveServer, @rendermode InteractiveWebAssembly or
@rendermode InteractiveAuto).

A Blazor Web App is two projects when it is created by dotnet new blazor -int Auto or by
dotnet new blazor -int WebAssembly. It has a server project and a .Client project, each with its
own Program.cs, and both of them need the same two lines: the server builds the form whenever the
page prerenders (on by default) or runs on the server's circuit, as InteractiveServer does on
every visit and InteractiveAuto does on the first one. Only a page written InteractiveWebAssembly
with prerendering turned off skips the server entirely. Register on the client alone anywhere
else and the server has no validator to resolve.

## Links

The full documentation, the runnable sample app, and every recipe live in the repository:

- Repository: https://github.com/xfunc/formidable <!-- publish-day: verify -->
- Documentation index: https://github.com/xfunc/formidable/tree/main/docs <!-- publish-day: verify -->
- Recipes (behaviour to configuration): https://github.com/xfunc/formidable/blob/main/docs/recipes.md <!-- publish-day: verify -->
- Live demo: https://xfunc.github.io/formidable/ <!-- publish-day: verify -->
- Issues and contributions: https://github.com/xfunc/formidable/blob/main/CONTRIBUTING.md <!-- publish-day: verify -->

Licensed under the MIT License.
