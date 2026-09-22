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

Blazor hands you `EditForm`. FluentValidation hands you rules. The layer in between decides which
rules run while someone is still typing, which messages they have earned the right to see, and what
to do when the server disagrees with the browser. That is the layer I kept rebuilding by hand, one
client form at a time, slightly differently each time. Formidable is that layer, built once and
covered by tests.

It runs one real client project's forms today. Exactly one: a number I'd rather give you straight
than round up. What that buys you is a library that met a deadline before it met a README, so the
awkward parts were found by shipping rather than by guessing. You can run two apps out of this repo:
the [sample](#run-the-sample-locally) (22 pages, one feature each) and
`samples/Formidable.Tutorial`, which grows a single signup form across six stages.

If you'd rather see it than read about it, there is a [live demo](#live-demo) and a
[five-minute quickstart](#5-minute-quickstart) below.

## What it is

- **Draft and Submit from one validator** — two lifecycles ship built in: a lenient Draft profile
  for save-as-you-go, a strict Submit profile for the real thing, both defined once in the same
  FluentValidation class. Wizard steps and approval stages are custom profiles over that same
  definition.
- **A headless component kit** — `FormidableForm`, `FormidableField`, `FormidableFieldMessage`, and
  `FormidableSummary` own the `EditContext` and render the validator's verdict. The library ships no
  CSS, so the same kit fits a UI library, a design system, or plain HTML.
- **One validator, client and server** — the same FluentValidation rules run in the browser and
  again on the server, and the server's `ValidationProblemDetails` response applies straight into
  the engine, so a rejected save lights up the exact fields inline.
- **Progressive disclosure** — a message waits until the visitor has earned it. A field nobody has
  touched stays silent whatever its rules say; once they have engaged it, it tells them what would
  actually block the save. At submit, which errors show is decided by what is on screen at that
  moment, and a defensive gate catches the case where every failure would otherwise go unseen.
- **Collections that keep their errors** — a message belongs to the row object, not to the row
  number, so adding, removing, and reordering rows can never move an error onto the wrong row.

## Packages

| Package | Depends on | Contents |
|---|---|---|
| `Formidable` | FluentValidation + `Microsoft.Extensions.DependencyInjection.Abstractions` | Validation profiles, `ProfiledValidator<T>` / `DraftSubmitValidator<T>`, the `IModelValidator` seam, `ValidationIssue` / `ValidationReport`, `INormalizableModel`, model introspection. No Blazor dependency. |
| `Formidable.Blazor` | `Formidable` + `Microsoft.AspNetCore.Components.Web` | The validation engine (EditContext integration, field registry, validation flows) and the headless component kit (`FormidableForm`, `FormidableField`, `FormidableFieldMessage`, `FormidableSummary`, …). |
| `Formidable.AspNetCore` | `Formidable` + ASP.NET Core | Minimal-API endpoint filter and MVC `[Validate]` action filter, returning `ValidationProblemDetails` in the same path format the Blazor client consumes. |

A shared contracts assembly (your models and validators) references `Formidable` only and stays free
of Blazor and ASP.NET Core dependencies. A Blazor client adds `Formidable.Blazor`; an API adds
`Formidable.AspNetCore`. Neither leaf package depends on the other: both depend only on
`Formidable`.

## 5-minute quickstart

Five minutes in an interactive project: `dotnet new blazorwasm`, or a Blazor Web App page carrying
`@rendermode InteractiveServer`, `@rendermode InteractiveWebAssembly` or
`@rendermode InteractiveAuto`.

> [!NOTE]
> Which render mode a page needs, and why `FormidableForm` refuses to render without one:
> [Hosting models](docs/hosting-models.md#does-the-page-need-a-render-mode).

Install the Blazor package, which carries the core `Formidable` package with it:

```bash
dotnet add package Formidable.Blazor
```

One line in `_Imports.razor` brings every component below into scope:

```razor
@using Formidable.Blazor
```

Then one page file (`Pages/Signup.razor`, whose name is the `Signup` the registration below names
its types through) holds the model, the validator, and the form. A plain `AbstractValidator<T>`, no
profiles required:

```razor
@page "/signup"
@using FluentValidation
```

The rest of the file holds the model, the validator, and the form together:

```razor
<FormidableForm Model="_contact" OnValidSubmit="HandleValid" OnInvalidSubmit="_ => _saved = false">
    <FormidableSummary />

    <label>Name
        <FormidableInputText @bind-Value="_contact.Name" />
    </label>
    <FormidableFieldMessage For="() => _contact.Name" />

    <label>Email
        <FormidableInputText @bind-Value="_contact.Email" />
    </label>
    <FormidableFieldMessage For="() => _contact.Email" />

    <button type="submit">Submit</button>
</FormidableForm>

@if (_saved)
{
    <p>Saved.</p>
}

@code {
    private readonly Contact _contact = new();
    private bool _saved;

    private void HandleValid()
    {
        _saved = true;
    }

    public class Contact
    {
        public string Name { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
    }

    public class ContactValidator : AbstractValidator<Contact>
    {
        public ContactValidator()
        {
            RuleFor(c => c.Name).NotEmpty().WithMessage("Name is required");
            RuleFor(c => c.Email).Cascade(CascadeMode.Stop)
                .NotEmpty().WithMessage("Email is required")
                .EmailAddress().WithMessage("Enter a valid email address");
        }
    }
}
```

<!-- Excerpt from `samples/Formidable.Tutorial/Pages/Stage1.razor` -->

Two registrations in `Program.cs` finish it, `using` directives included. The model and validator
are nested in the page class, so they are named through it:

```csharp
using FluentValidation;
using Formidable.Blazor;
using YourApp.Pages;

builder.Services.AddFormidableBlazor();
builder.Services.AddScoped<IValidator<Signup.Contact>, Signup.ContactValidator>();
```

That `using` is the page's own namespace, which each template decides by where it puts the page
file: `YourApp.Pages` in a standalone WebAssembly app, `YourApp.Components.Pages` in a Blazor Web
App, and `YourApp.Client.Pages` in the `.Client` project of one created with
`dotnet new blazor -int Auto` or `-int WebAssembly`.

> [!NOTE]
> A two-project Blazor Web App registers in **both** `Program.cs` files, because the server builds
> the form too whenever a page prerenders or runs on its circuit:
> [Hosting models](docs/hosting-models.md#the-server-builds-the-form-too).

That is the whole form. `FormidableForm` owns the `EditContext`, `FormidableInputText` registers its
field and applies the validation CSS classes, and `FormidableFieldMessage` and `FormidableSummary`
render the validator's verdict. `Saved.` appears once a submit lands, and `OnInvalidSubmit` clears
it the moment a later one is blocked.

Two places take this form further:

- **[Quickstart](docs/quickstart.md)** takes the same four pieces slowly, with each line explained
  and a run at the end.
- **[The sample's Quickstart page](samples/Formidable.Sample/Pages/Quickstart.razor)**, the app's
  home page, is this same form grown up a little, with the model in a shared project and the handler
  in a code-behind.

## Server validation in two lines

The rules you just wrote run on the server too, and they answer in the exact format the client
already knows how to apply. Minimal APIs:

```csharp
var orders = app.MapGroup("/api/orders").Validate<RoundTripOrder>();
```

<!-- Source: `samples/Formidable.Sample.Api/Program.cs` -->

MVC controllers:

```csharp
[Validate] // class-level: every action's validatable arguments run the Submit profile
```

<!-- Source: `samples/Formidable.Sample.Api/Controllers/AgreementsController.cs` -->

Both filters return `ValidationProblemDetails`. On the client, deserialize the response and hand it
to the form's `ApplyServerIssues(...)`, which lands each issue on the field it names. Guard that
deserialize: a 400 can come from a proxy or a gateway rather than from the endpoint, and what those
send is no verdict, often not JSON at all. The wire contract the filters share and
[that guard](docs/server-integration.md#what-if-the-400-is-not-formidables) are both in
[Server integration](docs/server-integration.md).

## Documentation

The docs come in two tiers. The first is a path: read it in order and you will have built the thing.
The second is the shelf you come back to afterwards.

### Learn the library

Eight documents, in reading order. Start at the top and keep going.

| Doc | What's in it |
|---|---|
| [Why Formidable](docs/why-formidable.md) | Why this layer exists, with one row per workaround it replaces. |
| [Quickstart](docs/quickstart.md) | A model, a validator, and the four components that render it. |
| [Draft and submit rules](docs/tutorial/2-draft-and-submit.md) | One validator carrying draft rules and submit rules, a draft save that demands nothing, and field state made visible. |
| [A warning alongside an error](docs/tutorial/3-severity.md) | A rule that advises instead of blocking, and a submit that goes through with the warning still showing. |
| [A list of members](docs/tutorial/4-collections.md) | Rows that keep their errors, a rule for the list itself, and the notify a page-driven edit owes. |
| [Async rules](docs/tutorial/5-async.md) | An async rule that asks a service for an answer, and the pending state that shows while it waits. |
| [The server round trip](docs/tutorial/6-server.md) | The same validator running on the server, and a rejected submit landing back on the fields it names. |
| [Recipes](docs/recipes.md) | "I want to…" answered with code, then what explains it, plus a sample where one exists. |

### Reference

The deep dives, grouped the way the concepts stack. The last column is for arriving with a symptom
rather than a topic: find your own words there and follow the link.

| Area | Doc | What it covers | Come here when… |
|---|---|---|---|
| Concepts | [Profiles](docs/profiles.md) | Draft, Submit, and profiles you define yourself: how a pass picks its rules. | [the form should stay silent until Submit](docs/recipes.md#i-want-to-validate-while-typing-on-blur-or-only-at-submit), [I want to save a half-finished form](docs/profiles.md#which-profile-runs-when), [I need a profile beyond Draft and Submit](docs/profiles.md#how-do-i-define-a-custom-profile) |
| Concepts | [Severity](docs/severity.md) | Errors block a submit; warnings and infos say their piece and let it through. | [a rule should warn without blocking the submit](docs/severity.md#need-to-know), [the submit went through with a warning showing](docs/severity.md#does-a-warning-or-an-info-block-the-submit), [I want warnings styled apart from errors](docs/severity.md#where-do-warnings-and-infos-show-and-how-do-i-style-them) |
| Concepts | [Disclosure](docs/disclosure.md) | Render-registration in full: why a submit's issue surfaces only where its field is mounted. | [a field says nothing until Submit is pressed](docs/disclosure.md#why-isnt-my-message-showing-yet), [Submit is blocked but no message shows](docs/disclosure.md#why-is-the-submit-blocked-with-no-message-in-sight), [a message lingered after I fixed the value](docs/disclosure.md#why-is-it-still-showing-and-when-does-it-clear), [a record I loaded no longer passes, but the form opens with no message](docs/recipes.md#i-want-to-open-a-form-on-values-the-visitor-did-not-type) |
| Fields & collections | [Collections and row identity](docs/collections-and-row-identity.md) | How a message stays attached to its row through add, remove, and reorder. | [a message landed on the wrong row](docs/collections-and-row-identity.md#why-did-my-message-move-rows), [a rule on the list itself shows nowhere](docs/collections-and-row-identity.md#where-does-a-collection-level-rules-message-go), [I have a list inside a list](docs/collections-and-row-identity.md#how-do-i-nest-one-collection-inside-another) |
| Fields & collections | [Component kit](docs/component-kit.md) | Every component and parameter, plus the seams for foreign and native controls. | [I want Formidable inside my own EditForm](docs/component-kit.md#formidablevalidatortmodel-attaching-to-an-existing-form), [I want the summary in page order](docs/component-kit.md#the-order-entries-appear-in), [I have a control the kit lacks](docs/component-kit.md#the-foreign-control-pattern) |
| Async & server | [Async validation](docs/async-validation.md) | When an async rule runs, what happens to a check whose value has gone stale, and which fields show "checking…". | [the check fires on every keystroke](docs/async-validation.md#how-do-i-stop-the-check-firing-on-every-keystroke), [the same value was checked twice](docs/async-validation.md#why-did-the-same-value-get-checked-twice-and-how-do-i-stop-it), [the check passed but no green appeared](docs/async-validation.md#why-isnt-my-field-green-even-though-the-check-passed) |
| Async & server | [Server integration](docs/server-integration.md) | Two entry points, the endpoint filter and the `[Validate]` attribute, sharing one wire format. | [a server error stayed after my edit](docs/server-integration.md#what-happens-to-a-server-error-when-i-edit-the-field), [I need the 400's exact shape](docs/server-integration.md#how-does-a-400-reach-the-client-and-what-shape-must-it-have), [parsing a 400 threw in my submit handler](docs/server-integration.md#what-if-the-400-is-not-formidables) |
| Presentation | [CSS and accessibility](docs/css-and-accessibility.md) | Formidable computes the class names and wires the ARIA; both are yours to override. | [messages show but nothing turns red or green](docs/css-and-accessibility.md), [I want the exact condition for green](docs/css-and-accessibility.md#what-puts-green-on-a-field), [nothing took focus on a blocked submit](docs/css-and-accessibility.md#why-did-nothing-take-focus) |
| Presentation | [Options](docs/options.md) | `FormidableOptions` property by property, from the debounce to the CSS class map. | [I want Save disabled until the form validates](docs/options.md#trackformvalidity), [I want the required asterisk gone](docs/options.md#showrequiredindicators), [a conditionally required field shows no asterisk](docs/options.md#requiredoverride) |
| Project | [Migration guide](docs/migration-guide.md) | Moving an existing FluentValidation and `EditForm` integration across. | [I want to keep the EditForm I have](docs/migration-guide.md#two-ways-to-attach), [something behaves differently after migrating](docs/migration-guide.md#what-to-check-after-migrating), [my EditForm's summary is in rule order](docs/migration-guide.md#attach-mode-lists-issues-in-the-engines-order-not-the-pages) |
| Project | [Troubleshooting](docs/troubleshooting.md) | Symptom, cause and fix, ordered build-time to run-time. | [it will not build](docs/troubleshooting.md), [the server's error never appears](docs/troubleshooting.md), [my symptom is not in this column](docs/troubleshooting.md) |
| Project | [Testing](docs/testing.md) | The tests you write over a form you built, and this repo's own suite: its two tiers and the gate a release has to pass. | [I want to test rules without Blazor](docs/testing.md#the-rules-without-blazor), [I want the form under bUnit](docs/testing.md#the-form-under-bunit), [my test has to wait out a debounce](docs/testing.md#faking-the-clock) |
| Project | [How the engine works](docs/how-the-engine-works.md) | For contributors, and for anyone curious about the machinery: the engine in its own vocabulary. Nobody needs it to use the library. | [no other page answered my question](docs/how-the-engine-works.md) |
| Project | [Releasing](docs/releasing.md) | The maintainer's runbook for cutting a version. |  |
| Project | [Manual checklist](samples/MANUAL-CHECKLIST.md) | The eyes-on walkthrough of the sample app, one check per behaviour. |  |
| Project | [Changelog](CHANGELOG.md) | What shipped, release by release. |  |

## Run the sample locally

The sample app is a runnable tour: one page per feature, each carrying the real source that makes it
work.

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

The API listens on `http://localhost:5180`; open the Blazor app at `http://localhost:5181`.

## Live demo

The same sample runs on GitHub Pages, deployed from `main` by a manual workflow run. A simulated
in-browser API stands in for the real server; every other page behaves exactly as it does locally.

**[xfunc.github.io/formidable](https://xfunc.github.io/formidable/)** <!-- publish-day: verify -->

## Contributing, security, and license

Contributions are welcome, and [CONTRIBUTING.md](CONTRIBUTING.md) is where to start: project layout,
dev setup, and what to do before opening a pull request. Found a security issue?
[SECURITY.md](SECURITY.md) says how to report it privately.

Formidable is licensed under the [MIT License](LICENSE).
