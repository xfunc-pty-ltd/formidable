# Quickstart

A Formidable form needs four pieces: a model, a FluentValidation validator, `FormidableForm`
to own the wiring, and a couple of components to render what the validator finds. Here they
all are, working together end to end.

The project underneath them has to be interactive. `dotnet new blazorwasm` is the assumption
these snippets make, since every page in a standalone WebAssembly app is interactive already.
On a Blazor Web App (`dotnet new blazor`, the default template) pages are statically
server-rendered until one says otherwise, so the page holding the form needs a render mode of
its own: `@rendermode InteractiveServer` or `@rendermode InteractiveWebAssembly` at the top.
Leave it off and `FormidableForm` refuses to render, naming that same fix: a form on such a
page could be filled in, but its submit would never reach the validation pipeline.

## Install

Add the Blazor package:

```bash
dotnet add package Formidable.Blazor
```

`Formidable.Blazor` carries the core `Formidable` package along as a dependency, so this one
install brings both.

## Register it

In `Program.cs`, register the engine and your FluentValidation validator. Two `using`
directives at the top of the file, two registrations beside the template's own:

```csharp
using FluentValidation;
using Formidable.Blazor;

builder.Services.AddFormidableBlazor();
builder.Services.AddScoped<IValidator<Signup>, SignupValidator>();
```

The first line registers the engine and the services the kit resolves. The second makes your
validator resolvable as `IValidator<Signup>`, which is how Formidable finds it — one line per
validator.

## The model and the validator

A plain model and a plain `AbstractValidator<T>` — nothing fancy yet. Both live in a new
`Signup.cs`:

```csharp
using FluentValidation;

public class Signup
{
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
}

public class SignupValidator : AbstractValidator<Signup>
{
    public SignupValidator()
    {
        RuleFor(s => s.Name).NotEmpty().WithMessage("Name is required");
        RuleFor(s => s.Email).Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("Email is required")
            .EmailAddress().WithMessage("Enter a valid email address");
    }
}
```

## Bring the kit into scope

Every component below lives in the `Formidable.Blazor` namespace, so one line in
`_Imports.razor` puts all of them within reach of every page:

```razor
@using Formidable.Blazor
```

Without it the compiler reads `<FormidableInputText>` as unknown markup and reports the
`@bind-Value` on it as a binding-syntax error — a diagnostic that sends you to the Razor
documentation when the only thing missing is the namespace.

## The form

Four components and nothing else: `FormidableForm` owns the `EditContext`,
`FormidableInputText` renders each field, `FormidableFieldMessage` shows that field's own
issues, and `FormidableSummary` lists everything the form currently has to say at once. Markup
and `@code` block together are one routable page — `Pages/Signup.razor`, say:

```razor
@page "/signup"

<FormidableForm Model="_signup" OnValidSubmit="HandleValid">
    <FormidableSummary />

    <label>Name
        <FormidableInputText @bind-Value="_signup.Name" />
    </label>
    <FormidableFieldMessage For="() => _signup.Name" />

    <label>Email
        <FormidableInputText @bind-Value="_signup.Email" />
    </label>
    <FormidableFieldMessage For="() => _signup.Email" />

    <button type="submit">Submit</button>
</FormidableForm>

@code {
    private readonly Signup _signup = new();

    private void HandleValid()
    {
        // ...
    }
}
```

An input names its field once, in `@bind-Value`: the Razor compiler hands it the expression
behind that binding, which is all it needs to know which field it edits, registers and styles.
`FormidableFieldMessage` renders no value of its own and so has no binding to read, which is
why it names its field the explicit way, with `For`. Inputs accept `For` too — as the override
that wins when you deliberately want an input speaking for a different field than it binds.

## Run it

Start the app and open `/signup`. Submit the empty form and both fields complain at once: the
summary lists "Name is required" and "Email is required", and each field's own
`FormidableFieldMessage` repeats its half of that list right where the field renders. Type a
name and move to the next field, and its message disappears immediately — no second submit
needed. Leave the email blank a moment longer and its message just sits there, waiting for you
to fix it.

That instant fix, with no second submit needed, is a live validation pass — and which rules
run live versus which wait for submit is exactly what core concepts covers next.

**Next:** [Core concepts](core-concepts.md)
