# Engine options

**You should already know:** the live/submit split and why one validator serves both moments
([Core concepts](core-concepts.md)), and the debounced refresh a submitted form runs on
every further edit ([Async validation](async-validation.md)).

Every default is somebody's opinion about how your form should behave, and the moment a form
disagrees, that opinion becomes your problem. A 300ms debounce is well-mannered for a typical
form and needlessly chatty for one whose async rule takes two seconds to answer. A field hidden
behind a wizard step that genuinely can't have rendered yet still deserves to be forced visible
sometimes, whatever the field registry says. Formidable picks sensible defaults so a form works
out of the box, and hands every one of them back through `FormidableOptions` — pass it to
`FormidableForm<TModel>`'s `Options` parameter, and the defaults stop being fixed.

## Need to know

Every property on `FormidableOptions` has a default, so omitting `Options` entirely (the form
falls back to `new FormidableOptions()`) is a fully working configuration:

```razor
<FormidableForm Model="_request" Options="_options" OnValidSubmit="HandleValid"
                @ref="_form" id="@FormGateId" tabindex="-1">
```

*Excerpt from `samples/Formidable.Sample/Pages/Disclosure.razor`*

`Options` is the parameter this page is quoted for. The `id` and `tabindex` beside it are the
sample's own: they give the model-level field that the defensive gate reports under an element
to focus (see [CSS and accessibility](css-and-accessibility.md)). Any attribute
`FormidableForm<TModel>` does not recognise is splatted onto the `<form>` element it renders.

Here's the one gotcha worth knowing before anything else: `FormidableForm<TModel>` builds its
engine once per `Model` instance and passes `Options` straight into the engine's constructor at
that point. The engine never re-reads the `Options` *parameter* on a later render, so handing
the form a whole new `FormidableOptions` instance without also swapping `Model` has no effect.
The engine does keep re-reading that instance's *properties* on every pass, though: mutating
`LiveProfile`, `RefreshDebounce`, `DisclosureOverride`, or any other property on the same object
takes effect starting with the next validation pass. See
[Recipes](recipes.md#i-want-profiles-of-my-own) for a worked case. Building the
`FormidableOptions` once, up front, and leaving it alone for the life of the rendered form — as
the sample further below does — is still the simplest habit to default to.

That's the contract. What follows is every property, what it defaults to, and where the sample
demonstrates it.

## Properties

### `LiveProfile`

`ValidationProfile`, defaults to `ValidationProfile.Draft`. The profile every live pass — one
per field change — validates against. See [Profiles](profiles.md).

### `SubmitProfile`

`ValidationProfile`, defaults to `ValidationProfile.Submit`. The profile the submit pipeline
validates against, and the profile the debounced post-submit refresh re-validates against. See
[Profiles](profiles.md).

### `RefreshDebounce`

`TimeSpan`, defaults to 300 ms. How long the engine waits, after a field change once a submit
has happened, before re-running `SubmitProfile` to refresh inline errors.

### `DisclosureOverride`

`Func<ValidationIssue, bool?>?`, defaults to `null`. A tri-state override consulted per issue:
return `true` to force it visible, `false` to force it suppressed, or `null` to defer to the
field registry (whether a rendered field claimed that path). Model-level issues — an empty
`Path` — are always visible unless the override returns `false`.

### `SuppressedIssueDiagnostic`

`Action<ValidationIssue>?`, defaults to `null`. Invoked once per error-severity issue that
submit suppresses because no rendered field registration matches it and `DisclosureOverride`
didn't force it visible. A `Trace`-output warning is written for every suppression regardless of
whether this callback is set — the callback is for surfacing suppressions in your own UI or
telemetry, not the only place they get recorded.

### `CssClasses`

`FormidableCssOptions`, defaults to a new instance. Class names field components and native
`InputBase` descendants apply based on field state:

| Property | Applied when | Default |
|---|---|---|
| `Invalid` | the field has error-severity issues | `formidable-invalid` |
| `Valid` | the field is touched or modified and has no errors | `formidable-valid` |
| `Pending` | a validation pass involving the field is in flight | `formidable-pending` |

`Pending` appends alongside `Invalid`/`Valid` rather than replacing it — see
[CSS and accessibility](css-and-accessibility.md) for how the three compose.

## `FormidableOptions`, built once

Since a new `Options` instance only takes effect together with a new `Model` instance, the
simplest habit is building `FormidableOptions` once, up front, and never reassigning it — exactly
what the sample below does:

```csharp
    protected override void OnInitialized()
    {
        _options = new FormidableOptions
        {
            SuppressedIssueDiagnostic = issue =>
            {
                _suppressed.Add($"{issue.Path}: {issue.Message}");
                _ = InvokeAsync(StateHasChanged);
            }
        };
    }
```

*Excerpt from `samples/Formidable.Sample/Pages/Disclosure.razor.cs`* — `_options` is a
`FormidableOptions?` field on the page, built once here and never reassigned.

## Where each option is demonstrated

- `SuppressedIssueDiagnostic` and `DisclosureOverride` — the progressive disclosure sample
  (`/disclosure`) and [Disclosure](disclosure.md).
- `CssClasses` — [CSS and accessibility](css-and-accessibility.md); remapped onto a UI
  library's own classes (`/bootstrap`) and recoloured live via CSS custom properties
  (`/css-colours`).
- `LiveProfile` / `SubmitProfile` — [Profiles](profiles.md) and the `/profiles`
  sample (the sample relies on the defaults; it doesn't override them).

**Sample:** [`/disclosure`](../samples/Formidable.Sample/Pages/Disclosure.razor)
