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
    public static string Compute(FieldState state, FormidableCssClasses classes) =>
        Assemble(state.HasErrors, state.IsTouched || state.IsModified, state.IsValidating, classes);

    /// <summary>
    /// Assembles the space-joined class string from three already-decided booleans: invalid wins
    /// outright, valid applies only when not invalid, and pending appends to whichever of those
    /// (or neither) applies. <see cref="Compute"/> and
    /// <see cref="FormidableFieldCssClassProvider"/> each decide <paramref name="invalid"/> and
    /// <paramref name="validWithoutError"/> their own way, from different sources — this only
    /// joins the three strings the same way both callers always have.
    /// </summary>
    internal static string Assemble(bool invalid, bool validWithoutError, bool pending, FormidableCssClasses classes)
    {
        var baseClass = invalid ? classes.Invalid : validWithoutError ? classes.Valid : string.Empty;

        if (!pending)
        {
            return baseClass;
        }

        return baseClass.Length == 0 ? classes.Pending : $"{baseClass} {classes.Pending}";
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
public sealed class FormidableCssClasses
{
    /// <summary>Applied when the field has error-severity issues. Defaults to <c>"formidable-invalid"</c>.</summary>
    public string Invalid { get; set; } = "formidable-invalid";

    /// <summary>Applied when the field is touched or modified and has no errors. Defaults to <c>"formidable-valid"</c>.</summary>
    public string Valid { get; set; } = "formidable-valid";

    /// <summary>Appended while a validation pass involving the field is in flight. Defaults to <c>"formidable-pending"</c>.</summary>
    public string Pending { get; set; } = "formidable-pending";
}
```

*Source: `src/Formidable.Blazor/FormidableCssClasses.cs`*

`FormidableInputBase<TValue>`'s `CssClass` property (and `FormidableFieldContext.CssClass` for
the renderless path) calls `FormidableCss.Compute` with whatever `FormidableOptions.CssClasses`
instance the form was built with, then merges the result with any consumer-splatted `class`. See
[Component kit](component-kit.md) for the merge itself. `FormidableFieldMessage`/
`FormidableCollectionMessage` and `FormidableSummary` use a related, fixed convention of their
own for the messages they render — see
[Severity](severity.md) for the `formidable-message--{severity}` and
`formidable-summary__group--{severity}` class families.

That's the entire class-name contract: rename the three strings, and the rule above still
decides when each one applies. What follows is how to rename them, the rule a native `InputBase`
picks up automatically, and the ids/aria/focus wiring built on top of the same field state.

## Configuring `FormidableCssClasses`

`FormidableCssClasses` is a plain settings object — a mutable class with three `string`
properties (`Invalid`, `Valid`, `Pending`, all shown with their defaults above). Construct one,
set whichever properties you want to rename to match your own stylesheet or design system's
naming convention, and assign it to `FormidableOptions.CssClasses`. That property lives on the
options object every `FormidableForm<TModel>`/`FormidableValidator<TModel>` takes (see
[Options](options.md)). Formidable doesn't care what the strings are, only when each one
applies. The rule above is the entire contract. As with every other `FormidableOptions`
property, `CssClasses` is read once, when the engine is built for a given `Model` instance;
handing the form a different `FormidableOptions` instance on a later render throws rather than
quietly changing nothing. See
[Options](options.md#formidableoptions-is-read-once) for that rule.

## The `FieldCssClassProvider` bridge

A field rendered by a plain Blazor `InputBase` (not a Formidable component) still needs a class
that reflects its validation state, and `InputBase` gets its class from the `EditContext`'s
`FieldCssClassProvider`, not from anything Formidable's own components compute. The engine
installs a Formidable-aware provider on the `EditContext` at construction so a native input picks
up the same configured class names automatically:

```csharp
namespace Formidable.Blazor;

/// <summary>
/// Internal fast path for a field-scoped "is this field validating right now" read — the one
/// piece of <see cref="FieldState"/> <see cref="FormidableFieldCssClassProvider"/> needs, without
/// the severity scan the rest of <see cref="IFormValidationEngine.GetFieldState"/> does for
/// errors/warnings the provider already answers from the <c>EditContext</c> instead.
/// <see cref="FormValidationEngine{TModel}"/> implements this explicitly; any other
/// <see cref="IFormValidationEngine"/> (a test double, say) does not, so the provider falls back
/// to <see cref="IFormValidationEngine.GetFieldState"/> for it — the capability stays
/// engine-internal rather than growing the public engine contract for what only this one caller
/// wants.
/// </summary>
internal interface IValidatingFieldReader
{
    /// <summary>Whether a validation pass currently in flight covers <paramref name="field"/>.</summary>
    bool IsFieldValidating(FieldIdentifier field);
}

/// <summary>
/// Applies the configured class names to native InputBase components via the EditContext,
/// including the Pending class while the engine reports the field as validating. Every engine
/// installs one of these on its EditContext as it is built, so a form needs no wiring to get these
/// classes.
/// </summary>
public sealed class FormidableFieldCssClassProvider : FieldCssClassProvider
{
    private readonly FormidableCssClasses _classes;
    private readonly IFormValidationEngine _engine;
    private readonly IValidatingFieldReader? _validatingReader;

    /// <summary>
    /// Creates a provider using the given class names, reading pending state from
    /// <paramref name="engine"/>. Construction is a consumer's business only when their own
    /// <c>EditContext.SetFieldCssClassProvider</c> call has replaced the installed one and they
    /// want Formidable's classes back, or when their own provider wants to delegate to this one:
    /// pass the form's <c>FormidableOptions.CssClasses</c> and its engine, both reachable through
    /// <see cref="FormidableFormContext.Engine"/>.
    /// </summary>
    public FormidableFieldCssClassProvider(FormidableCssClasses classes, IFormValidationEngine engine)
    {
        ArgumentNullException.ThrowIfNull(classes);
        ArgumentNullException.ThrowIfNull(engine);
        _classes = classes;
        _engine = engine;
        _validatingReader = engine as IValidatingFieldReader;
    }

    /// <inheritdoc />
    public override string GetFieldCssClass(EditContext editContext, in FieldIdentifier fieldIdentifier)
    {
        var invalid = editContext.GetValidationMessages(fieldIdentifier).Any();
        var validWithoutError = editContext.IsModified(fieldIdentifier);
        var pending = _validatingReader is not null
            ? _validatingReader.IsFieldValidating(fieldIdentifier)
            : _engine.GetFieldState(fieldIdentifier).IsValidating;

        return FormidableCss.Assemble(invalid, validWithoutError, pending, _classes);
    }
}
```

*Source: `src/Formidable.Blazor/FormidableFieldCssClassProvider.cs`*

```csharp
        editContext.SetFieldCssClassProvider(new FormidableFieldCssClassProvider(options.CssClasses, this));
```

*Source: `src/Formidable.Blazor/FormValidationEngine.cs`*

This is the same three class names as `FormidableCss.Compute`, and the same three-part outcome,
but `Invalid`/`Valid` still come from a narrower pair of sources: the `EditContext`'s
own message store and its `IsModified` flag, not the engine's `FieldState`. Concretely, a field
the engine considers touched (`FieldState.IsTouched`, set by `MarkTouched()`) but that the
`EditContext` has never seen `NotifyFieldChanged` for still gets no class through this path —
`FormidableCss.Compute`'s `IsTouched || IsModified` branch (see above) stays out of the
provider deliberately, the same choice that keeps the kit from wiring touch onto blur app-wide.
`Pending` is different: the provider is constructed with the owning `IFormValidationEngine` and
appends `Pending` whenever the field is currently validating, space-joined after whatever
`Invalid`/`Valid` decision was made — the identical append rule `FormidableCss.Compute` uses
(both funnel through the same internal `FormidableCss.Assemble`), so `Pending` can appear alone on
an untouched, unmodified field just as it can on a Formidable input. Reading that one bit costs
less than the full `GetFieldState` a Formidable input reads for its own `CssClass`: the provider
probes the engine for `IValidatingFieldReader`, an internal fast path
`FormValidationEngine<TModel>` implements, and falls back to `GetFieldState(fieldIdentifier).
IsValidating` only for an `IFormValidationEngine` that doesn't implement it (a test double, say).
A native input inside a Formidable form shows the same "checking…" cue a Formidable input does,
automatically; see the Vanilla interop section of [Component kit](component-kit.md) for the
provider wired into a native `InputText` beside a Formidable one.

Installation is the engine's job, so a form never constructs a provider to get these classes. The
constructor is public for the case where an `EditContext` no longer has Formidable's provider on
it: `SetFieldCssClassProvider` holds exactly one, so a consumer's own call replaces it, and the
way back is `new FormidableFieldCssClassProvider(engine.Options.CssClasses, engine)` with the
engine read from `FormidableFormContext.Engine`. The same construction lets a consumer's provider
delegate to Formidable's and append classes of its own to what it returns. One lifetime note for
`FormidableValidator`, which attaches to an `EditContext` it doesn't own: disposing the validator
leaves Formidable's provider installed on that `EditContext`, still pointing at the disposed
engine, so a page that keeps using the `EditContext` afterwards should install whichever provider
it wants for that next life.

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
    private const string MessagesSuffix = "-messages";

    /// <summary>The id for a field: <c>formidable-{owner-hash}-{sanitized-name}</c>; the model-level field uses <c>form</c> as its name.</summary>
    public static string For(FieldIdentifier field)
    {
        var name = field.FieldName.Length == 0
            ? "form"
            : string.Concat(field.FieldName.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-'));
        return $"formidable-{RuntimeHelpers.GetHashCode(field.Model):x8}-{name}";
    }
```

*Excerpt from `src/Formidable.Blazor/FormidableFieldId.cs`*

The model-level field — an empty `FieldIdentifier.FieldName`, the one the defensive
all-suppressed gate in [Disclosure](disclosure.md) targets — gets `form` as its name
segment rather than an empty one. `FormidableForm` renders it on its own `<form>` element (see
[Component kit](component-kit.md)), so a page only reaches for `FormidableFieldId` by hand for a
field that owns no input of its own — a collection container, or the `<form>` element in attach
mode. An overload of `For` takes a member-access expression instead of a `FieldIdentifier` for
that case — `FormidableFieldId.For(order, o => o.Description)` — so the id can be computed
without a `nameof` step to keep in sync with the property it names. There is no expression shape
for the model-level field itself; construct `new FieldIdentifier(model, string.Empty)` and pass
it to the `FieldIdentifier` overload, as `FormidableForm` does internally.

The message list's id is the same id with `-messages` appended, and `FormidableFieldId.MessagesFor`
is the one place that appends it — the `aria-describedby` contract has an owner rather than a
convention. Call it when wiring a control by hand; the kit's inputs, the message components and
`FormidableFieldContext.AriaDescribedBy` all get their string from it.

### `aria-invalid` and `aria-describedby`

`FormidableInputBase<TValue>.AddCommonAttributes` renders both, following the same field state the
CSS class rule reads:

```csharp
        if (state.HasErrors)
        {
            builder.AddAttribute(sequence + 3, "aria-invalid", "true");
        }

        if (issues.Count > 0)
        {
            builder.AddAttribute(sequence + 3, "aria-describedby", MessagesElementId);
        }
```

*Source: `src/Formidable.Blazor/FormidableInputBase.cs`*

`aria-invalid="true"` only appears for error-severity issues; `aria-describedby` appears for any
issue, warnings and infos included, since those are still rendered and still worth announcing.
The id it points at — `FormidableFieldId.MessagesFor(field)`, the field's id plus `-messages` — is
exactly the id `FormidableFieldMessage`/`FormidableCollectionMessage` render on their message
list:

```csharp
        builder.OpenElement(sequence++, "ul");
        builder.AddAttribute(sequence++, "id", _messagesElementId);
        builder.AddAttribute(sequence++, "class", "formidable-messages");
```

*Source: `src/Formidable.Blazor/FormidableFieldMessage.cs`*

`FormidableFieldContext` — the renderless path's equivalent — computes the identical pair from
the same inputs, so a hand-rolled control driven by `FormidableField` gets the same wiring a
`FormidableInputBase` descendant does:

```csharp
        AriaInvalid = state.HasErrors;
        AriaDescribedBy = issues.Count > 0 ? FormidableFieldId.MessagesFor(elementId) : null;
```

*Source: `src/Formidable.Blazor/FormidableFieldContext.cs`*

### `FormidableSummary` as a live region

`FormidableSummary` renders as a `role="alert"` region, so assistive technology announces it
whenever its content changes — a submit that fails, a live-typed correction that clears an
error, a server-applied issue landing:

```csharp
        builder.OpenElement(sequence++, "div");
        builder.AddAttribute(sequence++, "class", "formidable-summary");
        builder.AddAttribute(sequence++, "role", "alert");
```

*Source: `src/Formidable.Blazor/FormidableSummary.cs`*

It subscribes to the engine's `StateChanged` event itself, so the region's content — and the
alert it triggers — stays current through every kind of update, not just the moment of submit.

### Focus service

Every entry in `FormidableSummary` is a button that calls `IFormidableFocusService.FocusAsync`, which
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
`FormidableSummary`'s click-to-focus handler, when `FocusAsync` reports a miss and no
`FocusFallback` is set (or the fallback itself fails to recover it). The no-op matters for two
cases documented elsewhere. The first is a field scrolled out of a `Virtualize` window with no
current DOM element: the click-to-focus miss that `FormidableSummary`'s `FocusFallback`
parameter exists to recover from (see [Component kit](component-kit.md)). The second is a raw
or foreign control whose markup never actually rendered `field.ElementId` as its `id` attribute,
which is why `FormidableField`'s `ForeignControl.razor` sample sets `id="@field.ElementId"`
explicitly (see [Component kit](component-kit.md)). A `FormidableFieldAnchor`-only registration
with no id on the control it anchors has nothing for the focus service to find. The samples now
close that gap rather than illustrate it, giving their native `InputText`s the field's id
alongside the anchor.

Because the id is the whole of the lookup, a field with no input of its own can be focused just
as well. Give any element the field's `FormidableFieldId.For(...)` id and a `tabindex="-1"` so
it can hold focus, and that element is where the summary entry lands. `FormidableForm` does this
for the model-level field itself, on the `<form>` element it renders — see
[Component kit](component-kit.md) — so the all-suppressed defensive gate's summary entry lands
there with no page wiring. A collection's rules fail against the list rather than against any one
control, so nothing renders that id automatically; the sample gives the container holding every
row the collection's id by hand. A container shows nothing when it takes focus, so the sample
stylesheet marks those landings with an outline, scoped to `:focus-visible`. The scope matters
because `tabindex="-1"` leaves an element focusable by mouse, and a plain `:focus` rule would paint the
whole container whenever a click landed on its padding. Activating a summary entry from the
keyboard carries focus-visible through to the programmatic focus, so the keyboard path keeps the
mark while a mouse click gets `scrollIntoView` alone.

## Where this is demonstrated

- The class rule and its interaction with the `Pending` state — every sample using
  `FormidableInputText` shows it implicitly; [Async validation](async-validation.md)'s
  pending-UI section is the most direct look at `Pending` specifically.
- Renaming two of `FormidableCssClasses`' three class names to fit a UI library's own —
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
- `FormidableSummary`'s live region and click-to-focus, including the virtualize limit —
  [`/virtualized`](../samples/Formidable.Sample/Pages/Virtualized.razor).
- The field id on markup the page renders itself — a native input, a collection's container —
  [`/vanilla`](../samples/Formidable.Sample/Pages/VanillaInterop.razor),
  [`/collections`](../samples/Formidable.Sample/Pages/Collections.razor) and
  [`/workout`](../samples/Formidable.Sample/Pages/Workout.razor) (the venue region input and the
  attendees group). The model-level gate id no longer needs a page's help —
  [`/disclosure`](../samples/Formidable.Sample/Pages/Disclosure.razor) and `/workout` both show
  the all-suppressed gate landing on the `<form>` element `FormidableForm` renders it on.
