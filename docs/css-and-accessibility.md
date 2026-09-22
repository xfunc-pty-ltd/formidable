# CSS and accessibility

**You should already know:** how a Formidable form wires up end to end
([Quickstart](quickstart.md)).

Ship your own CSS and you own every consequence of it: browser default fights, a design system's
naming convention that has nothing to do with "valid" and "invalid," a dark-mode audit nobody
signed up for. A form library that ships CSS of its own trades that ownership away, quietly, the
day its class names collide with yours or its colours don't match your brand. A headless kit
means the classes stay yours: Formidable ships no CSS and no visual opinion (see
[Component kit](component-kit.md)). What it ships instead is a small, consistent surface of
class names and ARIA attributes. Every field exposes it the same way, whether it's Formidable's
own input, a renderless `FormidableField` template, or a plain `InputBase` sitting beside them.
So your own stylesheet and assistive technology both have one thing to key off, regardless of
which shape rendered the field.

## Need to know

One static rule computes a field's state class, shared by every Formidable component that
computes one:

```csharp
namespace Formidable.Blazor;

/// <summary>Shared field CSS class rule: errors win; touched/modified without errors is valid; pending appends while validating.</summary>
public static class FormidableCss
{
    /// <summary>Computes the space-joined class string for a field state.</summary>
    public static string Compute(FieldState state, FormidableCssOptions options)
    {
        var builder = new StringBuilder();

        if (state.HasErrors)
        {
            builder.Append(options.Invalid);
        }
        else if (state.IsTouched || state.IsModified)
        {
            builder.Append(options.Valid);
        }

        if (state.IsValidating)
        {
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append(options.Pending);
        }

        return builder.ToString();
    }
}
```

*Source: `src/Formidable.Blazor/FormidableCss.cs`*

In order: **errors win** — a field with error-severity issues is always `Invalid`, regardless of
touched/modified state. Failing that, **touched or modified is valid** — a field the user has
interacted with (or that the `EditContext` reports as modified) and that currently has no errors
is `Valid`; an untouched, unmodified, error-free field gets neither class. Finally, **pending
appends** — whichever of those two classes was chosen (or neither) gets `Pending` added onto it,
space-joined, while a validation pass involving the field is in flight. `Pending` never replaces
`Invalid`/`Valid`, and it can appear on its own if the field is validating before it's ever been
touched.

The three class names themselves are configurable, each with a default:

```csharp
namespace Formidable.Blazor;

/// <summary>Class names applied to a field based on its current <see cref="FieldState"/>.</summary>
public sealed class FormidableCssOptions
{
    /// <summary>Applied when the field has error-severity issues. Defaults to <c>"formidable-invalid"</c>.</summary>
    public string Invalid { get; set; } = "formidable-invalid";

    /// <summary>Applied when the field is touched or modified and has no errors. Defaults to <c>"formidable-valid"</c>.</summary>
    public string Valid { get; set; } = "formidable-valid";

    /// <summary>Appended while a validation pass involving the field is in flight. Defaults to <c>"formidable-pending"</c>.</summary>
    public string Pending { get; set; } = "formidable-pending";
}
```

*Source: `src/Formidable.Blazor/FormidableCssOptions.cs`*

`ValidatedInputBase<TValue>`'s `CssClass` property (and `FormidableFieldContext.CssClass` for
the renderless path) calls `FormidableCss.Compute` with whatever `FormidableOptions.CssClasses`
instance the form was built with, then merges the result with any consumer-splatted `class`. See
[Component kit](component-kit.md) for the merge itself. `FieldMessage`/`CollectionMessage` and
`FormSummary` use a related, fixed convention of their own for the messages they render — see
[Severity](severity.md) for the `formidable-message--{severity}` and
`formidable-summary__group--{severity}` class families.

That's the entire class-name contract: rename the three strings, and the rule above still
decides when each one applies. What follows is how to rename them, the narrower rule a native
`InputBase` picks up automatically, and the ids/aria/focus wiring built on top of the same field
state.

