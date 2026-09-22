# Quickstart

Three files stand between an empty project and a validated form: one page file holding the model,
the validator and the form together, one line in `_Imports.razor`, and two registrations in
`Program.cs`. Here they all are, end to end.

The project underneath them has to be interactive. `dotnet new blazorwasm` is the assumption these
snippets make, since every page in a standalone WebAssembly app is interactive already. On a
Blazor Web App (`dotnet new blazor`, the default template) pages are statically server-rendered
until one says otherwise, so the page holding the form needs a render mode of its own:
`@rendermode InteractiveServer`, `@rendermode InteractiveWebAssembly` or
`@rendermode InteractiveAuto` at the top. Leave it off and `FormidableForm` refuses to render,
asking for a render mode in its own message: a form on such a page could be filled in, but its
submit would never reach the validation pipeline.

Prerendering is on by default under all three of those modes, so the page is rendered on the server
and sent as HTML a moment before it becomes interactive. A submit that lands inside that window
posts natively, and the server answers it with the platform's own 400: *"The POST request does not
specify which form is being submitted."* The render-mode guard cannot help there, because
interactivity genuinely is coming, and the fix that message proposes (a `FormName` on `EditForm`)
is not a parameter `FormidableForm` carries. The ordinary answer is a submit button that stays
disabled until the page reports itself interactive. [Hosting models](#hosting-models) covers what
else prerendering changes.

## Install

Add the Blazor package:

```bash
dotnet add package Formidable.Blazor
```

`Formidable.Blazor` carries the core `Formidable` package along as a dependency, so this one
install brings both.

## Bring the kit into scope

Every component below lives in the `Formidable.Blazor` namespace, so one line in `_Imports.razor`
puts all of them within reach of every page:

```razor
@using Formidable.Blazor
```

Without it every `<Formidable…>` element is read as unknown markup. Razor does say so, and it
even names the fix, but it says it as a *warning*: `RZ10012`, one per element. Whether the build
fails at all, and what it fails with, depends on what else the page does with those elements. A
`@bind-Value` on one is reported as a binding-syntax error (`RZ9991`). A fragment body reading
`context`, or whatever a `Context="…"` renamed it to, is reported as `CS0103` on that name, at
the line that reads it. A page doing neither compiles clean, unless the project promotes warnings
to errors, and then ships elements the browser treats as unknown: your own labels and headings
inside them still render, and the components produce no output of their own. All three are the
one missing line, and [Troubleshooting](recipes.md#part-2-troubleshooting) lists them by symptom.

## The page

One file, `Pages/Signup.razor`: a plain model, a plain `AbstractValidator<T>`, and the four
components that render what the validator finds. `FormidableForm` owns the `EditContext`,
`FormidableInputText` renders each field, `FormidableFieldMessage` shows that field's own issues,
and `FormidableSummary` lists everything the form currently has to say at once.

```razor
@page "/signup"
@using FluentValidation

<FormidableForm Model="_contact" OnValidSubmit="HandleValid">
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

@code {
    private readonly Contact _contact = new();

    private void HandleValid()
    {
        // Save it.
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

The `@using FluentValidation` at the top is there because the validator itself lives in this file;
a project that keeps its validators elsewhere doesn't need it on the page.

An input names its field once, in `@bind-Value`: the Razor compiler hands it the expression behind
that binding, which is all it needs to know which field it edits, registers and styles.
`FormidableFieldMessage` renders no value of its own and so has no binding to read, which is why it
names its field the explicit way, with `For`. Inputs accept `For` too — as the override that wins
when you deliberately want an input speaking for a different field than it binds.

## Register it

Two registrations in `Program.cs`, beside the template's own:

```csharp
using FluentValidation;
using Formidable.Blazor;
using YourApp.Pages;

builder.Services.AddFormidableBlazor();
builder.Services.AddScoped<IValidator<Signup.Contact>, Signup.ContactValidator>();
```

The first line registers the engine and the services the kit resolves. The second makes your
validator resolvable as `IValidator<Contact>`, which is how Formidable finds it — one line per
validator. The model and the validator are nested inside the page class here, so they are named
through it (`Signup.Contact`, `Signup.ContactValidator`) and the `using` is the page's own
namespace, which each template decides by where it puts the page file:

- `YourApp.Pages` for a standalone WebAssembly app (`dotnet new blazorwasm`), whose pages sit in
  `Pages/`.
- `YourApp.Components.Pages` for a Blazor Web App (`dotnet new blazor`), whose pages sit in
  `Components/Pages/`.
- `YourApp.Client.Pages` for a page in the `.Client` project a Blazor Web App gets from
  `dotnet new blazor -int Auto` or `-int WebAssembly`.

In those two-project Web Apps the server builds the form as well, whenever the page prerenders (on
by default) or runs on the server's circuit, which `InteractiveServer` does every visit and
`InteractiveAuto` does on the first one. So both registrations go in **both** `Program.cs` files.
Register on the client alone and the server has no validator to resolve, so building the form
throws `No IModelValidator<Contact> is registered` — naming a call the client project already
makes. [Hosting models](#hosting-models) has the one page shape the server never builds.

## Run it

Start the app and open `/signup`. Submit the empty form and both fields complain at once: the
summary lists "Name is required" and "Email is required", and each field's own
`FormidableFieldMessage` repeats its half of that list right where the field renders. Type a name
and move to the next field, and its message disappears immediately — no second submit needed.
Leave the email blank a moment longer and its message just sits there, waiting for you to fix it.
That instant fix on the name field, with no second submit needed, is a live validation pass.

## Hosting models

A standalone WebAssembly app runs one way: every page is interactive from the moment it loads, and
the single `Program.cs` you registered in is the container every form resolves from. A Blazor Web
App has more moving parts, and four of them are better met here than in a stack trace.

**The server builds the form whenever a page prerenders or runs on a circuit.** A Web App created
with `dotnet new blazor -int Auto` or `-int WebAssembly` has a server project and a `.Client`
project, each with a `Program.cs` of its own, and two routes take the form through the server's
container. Prerendering builds it there before any runtime has started, and it is on unless a page
turns it off; a render mode that runs on the server's circuit builds it there too, which
`InteractiveServer` does on every visit and `InteractiveAuto` does on a first visit while the
WebAssembly runtime downloads. So the server project needs `AddFormidableBlazor()` and the same
validator registrations the client project makes. One page shape escapes both routes and only one
— `@rendermode @(new InteractiveWebAssemblyRenderMode(prerender: false))`, which has no prerender
pass and no circuit to fall back to, so the browser builds the form alone. A Web App created with
`-int Server` has one project and one container, so the question never comes up.

**The form is built twice per visit.** Prerendering renders the page once on the server and again
when interactivity starts, so a single visit constructs the engine twice and resolves the validator
twice. With `TrackFormValidity` on, the probe the engine runs at construction runs on the
prerendered pass too, so a validator with a slow async rule pays for it on a render that is about to
be replaced. None of this is a fault to fix, but a team counting validator constructions should know
why the number is two. Writing the render mode the long way
(`@rendermode @(new InteractiveServerRenderMode(prerender: false))`) is what turns prerendering off,
and the count goes to one.

**Nothing reaches the browser until interactivity does.** `OnAfterRenderAsync` does not run on the
prerendered pass, and a JavaScript call issued earlier throws, in the platform's own words:
*"JavaScript interop calls cannot be issued at this time. This is because the component is being
statically rendered."* The library's browser-side work waits accordingly — the displaced-click guard
installs on the first interactive render, and a summary lists issues in validator order until the
first field-order resolve lands. Interop of your own belongs in `OnAfterRenderAsync` for the same
reason.

**Culture is a WebAssembly step, and it belongs to the app.** A WebAssembly app fixes its culture
before `RunAsync()` and downloads its satellite resource assemblies for that one, so an app offering
a language choice has to apply the stored choice there rather than from a page afterwards.
Formidable ships nothing for it, deliberately, and the sample writes the few lines in the open. A
Blazor Server host takes its culture from the request and needs no boot-time step at all. See
[Culture at WebAssembly boot](component-kit.md#culture-at-webassembly-boot).

## As the form grows

One file is the right shape for one small form, and the wrong shape for the fourth page that wants
the same model. Move the model and the validator into files of their own as soon as anything else
needs them: the registration loses its `Signup.` prefix, and nothing else about the form changes.
Move the `@code` block into a `Signup.razor.cs` code-behind once it holds more than a field and a
handler. Every page in the sample app is built that way. Its
[Quickstart page](../samples/Formidable.Sample/Pages/Quickstart.razor), which is the app's home
page, is this same form after exactly that split: the model and validator in a shared project, the
handler in a code-behind.

What decides when a rule gets to speak — and how a form says what a submit needs without nagging
about fields nobody has touched — is exactly what core concepts covers next.

**Next:** [Core concepts](core-concepts.md)
