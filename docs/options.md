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
                @ref="_form">
```

*Excerpt from `samples/Formidable.Sample/Pages/Disclosure.razor`*

`Options` is the parameter this page is quoted for. Any attribute `FormidableForm<TModel>` does
not recognise is splatted onto the `<form>` element it renders — except `id` and `tabindex`,
which `FormidableForm` always sets itself, so the all-suppressed defensive gate's summary entry
has an element to focus without this page (or any other) wiring it up (see
[CSS and accessibility](css-and-accessibility.md)).

Here's the one rule worth knowing before anything else: `FormidableForm<TModel>` builds its
engine once per `Model` instance and passes `Options` straight into the engine's constructor at
that point. The engine never re-reads the `Options` *parameter* on a later render, so handing the
form a whole new `FormidableOptions` instance without also swapping `Model` throws — the form
will not accept an instance it has no way to honour.
The engine does keep re-reading that instance's *properties* on every pass, though: mutating
`LiveProfile`, `RefreshDebounce`, `DisclosureOverride`, or any other property on the same object
takes effect starting with the next validation pass. See
[Recipes](recipes.md#i-want-profiles-of-my-own) for a worked case. Building the
`FormidableOptions` once, up front, and leaving it alone for the life of the rendered form — as
the sample further below does — is what the rule asks for.

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
telemetry, not the only place they get recorded. When the host resolved an `ILoggerFactory`
(both Blazor components do so automatically when one is registered), the same suppression also
logs a `LogWarning` — WASM's default logging provider is the browser console, so this is the
channel that needs no consumer wiring at all to be seen.

### `CssClasses`

`FormidableCssClasses`, defaults to a new instance. Class names field components and native
`InputBase` descendants apply based on field state:

| Property | Applied when | Default |
|---|---|---|
| `Invalid` | the field has error-severity issues | `formidable-invalid` |
| `Valid` | the field is touched or modified and has no errors | `formidable-valid` |
| `Pending` | a validation pass involving the field is in flight | `formidable-pending` |

`Pending` appends alongside `Invalid`/`Valid` rather than replacing it — see
[CSS and accessibility](css-and-accessibility.md) for how the three compose.

## `UpdateOn` (per input, not a `FormidableOptions` property)

Every property above tunes the engine as a whole, through `FormidableOptions`. `UpdateOn` tunes a
single input instead: it's a parameter on `FormidableInputBase<TValue>` (see [Component
kit](component-kit.md#formidableinputtext-and-formidableinputbasetvalue)), not a member of
`FormidableOptions`, so it isn't set through `Options` and doesn't appear in the properties list
above — it earns a place on this page anyway because it answers the other half of "when does a
rule get to answer": `RefreshDebounce` governs the post-submit refresh's timing, and `UpdateOn`
governs a live pass's.

`InputUpdateMode.OnChange` (default) commits the value and notifies the engine together, on the
element's `change` event. `InputUpdateMode.OnInput` commits the same pair on every keystroke
instead. `InputUpdateMode.OnBlur` splits the pair across two events: the value commits on
`change`, but the engine isn't notified until `blur` — for a native control whose `change` event
fires more than once per logical edit (a date input, segment by segment, is the clearest case),
so the live pass a notification starts waits for the value to actually settle instead of running
on a value still being typed.

```razor
<FormidableInputDate @bind-Value="Model.EventDate"
                     UpdateOn="InputUpdateMode.OnBlur" />
```

`OnBlur` is the one mode that binds an event a page may already want for itself, so it chains
rather than claims: an input carrying its own splatted `@onblur` runs that handler first, awaits
it, and notifies the engine afterwards. A field that marks itself touched on blur keeps doing so
after the mode is switched on.

**Read:** [Recipes](recipes.md#i-want-to-validate-while-typing-on-blur-or-only-at-submit) for the
full behaviour table across all three modes and both rule buckets, and [Component
kit](component-kit.md#formidableinputdatetvalue) for why `FormidableInputDate` in particular
prefers this mode.
**Sample:** [`/custom-profiles`](../samples/Formidable.Sample/Pages/CustomProfiles.razor) —
`Publish date` is the typed date input; [`/workout`](../samples/Formidable.Sample/Pages/Workout.razor)
shows the same mode on the string-modelled pattern instead, on both its date fields.

## `FormidableOptions` is read once

A new `Options` instance takes effect only together with a new `Model` instance, and the form
enforces that rather than leaving it to habit: hand the `Options` parameter a different
`FormidableOptions` reference on a render where `Model` did not also change, and
`FormidableForm<TModel>` throws an `InvalidOperationException` naming the three ways out —
build the options once, mutate the instance you already have, or swap `Model` alongside them.
`FormidableValidator<TModel>` enforces the same rule against its own rebuild trigger, a new
`EditContext` from the enclosing `EditForm`.

Two shapes therefore never work. Passing `Options="new FormidableOptions { … }"` inline hands the
form a fresh instance on every render. Reassigning an options field to change a setting at
runtime hands it a different one on the next render. Neither can reach an engine that already
read its options, so both are errors on the render that introduces them rather than settings that
quietly do nothing.

Build the `FormidableOptions` once, up front, and hold it in a field for the life of the form —
exactly what the sample below does:

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

## App-wide defaults

Most settings on this page are a decision an app makes once, not per form: a design system's
class names, a team's debounce, a profile pair. `AddFormidableBlazor` takes an
`Action<FormidableOptions>` overload for exactly that, and registers the configured instance as
the default every form falls back to:

```csharp
builder.Services.AddFormidableBlazor(options =>
{
    options.CssClasses = new FormidableCssClasses { Invalid = "is-invalid", Valid = "is-valid" };
    options.RefreshDebounce = TimeSpan.FromMilliseconds(500);
});
```

A form with no `Options` parameter uses those. A form that passes one wins outright —
resolution is parameter first, then the configured default, then `new FormidableOptions()`, and
there is no merging between the steps: an `Options` parameter replaces the app-wide instance
whole rather than overriding a property of it. See [Component
kit](component-kit.md#addformidableblazor) for the registration itself.

The configured instance is a singleton the whole app shares. That makes property mutation a
wider lever than it looks: changing `RefreshDebounce` on it at runtime changes every live form
that resolved it, not the one on screen.

## Where each option is demonstrated

- `SuppressedIssueDiagnostic` and `DisclosureOverride` — the progressive disclosure sample
  (`/disclosure`) and [Disclosure](disclosure.md).
- `CssClasses` — [CSS and accessibility](css-and-accessibility.md); remapped onto a UI
  library's own classes (`/bootstrap`) and recoloured live via CSS custom properties
  (`/css-colours`).
- `LiveProfile` / `SubmitProfile` — [Profiles](profiles.md) and the `/profiles`
  sample (the sample relies on the defaults; it doesn't override them).

**Sample:** [`/disclosure`](../samples/Formidable.Sample/Pages/Disclosure.razor)