## Configuring `FormidableCssOptions`

`FormidableCssOptions` is a plain settings object — a mutable class with three `string`
properties (`Invalid`, `Valid`, `Pending`, all shown with their defaults above). Construct one,
set whichever properties you want to rename to match your own stylesheet or design system's
naming convention, and assign it to `FormidableOptions.CssClasses`. That property lives on the
options object every `FormidableForm<TModel>`/`FormidableValidator<TModel>` takes (see
[Options](options.md)). Formidable doesn't care what the strings are, only when each one
applies. The rule above is the entire contract. As with every other `FormidableOptions`
property, `CssClasses` is read once, when the engine is built for a given `Model` instance, and
has no effect if changed on a later render without also swapping the model. See
[Options](options.md#need-to-know) for that rule.

## The `FieldCssClassProvider` bridge

A field rendered by a plain Blazor `InputBase` (not a Formidable component) still needs a class
that reflects its validation state, and `InputBase` gets its class from the `EditContext`'s
`FieldCssClassProvider`, not from anything Formidable's own components compute. The engine
installs a Formidable-aware provider on the `EditContext` at construction so a native input picks
up the same configured class names automatically:

```csharp
namespace Formidable.Blazor;

/// <summary>Applies the configured class names to native InputBase components via the EditContext.</summary>
public sealed class FormidableFieldCssClassProvider : FieldCssClassProvider
{
    private readonly FormidableCssOptions _options;

    /// <summary>Creates a provider using the given class names.</summary>
    public FormidableFieldCssClassProvider(FormidableCssOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <inheritdoc />
    public override string GetFieldCssClass(EditContext editContext, in FieldIdentifier fieldIdentifier)
    {
        if (editContext.GetValidationMessages(fieldIdentifier).Any())
        {
            return _options.Invalid;
        }

        return editContext.IsModified(fieldIdentifier) ? _options.Valid : string.Empty;
    }
}
```

*Source: `src/Formidable.Blazor/FormidableFieldCssClassProvider.cs`*

```csharp
        editContext.SetFieldCssClassProvider(new FormidableFieldCssClassProvider(options.CssClasses));
```

*Source: `src/Formidable.Blazor/FormValidationEngine.cs`*

This is the same three class names as `FormidableCss.Compute`, but a narrower rule: it has no
`Pending` state at all, because `FieldCssClassProvider.GetFieldCssClass` is a synchronous
`EditContext`-level hook with no notion of an in-flight validation pass. All the hook can read
is whatever is currently in the `EditContext`'s message store and its `IsModified` flag. A
native input never shows the pending class a Formidable input can; see the Vanilla interop
section of [Component kit](component-kit.md) for the provider wired into a native `InputText`
beside a Formidable one.

## Accessibility wiring

### Deterministic ids

Every id Formidable assigns — an input's `id`, a message list's `id`, and the target the focus
service looks for — comes from one function, keyed by the owning object instance plus the field
name:

```csharp
namespace Formidable.Blazor;

/// <summary>
/// Deterministic DOM element ids for fields, shared by inputs (element id), messages
/// (aria-describedby target), and the focus service. Identity follows the owning object
/// instance plus the field name.
/// </summary>
public static class FormidableFieldId
{
    /// <summary>The id for a field: <c>formidable-{owner-hash}-{sanitized-name}</c>; the model-level field uses <c>form</c> as its name.</summary>
    public static string For(FieldIdentifier field)
    {
        var name = field.FieldName.Length == 0
            ? "form"
            : string.Concat(field.FieldName.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-'));
        return $"formidable-{RuntimeHelpers.GetHashCode(field.Model):x8}-{name}";
    }
}
```

*Source: `src/Formidable.Blazor/FormidableFieldId.cs`*

The model-level field — an empty `FieldIdentifier.FieldName`, the one the defensive
all-suppressed gate in [Disclosure](disclosure.md) targets — gets `form` as its name
segment rather than an empty one.

### `aria-invalid` and `aria-describedby`

`ValidatedInputBase<TValue>.AriaAttributes` sets both, following the same field state the CSS
class rule reads:

```csharp
    protected IReadOnlyDictionary<string, object> AriaAttributes
    {
        get
        {
            var state = State;
            var issues = Context!.Engine.GetIssues(Field);

            if (!state.HasErrors && issues.Count == 0)
            {
                return NoAriaAttributes;
            }

            var aria = new Dictionary<string, object>();
            if (state.HasErrors)
            {
                aria["aria-invalid"] = "true";
            }

            if (issues.Count > 0)
            {
                aria["aria-describedby"] = $"{ElementId}-messages";
            }

            return aria;
        }
    }
```

*Source: `src/Formidable.Blazor/ValidatedInputBase.cs`*

`aria-invalid="true"` only appears for error-severity issues; `aria-describedby` appears for any
issue, warnings and infos included, since those are still rendered and still worth announcing.
The id it points at — `"{ElementId}-messages"` — is exactly the id `FieldMessage`/`CollectionMessage`
render on their message list:

```csharp
        builder.OpenElement(sequence++, "ul");
        builder.AddAttribute(sequence++, "id", $"{FormidableFieldId.For(_field)}-messages");
        builder.AddAttribute(sequence++, "class", "formidable-messages");
```

*Source: `src/Formidable.Blazor/FieldMessage.cs`*

`FormidableFieldContext` — the renderless path's equivalent — computes the identical pair from
the same inputs, so a hand-rolled control driven by `FormidableField` gets the same wiring a
`ValidatedInputBase` descendant does:

```csharp
        AriaInvalid = state.HasErrors;
        AriaDescribedBy = issues.Count > 0 ? $"{elementId}-messages" : null;
```

*Source: `src/Formidable.Blazor/FormidableFieldContext.cs`*

### `FormSummary` as a live region

`FormSummary` renders as a `role="alert"` region, so assistive technology announces it whenever
its content changes — a submit that fails, a live-typed correction that clears an error, a
server-applied issue landing:

```csharp
        builder.OpenElement(sequence++, "div");
        builder.AddAttribute(sequence++, "class", "formidable-summary");
        builder.AddAttribute(sequence++, "role", "alert");
```

*Source: `src/Formidable.Blazor/FormSummary.cs`*

It subscribes to the engine's `StateChanged` event itself, so the region's content — and the
alert it triggers — stays current through every kind of update, not just the moment of submit.

### Focus service

Every entry in `FormSummary` is a button that calls `IFormidableFocusService.FocusAsync`, which
locates and focuses the DOM element carrying a field's deterministic id:

```csharp
namespace Formidable.Blazor;

/// <summary>
/// Moves keyboard focus (and scrolls into view) to the DOM element rendered for a field.
/// The target element is located by its <see cref="FormidableFieldId"/> id, which the kit's
/// inputs assign automatically — any element carrying that id can be focused this way.
/// </summary>
public interface IFormidableFocusService
{
    /// <summary>
    /// Moves focus to the rendered element for <paramref name="field"/>, scrolling it into view.
    /// Returns <c>true</c> when the element was found and focused, <c>false</c> when no element
    /// with the field's id exists in the DOM — e.g. a virtualized row outside the render window.
    /// </summary>
    /// <param name="field">The field whose rendered element should receive focus.</param>
    ValueTask<bool> FocusAsync(FieldIdentifier field);
}
```

*Source: `src/Formidable.Blazor/IFormidableFocusService.cs`*

The shipped implementation is a thin JS-interop wrapper: it computes the field's
`FormidableFieldId`, passes just that id string across the interop boundary, and returns whatever
the JS side reports:

```csharp
        return await module.InvokeAsync<bool>("focusField", FormidableFieldId.For(field));
```

*Source: `src/Formidable.Blazor/FormidableFocusService.cs`*

```javascript
export function focusField(id) {
    const element = document.getElementById(id);
    if (!element) {
        return false;
    }

    element.scrollIntoView({ behavior: "smooth", block: "center" });
    element.focus({ preventScroll: true });
    return true;
}
```

*Source: `src/Formidable.Blazor/wwwroot/formidable.js`*

`document.getElementById(id)` is the entire lookup, and a miss is reported rather than
swallowed: `focusField` returns `false` when no element carries the id, and `FocusAsync`
propagates that bool straight back to its caller. The silent no-op lives one layer up, in
`FormSummary`'s click-to-focus handler, when `FocusAsync` reports a miss and no `FocusFallback`
is set (or the fallback itself fails to recover it). The no-op matters for two cases documented
elsewhere. The first is a field scrolled out of a `Virtualize` window with no current DOM
element: the click-to-focus miss that `FormSummary`'s `FocusFallback` parameter exists to
recover from (see [Component kit](component-kit.md)). The second is a raw or foreign control
whose markup never actually rendered `field.ElementId` as its `id` attribute, which is why
`FormidableField`'s `ForeignControl.razor` sample sets `id="@field.ElementId"` explicitly (see
[Component kit](component-kit.md)). A `FieldAnchor`-only registration with no id on the control
it anchors has nothing for the focus service to find. The samples now close that gap rather than
illustrate it, giving their native `InputText`s the field's id alongside the anchor.

