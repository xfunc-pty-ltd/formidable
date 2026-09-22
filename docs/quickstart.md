# Quickstart

A Formidable form needs four pieces: a model, a FluentValidation validator, `FormidableForm`
to own the wiring, and a couple of components to render what the validator finds. Here they
all are, working together end to end.

## Install

Add the Blazor package:

```bash
dotnet add package Formidable.Blazor
```

`Formidable.Blazor` carries the core `Formidable` package along as a dependency, so this one
install brings both.

## Register it

In `Program.cs`, register the engine and your FluentValidation validator:

```csharp
builder.Services.AddFormidableBlazor();
builder.Services.AddScoped<IValidator<Signup>, SignupValidator>();
```

## The model and the validator

A plain model and a plain `AbstractValidator<T>` — nothing fancy yet:

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

## The form

Four components and nothing else: `FormidableForm` owns the `EditContext`,
`FormidableInputText` renders each field, `FormidableFieldMessage` shows that field's own
issues, and `FormidableSummary` lists everything the form currently has to say at once.

```razor
<FormidableForm Model="_signup" OnValidSubmit="HandleValid">
    <FormidableSummary />

    <label>Name
        <FormidableInputText For="() => _signup.Name" @bind-Value="_signup.Name" />
    </label>
    <FormidableFieldMessage For="() => _signup.Name" />

    <label>Email
        <FormidableInputText For="() => _signup.Email" @bind-Value="_signup.Email" />
    </label>
    <FormidableFieldMessage For="() => _signup.Email" />

    <button type="submit">Submit</button>
</FormidableForm>
```

```csharp
private readonly Signup _signup = new();

private void HandleValid()
{
    // ...
}
```

## Run it

Submit the empty form and both fields complain at once: the summary lists "Name is required"
and "Email is required", and each field's own `FormidableFieldMessage` repeats its half of that
list right where the field renders. Type a name and move to the next field, and its message
disappears immediately — no second submit needed. Leave the email blank a moment longer and
its message just sits there, waiting for you to fix it.

That instant fix, with no second submit needed, is a live validation pass — and which rules
run live versus which wait for submit is exactly what core concepts covers next.

**Next:** [Core concepts](core-concepts.md)
