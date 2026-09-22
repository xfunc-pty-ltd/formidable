# Quickstart

Three files stand between an empty project and a validated form: one page file holding the model,
the validator and the form together, one line in `_Imports.razor`, and two registrations in
`Program.cs`. Here they all are, end to end.

The project underneath them has to be interactive. `dotnet new blazorwasm` is the assumption these
snippets make, since every page in a standalone WebAssembly app is interactive already. On a
Blazor Web App (`dotnet new blazor`, the default template) pages are statically server-rendered
until one says otherwise, so the page holding the form needs a render mode of its own:
`@rendermode InteractiveServer` or `@rendermode InteractiveWebAssembly` at the top. Leave it off
and `FormidableForm` refuses to render, naming that same fix: a form on such a page could be
filled in, but its submit would never reach the validation pipeline.

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
namespace — `YourApp.Pages` under the default template.

## Run it

Start the app and open `/signup`. Submit the empty form and both fields complain at once: the
summary lists "Name is required" and "Email is required", and each field's own
`FormidableFieldMessage` repeats its half of that list right where the field renders. Type a name
and move to the next field, and its message disappears immediately — no second submit needed.
Leave the email blank a moment longer and its message just sits there, waiting for you to fix it.
That instant fix on the name field, with no second submit needed, is a live validation pass.

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