Because the id is the whole of the lookup, a field with no input of its own can be focused just
as well. Give any element the field's `FormidableFieldId.For(...)` id and a `tabindex="-1"` so
it can hold focus, and that element is where the summary entry lands. The samples do this for
the two field shapes that own no input. A collection's rules fail against the list rather than
against any one control, so the container holding every row takes the collection's id. The
`<form>` element itself carries the id of the model-level field the all-suppressed defensive gate
reports under. A container shows nothing when it takes focus, so the sample stylesheet marks
those landings with an outline, scoped to `:focus-visible`. The scope matters because
`tabindex="-1"` leaves an element focusable by mouse, and a plain `:focus` rule would paint the
whole container whenever a click landed on its padding. Activating a summary entry from the
keyboard carries focus-visible through to the programmatic focus, so the keyboard path keeps the
mark while a mouse click gets `scrollIntoView` alone.

## Where this is demonstrated

- The class rule and its interaction with the `Pending` state — every sample using
  `FormidableInputText` shows it implicitly; [Async validation](async-validation.md)'s
  pending-UI section is the most direct look at `Pending` specifically.
- Renaming two of `FormidableCssOptions`' three class names to fit a UI library's own —
  [`/bootstrap`](../samples/Formidable.Sample/Pages/BootstrapFitting.razor), which points
  `Invalid`/`Valid` at Bootstrap's `is-invalid`/`is-valid` and lets Bootstrap's own stylesheet do
  the rest; `Pending` is left at its default there.
- A consumer stylesheet keying off those same class names with CSS custom properties instead of
  fixed colours — [`/css-colours`](../samples/Formidable.Sample/Pages/CssColours.razor).
- The `FieldCssClassProvider` bridge and a native `InputBase` picking up the same classes —
  [`/vanilla`](../samples/Formidable.Sample/Pages/VanillaInterop.razor), covered in
  [Component kit](component-kit.md).
- `aria-invalid`/`aria-describedby` on a hand-rolled control —
  [`/foreign`](../samples/Formidable.Sample/Pages/ForeignControl.razor).
- `FormSummary`'s live region and click-to-focus, including the virtualize limit —
  [`/virtualized`](../samples/Formidable.Sample/Pages/Virtualized.razor).
- The field id on markup the page renders itself — a native input, a collection's container, the
  form element behind the gate — [`/vanilla`](../samples/Formidable.Sample/Pages/VanillaInterop.razor),
  [`/collections`](../samples/Formidable.Sample/Pages/Collections.razor),
  [`/disclosure`](../samples/Formidable.Sample/Pages/Disclosure.razor) and
  [`/workout`](../samples/Formidable.Sample/Pages/Workout.razor).
