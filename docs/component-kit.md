# The component kit

**You should already know:** the four components that make a working form
([Quickstart](quickstart.md)), and the first look at `FormidableField` and
`FormidableCollectionMessage` around a list of rows
([A list of members](tutorial/4-collections.md)).

This page catalogs every component and seam in the headless kit: what each declares, one example,
and the behaviour a signature does not give away.

## Need to know

| Component | Kind | One line | Sample |
|---|---|---|---|
| [`FormidableForm<TModel>`](#formidableformtmodel) | root | Owns the `EditContext` and renders the `<form>`; submit, reset, focus and loaded-value disclosure live here. | [`/scroll-focus`](../samples/Formidable.Sample/Pages/ScrollFocus.razor) (a toggle flips `FocusFirstErrorOnInvalidSubmit`); [`/dialog-submit`](../samples/Formidable.Sample/Pages/DialogSubmit.razor) (a toggle skips the suppressing call); [`/profiles`](../samples/Formidable.Sample/Pages/Profiles.razor) (a Reset button); [`/draft-load`](../samples/Formidable.Sample/Pages/DraftLoad.razor) (three saved values, three answers) |
| [`FormidableInputText` and `FormidableInputBase<TValue>`](#formidableinputtext-and-formidableinputbasetvalue) | input | The kit's text box, and the base every input here shares. | [`/`](../samples/Formidable.Sample/Pages/Quickstart.razor) |
| [`FormidableInputSelect<TValue>`](#formidableinputselecttvalue) | input | A `<select>` over a typed field, converting the string the DOM reports. | [`/custom-profiles`](../samples/Formidable.Sample/Pages/CustomProfiles.razor) (`Category`, under `UpdateOn="OnBlur"`) |
| [`FormidableInputTextArea`](#formidableinputtextarea) | input | The multiline sibling of `FormidableInputText`, differing only in the element tag. | [`/normalize`](../samples/Formidable.Sample/Pages/Normalize.razor) (`Body`) |
| [`FormidableInputNumber<TValue>`](#formidableinputnumbertvalue) | input | An `<input type="number">` converted through the invariant culture. | [`/custom-profiles`](../samples/Formidable.Sample/Pages/CustomProfiles.razor) (`Read minutes`, required and range-checked) |
| [`FormidableInputDate<TValue>`](#formidableinputdatetvalue) | input | An `<input type="date">` formatted and parsed through the invariant culture. | [`/custom-profiles`](../samples/Formidable.Sample/Pages/CustomProfiles.razor) (`Publish date`, under `UpdateOn="OnBlur"`) |
| [`FormidableFieldMessage<TValue>`](#formidablefieldmessagetvalue) | message | One field's current issues, any severity, as a persistent list. | [`/`](../samples/Formidable.Sample/Pages/Quickstart.razor) |
| [`FormidableModelMessage`](#formidablemodelmessage) | message | A message list for verdicts about the form rather than about any field. | [`/disclosure`](../samples/Formidable.Sample/Pages/Disclosure.razor) (*Without a summary*: submit with details collapsed) |
| [`FormidableRequiredIndicator<TValue>`](#formidablerequiredindicatortvalue) | indicator | Marks a field the submit profile demands a value for. | [`/workout`](../samples/Formidable.Sample/Pages/Workout.razor) (kit, foreign, native and row placements) |
| [`FormidableSummary`](#formidablesummary) | summary | The visible issues, grouped by severity, each entry focusing its field. | [`/summary-shape`](../samples/Formidable.Sample/Pages/SummaryShape.razor) (toggles `ItemTemplate`, `GroupByField`, `MaxItems` and `OverflowTemplate`); [`/severity`](../samples/Formidable.Sample/Pages/SeverityLevels.razor) (two disjoint summaries, both with headings) |
| [`FormidableValidator<TModel>`](#formidablevalidatortmodel-attaching-to-an-existing-form) | root | The root that attaches to an `EditForm` the page already owns. | [`/attach`](../samples/Formidable.Sample/Pages/AttachMode.razor) (a plain `InputText` beside a Formidable-managed list) |
| [`FormidableCollectionMessage<TValue>`](#formidablecollectionmessagetvalue) | message | `FormidableFieldMessage`'s sibling for a collection-level rule, registering its own path. | [`/collections`](../samples/Formidable.Sample/Pages/Collections.razor) |
| [`FormidableField<TValue>` and `FormidableFieldContext`](#formidablefieldtvalue-and-formidablefieldcontext) | seam | Renderless: hands your own markup the field's state, ids and issues. | [`/foreign`](../samples/Formidable.Sample/Pages/ForeignControl.razor) |
| [`FormidableFieldAnchor<TValue>`](#formidablefieldanchortvalue) | registration | Registration and nothing else, for a control the kit does not render. | [`/vanilla`](../samples/Formidable.Sample/Pages/VanillaInterop.razor) |
| [The foreign-control pattern](#the-foreign-control-pattern) | note | `FormidableField` around a plain `<select>`, worked end to end. | [`/foreign`](../samples/Formidable.Sample/Pages/ForeignControl.razor) |
| [`FocusFallback`](#focusfallback) | delegate | The parameter that recovers a focus move that missed. | [`/virtualized`](../samples/Formidable.Sample/Pages/Virtualized.razor) (one callback on the form and the summary) |
| [`PrepareFocus`](#preparefocus) | delegate | The parameter awaited before a move is attempted. | [`/dialog-submit`](../samples/Formidable.Sample/Pages/DialogSubmit.razor) (a toggle makes the dismissal report early) |
| [`AddFormidableBlazor()`](#addformidableblazor) | registration | The one-call registration, and where app-wide options are set. | — |
| [Culture at WebAssembly boot](#culture-at-webassembly-boot) | note | Where a WebAssembly app applies a stored language choice. | [`/localization`](../samples/Formidable.Sample/Pages/Localization.razor) (stores the choice and reloads) |
| [Virtualize and `KeepRegistered`](#virtualize-and-keepregistered) | note | Keeping a scrolled-away row disclosed and reachable. | [`/virtualized`](../samples/Formidable.Sample/Pages/Virtualized.razor) |
| [Vanilla interop](#vanilla-interop) | note | Native Blazor inputs and `ValidationMessage` inside a Formidable form. | [`/vanilla`](../samples/Formidable.Sample/Pages/VanillaInterop.razor); [`/bootstrap`](../samples/Formidable.Sample/Pages/BootstrapFitting.razor) (the CSS merge onto a UI library's classes) |

`FormidableForm` and `FormidableValidator` resolve the same five things as they build their engine,
and where a parameter exists for one, what you passed wins:

| What | Where it comes from | Parameter |
|---|---|---|
| The validator | An explicit `Validator`, else `IModelValidator<TModel>` from the container. | `Validator` |
| The introspector | `IModelIntrospector` from the container, or it throws. | none |
| The options | An explicit `Options`, then the app-wide default registered through [`AddFormidableBlazor(...)`](#addformidableblazor), then `new FormidableOptions()`. | `Options` |
| The logger | `ILoggerFactory` where one is registered, `null` otherwise. It carries the [suppressed-issue report](options.md#suppressedissuediagnostic). | none |
| The clock | `TimeProvider` from the container, else `TimeProvider.System`. | none |

Every timer the engine keeps rides the resolved clock, so a test drives both debounce windows with a
`FakeTimeProvider` ([Testing](testing.md#faking-the-clock)).

`Validator` takes the whole validator, capabilities included: the shipped FluentValidation adapter
implements `IRuleInspectingValidator<TModel>` and `IRuleLevelValidator<TModel>` beside the
validation seam, and a wrapper written against `IModelValidator<TModel>` alone presents neither.
Derive one from `DelegatingModelValidator<TModel>`, which forwards all three:
[wrap the validator](recipes.md#i-want-to-wrap-the-validator-without-losing-what-it-can-do).

Resolution fails loudly, and the validator's two failures are how a first form fails to start. Each
message below carries the form's own model type where `Order` is.

- `No IModelValidator<Order> is registered in the container this render is resolving from` —
  Formidable itself is missing from that container. Call `services.AddFormidableBlazor()` there and
  register the FluentValidation validator with it.
  [Hosting models](hosting-models.md#the-server-builds-the-form-too) has the page shape a
  two-project app most often meets it on.
- `No FluentValidation validator for 'Order' is registered` — the commoner of the two. Formidable is
  registered, so the open-generic adapter exists, but the FluentValidation validator it wraps does
  not. Register one with `services.AddScoped<IValidator<Order>, OrderValidator>()`, or a whole
  assembly's at once with `services.AddValidatorsFromAssembly()`. The container's own exception is
  kept as the `InnerException`.

## `FormidableForm<TModel>`

The primary root. It owns the `EditContext` (nothing under it creates or replaces one) and renders
a real `EditForm` underneath, so native `InputBase` descendants, `ValidationMessage` and a
`DataAnnotationsValidator` beside it keep working.

| Parameter | Type | Default | One line |
|---|---|---|---|
| `Model` | `TModel` | required | The object being edited. Omitting it throws a message naming the component and the parameter; binding a different instance rebuilds the `EditContext` and the engine. |
| `ModelChanged` | `EventCallback<TModel>` | unbound | Invoked when `ResetAsync` swaps the model; `@bind-Model` is what binds it. |
| `Validator` | `IModelValidator<TModel>?` | `null` (from the container) | The validator this form validates through, whole — see [Need to know](#need-to-know). |
| `Options` | `FormidableOptions?` | `null` (app-wide default, then `new FormidableOptions()`) | The [engine options](options.md) this form is built with. A different instance arriving without a new `Model` throws, since the engine [reads `Options` once](options.md#formidableoptions-is-read-once). |
| `ChildContent` | `RenderFragment<FormidableFormContext>?` | `null` | The form's content, handed the cascaded `FormidableFormContext` as `context`: the engine's members, plus `FocusFirstErrorAsync()`, which lives there because `PrepareFocus` and `FocusFallback` are the form's. `ResetAsync` stays out of reach, since it rebuilds the engine the context belongs to. Nesting another typed fragment that also leaves its parameter name implicit makes the Razor compiler ask for a `Context="..."` on one of the two, since what collides is the declaration rather than any use of it. |
| `OnValidSubmit` | `EventCallback<SubmitOutcome>` | unbound | Runs when the submit pipeline passes, with the [`SubmitOutcome`](severity.md#does-a-warning-or-an-info-block-the-submit) it produced. A parameterless handler binds too. |
| `OnInvalidSubmit` | `EventCallback<FormidableInvalidSubmitContext>` | unbound | Runs when it blocks, with a context carrying that outcome and the [suppression call](#suppressing-the-automatic-focus). A parameterless handler binds too. |
| `FocusFirstErrorOnInvalidSubmit` | `bool` | `true` | Whether the form moves focus itself on a blocked submit and on a rejected round trip. |
| `FocusFallback` | `Func<FieldIdentifier, ValueTask<bool>>?` | `null` | Recovers a move that missed: make the element reachable, return `true`, and it retries once. |
| `PrepareFocus` | `Func<FieldIdentifier, ValueTask>?` | `null` | Awaited before each move this form makes, so the page can clear the way first. |
| `AdditionalAttributes` | `IReadOnlyDictionary<string, object>?` | `null` | Splatted onto the rendered `<form>`, under the four positions below. |

```razor
<FormidableForm Model="_contact" OnValidSubmit="HandleValid">
    <div class="summary-slot">
        <FormidableSummary />
    </div>

    <div class="field"><label>Name <FormidableInputText @bind-Value="_contact.Name" /></label>
        <FormidableFieldMessage For="() => _contact.Name" /></div>
    <div class="field"><label>Email <FormidableInputText @bind-Value="_contact.Email" /></label>
        <FormidableFieldMessage For="() => _contact.Email" /></div>

    <div class="actions"><button type="submit">Submit</button></div>
</FormidableForm>
```

<!-- Excerpt from `samples/Formidable.Sample/Pages/Quickstart.razor` -->

The members a page calls:

| Member | What it does |
|---|---|
| `SubmitAsync()` | Runs the submit pipeline and routes to `OnValidSubmit` or `OnInvalidSubmit`. Also the handler wired to the rendered `EditForm`, so a toolbar button outside the form element calls the same thing the button inside it does. |
| `FocusFirstErrorAsync()` | [Makes the first-error move on demand](#asking-for-the-first-error-move), and answers whether an element took focus. |
| `ResetAsync(TModel?)` | [Returns the form to pristine](#returning-the-form-to-pristine), over the bound model or a new one. |
| `DiscloseLoadedValuesAsync(CancellationToken)` | [Says what the loaded values have earned](#saying-what-loaded-values-have-earned). |
| `ApplyServerIssues(...)` | Applies a server verdict — a sequence of issues, or a deserialized `FormidableValidationProblem`. Focuses the page's first error where the payload carries one, under `FocusFirstErrorOnInvalidSubmit` — [Server integration](server-integration.md#why-did-focus-move-when-i-applied-the-reply) has the round trip. |
| `Engine` | The engine as the non-generic `IFormidableEngine`, where `IsValidating`, `HasSubmitted` and `IsFormValid` are read — the last meaning nothing until [`TrackFormValidity`](options.md#trackformvalidity) is on. |

Call the methods from the renderer's synchronization context, and after the form's first render.
Before that there is no engine and they throw.

**The `<form>` takes four positions of its own against the splat**, across the five attributes it
can render:

| Attribute | Against the splat | What it is |
|---|---|---|
| `id` and `tabindex="-1"` | win outright | The model-level field's deterministic `FormidableFieldId`, which the all-suppressed gate's summary entry and the focus service both address, so its value cannot be a consumer's to choose. |
| `novalidate` | loses outright | Renders by default. Splat `novalidate="@false"` — the `bool`, since the string `"false"` would still render the attribute — to hand the submit back to the browser's own constraint UI ([CSS and accessibility](css-and-accessibility.md#why-aria-required-and-not-required) has what a form without it does). |
| `aria-describedby` | merges | Splatted ids first, the model-level message list's id appended, so a `FormidableModelMessage` describes the form with nothing wired and a page's own hint stays where the page put it. |
| `inert` | wins while it renders | Renders only [while a prerender window is open](hosting-models.md#what-happens-inside-the-prerender-window), and wins there the way `id` does. The form renders none at any other time, so a page that makes its own form inert keeps that everywhere else. |

`FormidableValidator` renders no `<form>`, so in attach mode all five attributes are
[the page's own to write](#formidablevalidatortmodel-attaching-to-an-existing-form).

| When | What you see |
|---|---|
| You read engine state inline through `context` | It refreshes when the form re-renders, not on every check; a live spinner needs its own `Engine.StateChanged` subscription. |
| A clean or advisory-only reply is applied through `ApplyServerIssues(...)` | Nothing moves, whatever an earlier submit left on screen. |
| A reply is applied through `context.Engine.ApplyServerIssues(...)` | Nothing moves, error or not: the engine's own method is the quiet background apply. |
| Static render, no interactivity coming | A `<p class="formidable-render-mode-message">` asking for a render mode, in the form's place; no attribute above renders ([Hosting models](hosting-models.md#does-the-page-need-a-render-mode) has why). |
| Anything watching that page's status code | Sees 200: the page renders like any other. |
| The host | Is told once, through the `Trace` line plus `ILoggerFactory` warning an unwired `FocusFallback` miss also uses. |

**A blocked submit moves focus to the first error**, not the first entry:
[issue order follows the page](#the-order-entries-appear-in), so a field above the failing one may
carry nothing worse than a warning. [`FocusFallback`](#focusfallback) recovers a miss and
[`PrepareFocus`](#preparefocus) prevents one; with no `IFormidableFocusService` registered nothing
moves and nothing throws.

[CSS and accessibility](css-and-accessibility.md#where-does-focus-go-on-a-blocked-submit) has the move, its fallback to the
first visible issue where a submit blocks with no error on screen, and
[what a miss looks like](css-and-accessibility.md#why-did-nothing-take-focus).
[Async validation](async-validation.md#what-happens-if-i-press-submit-while-a-check-is-running) has
the overtaken submit that leaves a form in that state, and the validator throw that propagates
instead of blocking.

### Asking for the first-error move

`FocusFirstErrorAsync()` makes the move on demand. It is the same move the submit path makes, so the
field it lands on, the `PrepareFocus` awaited ahead of the attempt and the `FocusFallback` that
recovers a miss all come with it. `FocusFirstErrorOnInvalidSubmit` does not gate it: that parameter
governs only the moves the form makes unasked. A dialog's own way out is the usual caller:

```csharp
    // Wired to your dialog's own Close button and its Escape handler: the ways out that name no
    // field, where nothing else is going to move focus to one.
    private async Task CloseAnnouncementAsync()
    {
        await _announcement!.CloseAsync();
        await _form!.FocusFirstErrorAsync();
    }
```

| When | What you see |
|---|---|
| An element took focus | It answers `true`. |
| Nothing did: no visible issue, no `IFormidableFocusService` registered, or an element the miss and its fallback could not reach | It answers `false`. |
| You need to tell a quiet form from an unreachable error | Read `Engine.GetVisibleIssues()`: the answer reports the move, not the form. |
| A component nested inside the form calls `context.FocusFirstErrorAsync()` | The root's own move, with no `@ref` to reach for, and everything above applies. |
| The `FormidableFormContext` was built through its public constructor (the shape [Testing](testing.md) points at) | No root behind it: it moves nothing and answers `false`. |

### Suppressing the automatic focus

A dialog announcing a blocked submit is the case `FocusFirstErrorOnInvalidSubmit` alone handles
badly. The form's own move runs immediately after `OnInvalidSubmit` returns, inside the same submit
call, so the caret lands in a box the overlay is covering. The handler declares the suppression
instead, at the moment it opens the dialog:

```csharp
    private async Task AnnounceAsync(FormidableInvalidSubmitContext context)
    {
        context.SuppressFirstErrorFocus();
        await _announcement!.OpenAsync();
    }
```

| When | What you see |
|---|---|
| `FocusFirstErrorOnInvalidSubmit="false"` | No automatic move on any submit, including those where no dialog opens. |
| The handler calls `SuppressFirstErrorFocus()` | No automatic move for this submit alone: a fresh context is built for every blocked submit, so nothing latches. |
| A handler delegates to a helper | `FirstErrorFocusSuppressed` reads what has been said so far. |

It suppresses; it does not request. Asking is
[`FocusFirstErrorAsync()`](#asking-for-the-first-error-move), and the sequence ends there: suppress,
open the dialog, close it, focus.

### Returning the form to pristine

`ResetAsync` is the clean slate a `Model` swap produces, without needing a different object to get
it:

```csharp
await _form!.ResetAsync();                // same instance, back to pristine
await _form!.ResetAsync(new Order());     // a different instance — needs @bind-Model
```

| When | What you see |
|---|---|
| Called with nothing | The engine and the `EditContext` are rebuilt over the model instance already bound; the model's values are not written. |
| What the old engine held | Gone: touched and modified state, the message store, the advisory buckets, `HasSubmitted`, and any whole-form re-check still waiting. |
| A `SubmitAsync` still awaiting its answer | Abandoned with that engine: neither callback fires, focus stays, nothing re-renders, and it returns a blocked outcome carrying nothing. |
| Called with a model but no `ModelChanged` bound | Throws, naming `@bind-Model` as the fix. |

Called with a model, it swaps to that instance. The swap is durable only if the parent's own field
moves too, since Blazor re-supplies `Model` from whatever the parent still holds on every one of the
*parent's* renders; `ResetAsync` invokes `ModelChanged` for that, and `@bind-Model` binds it:

```razor
<FormidableForm @bind-Model="_order" @ref="_form" OnValidSubmit="HandleValid">
```

### Saying what loaded values have earned

A form filled from somewhere other than this visitor's typing (a saved draft, a record opened for
editing) looks pristine however good or bad its contents are, since writing model properties
notifies nothing. `DiscloseLoadedValuesAsync` answers for what is already there:

```csharp
_proposal.Title = "Progressive disclosure in practice";
_proposal.ContactEmail = "ada.lovelace";
_proposal.Summary = string.Empty;

await _form!.DiscloseLoadedValuesAsync();
```

<!-- Excerpt from `samples/Formidable.Sample/Pages/DraftLoad.razor.cs` -->

It validates the whole model under `SubmitProfile`, then decides field by field on whether the field
**holds a value**:

| The field | What it is marked | What you see |
|---|---|---|
| Holds a value no rule fails with an error | Touched and engaged | The valid class, or whichever advisory tier its warnings and infos earn |
| Holds a value the rules do fail | Touched and engaged | Its message, inline and in the summary |
| Holds nothing: null, a blank or whitespace-only string, an empty collection, or a non-nullable value type's own default | Neither | Nothing, unstyled and silent, with any required mark it carries still standing |

"Holds a value" is FluentValidation's own `NotEmpty()` negated, taken against the type the member is
**declared** as. [Disclosure](disclosure.md#why-isnt-my-message-showing-yet) has why a loaded form
looks pristine, and why the load engages the fields it decides for rather than merely marking them
touched.

| When | What you see |
|---|---|
| A saved `false` in a `bool?` | An answer. An untouched `bool` is not: non-nullable value types err to silence, so model optional values as `T?`. |
| The constructor seeds a placeholder your rules reject | Rejected the moment the values load: a seeded value is a value. |
| The constructor seeds the type's default | Silent: a default is not a value. |
| The validator cannot report its rules | Wrong values still disclose, needing only the model; nothing is confirmed, which needs the validator's own field list. |
| The field sits inside a collection row | Confirmed exactly as a top-level one: the confirming list is the validator's declared shape. |
| A value cannot be read (a null owner on its path; a model-level failure naming no member) | Nothing is claimed: the field stays unstyled. |
| It runs | One whole-model check, async rules included, then the check that discloses what it found. It moves no focus. |
| A page spinner reads `IsValidating` | True for the whole check; "checking" shows on no field ([Async validation](async-validation.md#where-does-checking-show-and-where-doesnt-it)). |
| A form never calls it | Unaffected in every respect. |

## `FormidableInputText` and `FormidableInputBase<TValue>`

`FormidableInputText` is the kit's text box; `FormidableInputBase<TValue>` is the base under it and
under every other input here. The base is public because deriving from it is
[the supported way](#deriving-your-own-input) to get a control the kit does not ship.

Every input here takes these parameters; the input sections below list only what each one adds.

| Parameter | Type | Default | One line |
|---|---|---|---|
| `For` | `Expression<Func<TValue>>?` | `null` | The explicit field accessor, `() => _order.Description`. Supply it when the input has no `@bind-Value`, or to point registration, messages and focus at a field other than the one the value binds. It wins over `ValueExpression` whenever both are present, silently and by design. |
| `Value` | `TValue?` | `default` | The field's current value. |
| `ValueChanged` | `EventCallback<TValue?>` | unbound | Raised when the input commits a new value. |
| `ValueExpression` | `Expression<Func<TValue>>?` | `null` | The accessor behind `Value`, filled in by the Razor compiler for every `@bind-Value` — the same `Value`/`ValueChanged`/`ValueExpression` triple native inputs take, so ordinary markup names the field once. Not one you write by hand. With neither this nor `For`, the input throws as its parameters are set, naming itself and both spellings. |
| `KeepRegistered` | `bool` | `false` | Keeps the field registered after the component is disposed, for a virtualized container — see [Virtualize and `KeepRegistered`](#virtualize-and-keepregistered). |
| `UpdateOn` | `InputUpdateMode` | `InputUpdateMode.OnChange` | Which DOM event commits the value, and whether the engine hears about it then or at the next blur ([the three modes](options.md#updateon-per-input-not-a-formidableoptions-property)). |
| `AdditionalAttributes` | `IReadOnlyDictionary<string, object>?` | `null` | Splatted onto the rendered element, ahead of every value the component computes. |

```razor
<div class="field"><label>Name <FormidableInputText @bind-Value="_contact.Name" /></label>
    <FormidableFieldMessage For="() => _contact.Name" /></div>
```

<!-- Excerpt from `samples/Formidable.Sample/Pages/Quickstart.razor` -->

Label-wrapping is what every sample using `FormidableInputText` does: a wrapping label needs no
`for`. Markup that cannot wrap addresses the input by its id, as
[the foreign-control pattern](#the-foreign-control-pattern) does.

`AdditionalAttributes` enters the render tree ahead of every value the component computes, and
Blazor applies last-write-wins. That settles every row below but `@onblur`, which chains instead:

| Splatted | What the input does with it |
|---|---|
| `class` | Merges: the splatted value first, the computed state class after. A consumer writing `class="form-control"` keeps it and still gets whichever state class applies, plus `formidable-pending` while a check is running for the field. |
| `aria-describedby` | Merges: the splatted ids first, the messages id appended, so a persistent hint keeps its association as issues come and go. |
| `id` | Dropped. The rendered id is always the deterministic one, because the message list, `aria-describedby` and `IFormidableFocusService` all address the field by it. |
| `@onblur` | Chains rather than being claimed: the consumer's handler runs first and is awaited, then the kit's own blur work follows. |

| When | What you see |
|---|---|
| You splat `type="tel"`, `type="password"` or `type="range"` onto `FormidableInputText` | A plain `string` binding, no parsing and so no culture; the browser alone reads the type. |
| The control's own parser rejects a string (select, number, date) | Nothing commits or arms: a blur under `OnBlur` delivers only what an earlier commit armed. |
| The cascaded context is a new instance (first render; a host rebuilding its engine) | The field and its two ids resolve, and the input registers. |

Which aria attributes render, and when, is
[CSS and accessibility](css-and-accessibility.md#which-aria-attributes-does-an-input-get-and-when)'s subject.
Registration is what lets the submit channel show a field's issues
([Disclosure](disclosure.md#why-did-it-appear-when-i-pressed-submit)); it happens when the cascaded
context is a new instance (first render; a host rebuilding its engine).
[`FormidableFieldAnchor`](#formidablefieldanchortvalue) registers a field and nothing else.
`FormidableComponentBase`, under `FormidableInputBase<TValue>` and
`FormidableFieldAnchor` alike, is public only because a public component cannot inherit a less
accessible base, and is not an extension point.

### The click a disclosure displaces

Under `OnChange`, pressing Submit blurs the field the visitor was in, and that blur is the commit.
The message it discloses is inserted above the button, the button leaves the pointer, and the
browser dispatches the click on the nearest common ancestor of the press and the release (for a
submit button, usually the `<form>`, where nothing is listening).

[CSS and accessibility](css-and-accessibility.md#what-stops-a-click-on-submit-vanishing-when-a-message-moves-the-button) covers
the general case of a button that moves out from under a still pointer. Both roots close it with a
guard, installed by default, that re-delivers the click to the button it began on;
[`ClickRecovery`](options.md#clickrecovery) turns it off.

The press has to have begun on a `<button>`, an `<input type="submit">` or an `<input type="image">`
inside the root, and then all three of these hold together:

| Condition | Why recovery waits for it |
|---|---|
| The browser retargeted the click to an ancestor that contains the pressed button | A click that reached the button, or anything inside it, needs nothing done for it. |
| The pointer stayed where it was pressed, within a few pixels of drift | A press dragged off its button still cancels: that is the discrimination the platform cannot make for itself. |
| The button's border box changed | Measured against the viewport, so a scroll under a still pointer counts. A button merely covered by something that opened over it is left alone. |

What the re-delivered click is:

| Property | Consequence |
|---|---|
| One click, not two | The mis-delivered click is suppressed as the recovery goes out, so a delegated handler, an analytics listener or a click-outside-to-close guard sees one click per press. |
| Untrusted | `isTrusted` reads `false` on the button and on every element along its path, so code gating on that flag (yours or a widget's) can decline it. Script cannot dispatch a trusted event; that is recovery's price. |
| Still activated | Transient user activation survives, since the delivery happens inside the browser's own handling of the displaced click, so an activation-gated API still works. |

`FormidableValidator` renders no element of its own, so it looks in two places for one to scope to:
the element carrying the model-level field id, taken when that element is a `<form>` or has a
recoverable button inside it, and failing that the `<form>` a field this component registered sits
in.

A page offering neither gets no guard, and is told so through the same `Trace` line plus
`ILoggerFactory` warning an unwired `FocusFallback` miss uses. The message names three ways out: put
the model-level `FormidableFieldId` on the `EditForm` this attaches to, place a `<form>` element
around a field this component registers, or set [`ClickRecovery`](options.md#clickrecovery) to
`None` and ask for no guard at all.

Two things a page can do instead, both of which remove the shift rather than recovering from it:

| Instead of recovering | What it takes |
|---|---|
| `UpdateOn="InputUpdateMode.OnInput"` on the fields above the button | The message is on screen before the button is pressed, so nothing moves. It costs a check per keystroke, the trade [`/normalize`](../samples/Formidable.Sample/Pages/Normalize.razor) and [`/css-colours`](../samples/Formidable.Sample/Pages/CssColours.razor) make. |
| Reserve the space the messages will take | Real height, not a persistent wrapper alone: an empty element is zero-height. `FormidableFieldMessage` always renders its list element, so a stylesheet has something to give a `min-height`. The sample's `.summary-slot` shows the weaker version's limit: the wrapper and regions persist, and the button below still moves when the bands arrive. |

### Deriving your own input

The kit wraps a control when the wrapper meaningfully improves its validation UX. A native `<input>`
whose `type` only changes what the browser renders needs no wrapper: splat the type onto
`FormidableInputText` (the table above).

A control binding an actual `decimal` or `DateOnly` through the base's generic overload gets real
parsing instead, under the current thread's culture; that is the gap `FormidableInputNumber` and
`FormidableInputDate` close. A control the kit has no wrapper for (a checkbox binding through `checked`, a third-party component) stays native, paired
with [`FormidableFieldAnchor`](#formidablefieldanchortvalue) or driven by
[`FormidableField`](#formidablefieldtvalue-and-formidablefieldcontext).

Derive when a control in your own form reads better wrapped. A validated range slider, in full:

```csharp
public sealed class RatingInput : FormidableInputBase<int>
{
    /// <inheritdoc />
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "input");
        AddCommonAttributes(builder, 1);
        builder.AddAttribute(5, "type", "range");
        builder.AddAttribute(6, "value", Value);
        AddValueBinding(builder, 7);
        builder.CloseElement();
    }
}
```

<!-- Source: `tests/Formidable.Blazor.Tests/FormidableInputBaseDerivationTests.cs` -->

Consume it like any kit input (`<RatingInput @bind-Value="_feedback.Rating" min="0" max="5" />`)
where `min` and `max` splat through `AdditionalAttributes` untouched.

| What the base gives you | What deriving asks of you |
|---|---|
| `AddCommonAttributes(builder, sequence)` renders the splat, then the `id`, the `class` and the aria attributes, in the order that merges a consumer's `class` and `aria-describedby` and drops their `id` | Call it first, immediately after opening the element. It consumes `sequence` through `sequence + 3`, so the control's own attributes start at `sequence + 4`. |
| `AddValueBinding(builder, sequence, …)` renders the commit attribute `UpdateOn` calls for, and `blur` where the mode or the control needs it. Three overloads, one implementation underneath, so a mode the control knows nothing about still gets a correct binding | Call it last, immediately before closing the element. What the control renders between the two calls lands after the splat, so it wins the duplicate-attribute race too. Rendering an attribute *before* `AddCommonAttributes` is the deliberate opposite: it offers a default a consumer's splat can override, which is what `step="any"` does on the number input below. |
| `Field`, `ElementId` and `MessagesElementId` are the resolved identifier and the two deterministic ids, computed once at registration rather than per render | Render `ElementId` as written. `Register` is sealed: which field the control speaks for is not adjustable, because an input that resolved a different field would render no usable id and register nothing for disclosure. |
| `State` and `CssClass` are the field's current state and the merged class string | Nothing. `Context` is the route to everything the rest does not cover: `Context.Engine.GetIssues(Field)` to render messages yourself, `Context.EditContext`, `Context.Registry`. |
| `SetCurrentValueAsync`, `CommitValueAsync` and `NotifyChanged` are commit-and-notify in one call, or the two halves apart | For a control driving a commit from a handler of its own rather than through `AddValueBinding`. |
| `SyncsDomValueOnBlur` and `SyncDomValueAsync` bind `blur` in every `UpdateOn` mode and run your write there — after any consumer-splatted `@onblur`, and before the notification `OnBlur` delivers | Override both and inject `IFormidableDomValueSync`. The write is the override's whole job. |
| `OnParametersSet` is where the field resolves, registers, and binds the engine subscription | Call `base` from any override, or the control registers nothing and never re-renders as checks start and finish. |
| `Dispose` is non-virtual and always releases the registration and the subscription | Override `DisposeCore` for resources of your own. A control implementing `IAsyncDisposable` has to call `Dispose()` from its `DisposeAsync`, since Blazor calls only the async overload when a component implements both. |
| `ObservesEngineState` and `OnEngineStateChanged` are why a control re-renders as checks start and finish | Leave them alone unless you mean it: `false` freezes the state class, `aria-invalid`, `aria-describedby` and `aria-required` at whatever the last render gave them. Override the relay and call `base` so the re-render still happens. |

One more seam serves a control whose native element can display text it reports as empty, a number
box holding `e3` being the kit's own case. No render-tree diff can overwrite that, because the
rendered value and the reported value already agree. Two overrides and an injected service opt in:

```csharp
[Inject]
private IFormidableDomValueSync DomValueSync { get; set; } = default!;

protected override bool SyncsDomValueOnBlur => true;

protected override ValueTask SyncDomValueAsync() =>
    DomValueSync.SyncValueAsync(ElementId, FormatCurrentValue());
```

That is the shape `FormidableInputNumber` itself ships, with `FormatCurrentValue()` standing in for
however the control formats its `Value` back into DOM text.

## `FormidableInputSelect<TValue>`

A `<select>` needs everything a text box needs and one thing more: the option a visitor picks is
always a string, and the field it drives usually is not.

| Parameter | Type | Default | One line |
|---|---|---|---|
| `ChildContent` | `RenderFragment?` | `null` | The `<option>` elements, rendered inside the `<select>`. |

```razor
<div class="field">
    <label for="@CategoryId">Category</label>
    <FormidableInputSelect @bind-Value="_post.Category" UpdateOn="InputUpdateMode.OnBlur">
        <option value="">Choose…</option>
        <option>Announcement</option>
        <option>Tutorial</option>
        <option>Release notes</option>
    </FormidableInputSelect>
    <FormidableFieldMessage For="() => _post.Category" />
</div>
```

<!-- Excerpt from `samples/Formidable.Sample/Pages/CustomProfiles.razor` -->

| When | What you see |
|---|---|
| `TValue` has no conversion from a string | An `InvalidOperationException` naming the component and the type, the first time a change commits, as native does. |
| A committed option string fails to parse | Nothing commits and no message of the control's own appears: every message a `<select>` shows is a rule's. |
| A `bool?` field holds `null` | No selection (the blank `<option>`), where native formats it as `"false"`, so a blank option clears the field to unanswered. |
| `UpdateOn="OnInput"` | Exactly `OnChange`: a `<select>` has no `input` event distinct from `change`. |
| `UpdateOn="OnBlur"` | The value commits on `change`; the message waits for the blur. |
| A `<label>` wraps the `<select>` | Its text takes in every option's, defeating an exact-match label lookup in test tooling; not an accessibility defect. |

`TValue` can be `string`, `bool`, an enum, or anything else `BindConverter` converts from a string:
native `InputSelect<TValue>`'s set, no broader. Multi-select, an array-typed `TValue`, is out of
scope. An option's `value` is the string the component formats for that `TValue` (`ToString()`,
with `bool` as `"true"` or `"false"`), so the round trip lands back on the right value.

The `for=` above sidesteps the label wrap: `CategoryId` is `FormidableFieldId.For(_post, p =>
p.Category)`, the expression overload of the id the component computes
([CSS and accessibility](css-and-accessibility.md#how-do-i-compute-an-id-by-hand)), so no `nameof` has
to stay in sync. Everything else (`For`, `UpdateOn`, `KeepRegistered`, the splat) is
[the base's](#formidableinputtext-and-formidableinputbasetvalue).

## `FormidableInputTextArea`

The multiline sibling of `FormidableInputText`: same base, same parameters, same `UpdateOn` choice.
The only difference is the element tag, mirroring native `InputTextArea` exactly.

```razor
<div class="field"><label>Body <FormidableInputTextArea @bind-Value="_note.Body" UpdateOn="InputUpdateMode.OnInput" /></label>
    <FormidableFieldMessage For="() => _note.Body" /></div>
```

<!-- Excerpt from `samples/Formidable.Sample/Pages/Normalize.razor` -->

| When | What you see |
|---|---|
| A `<label>` wraps it | The usual wrap, as for `FormidableInputText`: a `<textarea>`'s content is its value, not child elements; its name reads as an `<input>`'s. |

Everything else is [the base's](#formidableinputtext-and-formidableinputbasetvalue).

## `FormidableInputNumber<TValue>`

An `<input type="number">` reports its value period-decimal (`"12.5"`, never `"12,5"`) whatever
the browser's locale, while the base's typed binding reads the current culture. Under a
comma-decimal culture that pairing reads `12.5` as `125` rather than failing loudly, so
`FormidableInputNumber<TValue>` converts through `CultureInfo.InvariantCulture` instead. It adds
no parameters of its own to [the shared set](#formidableinputtext-and-formidableinputbasetvalue).

```razor
<div class="field"><label>Read minutes <FormidableInputNumber @bind-Value="_post.ReadMinutes" /></label>
    <FormidableFieldMessage For="() => _post.ReadMinutes" /></div>
```

<!-- Excerpt from `samples/Formidable.Sample/Pages/CustomProfiles.razor` -->

| When | What you see |
|---|---|
| `TValue` is outside the supported set | A `TypeInitializationException` (wrapping the `InvalidOperationException` naming the type) the first time the closed generic is touched. |
| `UpdateOn="OnInput"` | A check per keystroke: a number box fires a real `input` event, where a `<select>` does not. |
| Text the parser rejects, an emptied non-nullable box included | Nothing commits: the model keeps its value, and under `OnBlur` nothing is armed for the blur. |
| `TValue` is `int?`, `decimal?` or another nullable form | An emptied box commits `null`, for a `NotNull()` rule to judge. |
| The box loses focus | The model's value is written back through `IFormidableDomValueSync`, in every `UpdateOn` mode: a box showing `e3` while reporting empty reverts. |
| You splat your own `step` | Off-grid values are a `stepMismatch` again: under `FormidableForm`'s `novalidate` nothing blocks, but the field matches `:invalid` and the spinner snaps. |

`TValue` is checked once, in a static constructor, against native `InputNumber<TValue>`'s set
(`int`, `long`, `short`, `float`, `double`, `decimal` and their nullable forms), so an unsupported
one never reaches an instance. Two attributes take fixed positions against the splat:

| Attribute | Position | Why |
|---|---|---|
| `step="any"` | Before the splat, so a consumer's own `step` wins | HTML's own default `step` is `1`, which makes any fractional value a native `stepMismatch`. The default retires that at the source, matching native `InputNumber<TValue>` for every type it supports. |
| `type="number"` | After the splat, so the component wins | The same position `RatingInput`'s `type="range"` takes above. |

[CSS and accessibility](css-and-accessibility.md#why-aria-required-and-not-required) has what a form
without `novalidate` does with a constraint the browser enforces, and why `novalidate` leaves
`:invalid` standing.

## `FormidableInputDate<TValue>`

The same problem one step further: a native `<input type="date">`'s DOM value is a specific
calendar-and-format pair, ISO `yyyy-MM-dd`, whatever the locale. Under a non-Gregorian-calendar
culture such as Thai, the base's current-culture conversion can read `"2024-01-15"` back as a
different year rather than failing, so `FormidableInputDate<TValue>` formats and parses through that
format string under `CultureInfo.InvariantCulture`. It adds no parameters of its own to
[the shared set](#formidableinputtext-and-formidableinputbasetvalue).

```razor
<div class="field"><label>Publish date <FormidableInputDate @bind-Value="_post.PublishDate" UpdateOn="InputUpdateMode.OnBlur" /></label>
    <FormidableFieldMessage For="() => _post.PublishDate" /></div>
```

<!-- Excerpt from `samples/Formidable.Sample/Pages/CustomProfiles.razor` -->

| When | What you see |
|---|---|
| `TValue` is outside the supported set | A `TypeInitializationException` (wrapping the `InvalidOperationException` naming the type) the first time the closed generic is touched. |
| The default `OnChange`, typing a date in Chromium | A check per typed segment (day, month, year), briefly showing a verdict against an unfinished year. |
| `UpdateOn="OnBlur"` | The model still commits on every segment's `change`; the check waits for the blur. Tabbing through with nothing committed starts none. |
| Anything not a well-formed `yyyy-MM-dd`, an emptied non-nullable box included | Nothing commits: the model keeps its value, and under `OnBlur` nothing arms for the blur. |
| `TValue` is `DateOnly?`, `DateTime?` or `DateTimeOffset?` | An emptied box commits `null`, for a `NotNull()` rule to judge. |
| The box loses focus | The model's value is written back through `IFormidableDomValueSync`, in every `UpdateOn` mode, so half-entered segments cannot linger. |

`TValue` is checked the way `FormidableInputNumber` checks its own: a static constructor against
`DateTime`, `DateTimeOffset`, `DateOnly` and their nullable forms. Rendering follows the same shape,
with `type="date"` in the component-wins position.

**Prefer `UpdateOn="InputUpdateMode.OnBlur"` for this component specifically**, for the reason the
Chromium row above gives.
[Options](options.md#updateon-per-input-not-a-formidableoptions-property) states all three modes,
for this component and every other input.

## `FormidableFieldMessage<TValue>`

`FormidableFieldMessage` renders one field's current issues, any severity, as an accessible list.

| Parameter | Type | Default | One line |
|---|---|---|---|
| `For` | `Expression<Func<TValue>>` | required | Accessor naming the field these messages speak for, e.g. `() => Model.Email`. |
| `AdditionalAttributes` | `IReadOnlyDictionary<string, object>?` | `null` | Splatted onto the rendered `<ul>`, under the three positions below. |

```razor
<div class="field"><label>Ticket reference <FormidableInputText @bind-Value="_ticket.Reference" /></label>
    <FormidableFieldMessage For="() => _ticket.Reference" /></div>
```

<!-- Excerpt from `samples/Formidable.Sample/Pages/SummaryShape.razor` -->

| When | What you see |
|---|---|
| Nothing else registers the field | Live messages still arrive; a submit's are suppressed. Pair it with an input, `FormidableField` or `FormidableFieldAnchor` ([Disclosure](disclosure.md#why-isnt-my-message-showing-yet)). |
| The field has no issues | An empty `<ul>`, still rendered: your CSS can transition it, and a configured [`InlineMessageLive`](options.md#inlinemessagelive) sits on an element that persists. |

**The list takes three positions of its own against the splat:**

| Attribute | Against the splat | What it is |
|---|---|---|
| `class` | merges | The splatted value first, `formidable-message-list` after. |
| `id` | wins outright | `FormidableFieldId.MessagesFor`'s value, which every input describing itself by this list points at, so it cannot be a consumer's to choose. |
| `aria-live` | wins outright while [`InlineMessageLive`](options.md#inlinemessagelive) is set | The list's live-region behaviour is that option's to decide, form-wide. With the option unset the kit computes none, so a splatted one stands. |

That option configures `aria-live` rather than a `role`, and the difference is not cosmetic. A
`role` on the list element replaces the list role, which drops every `<li>` inside it out of the
accessibility tree as presentational: the announcement arrives, and the messages stop being a list
the visitor can move through.

`status` and `alert` also carry an implicit `aria-atomic` of true, so correcting one field reads
back every message still standing. `aria-live` costs neither: the list stays a list, atomicity stays
false, and only what changed is announced.

Its two bases, `FormidableMessageBase<TValue>` and `FormidableAccessorComponentBase<TValue>`, are
public but not extension points: neither constructor is accessible outside the assembly, so this
component and [`FormidableCollectionMessage`](#formidablecollectionmessagetvalue) are the only two
shapes the first takes.

## `FormidableModelMessage`

Some verdicts are about the form rather than about any field: the defensive gate's explanation for
a blocked submit that can show nothing
([Disclosure](disclosure.md#why-is-the-submit-blocked-with-no-message-in-sight)), a server error
applied with an empty path, and the
[`ValidationFaultMessage`](options.md#validationfaultmessage) a throwing validator leaves behind,
which `GetIssues` orders last.

They live on the model-level field, an empty `FieldIdentifier.FieldName`, which no
`FormidableFieldMessage` can name: `For` takes an accessor expression, and no expression reaches
that field.

| Parameter | Type | Default | One line |
|---|---|---|---|
| `AdditionalAttributes` | `IReadOnlyDictionary<string, object>?` | `null` | Splatted onto the rendered `<ul>`, under [the message list's three positions](#formidablefieldmessagetvalue). |

Nothing else is declared, because the field it speaks for is fixed:

```razor
<FormidableForm Model="_inlineRequest" Options="_inlineOptions"
                OnValidSubmit="HandleInlineValid">
    <FormidableModelMessage />
```

<!-- Excerpt from `samples/Formidable.Sample/Pages/Disclosure.razor` -->

| When | What you see |
|---|---|
| It stands anywhere under the root | Nothing to pair it with: it registers nothing, and the model-level field is always disclosed. |
| A [`FormidableSummary`](#formidablesummary) is on the form too | The same model-level issues twice, since the summary lists them. Render this on a form with no summary. |
| The root is `FormidableValidator` | The same list; the `aria-describedby` pointing at it is [the page's to write](#formidablevalidatortmodel-attaching-to-an-existing-form) on its `EditForm`. |

It renders the same persistent list as the field messages, under the model-level message id: the
form element's own id plus `-messages`, per `FormidableFieldId.MessagesFor`. A configured
[`InlineMessageLive`](options.md#inlinemessagelive) sits on that persistent element.

## `FormidableRequiredIndicator<TValue>`

Marks a field the submit profile demands a value for.

| Parameter | Type | Default | One line |
|---|---|---|---|
| `For` | `Expression<Func<TValue>>` | required | Accessor naming the field to mark, e.g. `() => Model.Title`. |

It declares nothing else. Place it where the mark belongs:

```razor
<div class="field"><label>Title <FormidableRequiredIndicator For="() => _proposal.Title" /> <FormidableInputText @bind-Value="_proposal.Title" /></label>
    <FormidableFieldMessage For="() => _proposal.Title" /></div>
```

<!-- Excerpt from `samples/Formidable.Sample/Pages/DraftLoad.razor` -->

| When | What you see |
|---|---|
| A check runs, or a value changes | Nothing: it does not observe engine state. |
| `RequiredOverride`'s answer or `SubmitProfile` changes | The mark follows on the page's next render, not before. |
| It stands beside a control | It registers nothing (a marker is not an input); the input beside it, or a `FormidableFieldAnchor`, keeps the field registered. |
| A test queries raw text | It sees the marker. A role-and-name query, which runs the accessible-name algorithm, does not: `aria-hidden` separates them. |

The mark is derived from the validator's rules rather than declared on the markup, so a presence
rule moving between profiles moves the mark with it. The submit profile is the one that decides: a
narrowed `LiveProfile` changes when a message appears, never whether the value is demanded.

It is `aria-hidden`, deliberately, and it is not the accessible half of this feature: that a value
is demanded belongs on the input as `aria-required="true"`, which the kit's inputs and
`FormidableFieldContext.InputAttributes` put there
([CSS and accessibility](css-and-accessibility.md#why-is-the-required-mark-aria-hidden) has why the
two are separate elements).

**Placement is markup position.** The component needs the cascaded context and its `For`, and reads
nothing from what surrounds it:

| Where the mark goes | The case for it | Sample |
|---|---|---|
| Inside the `<label>`, after the label text | The habit most samples follow, and the shape above. `aria-hidden` is what lets it sit there without changing the accessible name computed from the label. | [`/draft-load`](../samples/Formidable.Sample/Pages/DraftLoad.razor) |
| Inside the `<label>`, after the control | A checkbox, where the control comes first, so the mark follows the `<input type="checkbox">` instead of the words. | No page |
| In a `<legend>`, outside every `<label>` | A radio group, where each label names an option rather than the field. A mark there lands in the legend's text rather than a label's. | [`/disclosure`](../samples/Formidable.Sample/Pages/Disclosure.razor) |
| Inside the `<label>`, beside a control Formidable does not render | Row one's placement again. What changes is the accessible half: the page writes `aria-required` onto the native control itself, the marker staying this component's. | [`/workout`](../samples/Formidable.Sample/Pages/Workout.razor) |

The checkbox row, worked:

```razor
<FormidableField For="() => _signup.AcceptsTerms" Context="field">
    <label><input type="checkbox" @attributes="field.InputAttributes"
                  checked="@_signup.AcceptsTerms"
                  @onchange="args => ToggleTerms(args, field)" />
        <FormidableRequiredIndicator For="() => _signup.AcceptsTerms" />
        I accept the terms</label>
</FormidableField>
```

Requiredness is a three-valued answer, and only one of the three draws anything:

| `FieldRequirement` | What it means | What the component draws |
|---|---|---|
| `Required` | The submit profile selects a presence rule for the field, and it carries no condition | `<span class="formidable-required" aria-hidden="true">` around [`RequiredIndicatorContent`](options.md#requiredindicatorcontent) — `"*"` unless you say otherwise |
| `ConditionallyRequired` | Every presence rule the profile selects for the field is conditional | Nothing: a condition cannot be evaluated without a model instance, so the mark would assert a demand the library cannot verify. A page that wants to say something there reads `FormidableFieldContext.Requirement` from a `FormidableField` and renders its own markup |
| `NotRequired` | No presence rule was found for the field | Nothing |

What inspection reads, and where it stops — every limit costing a mark rather than inventing one:

| The rule | The answer |
|---|---|
| `NotEmpty()` or `NotNull()` | Read as presence. On a `bool`, whose empty value is `false`, that is how "must be ticked" becomes readable as a demand. |
| A rule declared with the index left open, `Attendees[].Name` | Expanded against the rows the model holds, one answer per row under the row's own identifier, so a field inside a collection row is marked exactly as a top-level one: each attendee's Name earns its own marker and its own `aria-required`. |
| `Must(s => !string.IsNullOrWhiteSpace(s))`, or `Equal(true)` on that same `bool` | `NotRequired`: a predicate is indistinguishable from any other predicate, so the mark waits on [`RequiredOverride`](options.md#requiredoverride). |
| Any rule on a validator that cannot be inspected | `NotRequired`, for every field it declares. |
| `RuleForEach(...).Where(...)` or `.WhereAsync(...)` | `ConditionallyRequired` for every row. Which rows the filter admits cannot be answered without a model, so the answer is a property of the rules rather than of any one row's values. |
| A rule inside a child validator scoped by the `SetValidator` call itself, as in `.SetValidator(new AddressValidator(), "Admin")` | Read under a selection built from those ruleset names, which replaces the one that selected the holding rule — because that is what FluentValidation executes. A rule tagged into `"Admin"` in there demands its field whenever the holding rule is selected; an untagged `NotEmpty()` in there is one FluentValidation runs under no profile, so it demands nothing anywhere, and the absent mark is the honest report. |
| A rule the root does not declare for itself | Read by whichever of three routes carries it: one inside a child validator answers under the child's own path (`Address.City`), one merged in with `Include` at the including validator's level, and one inside a child a model-level rule carries at the root's own level. |

Where the rules cannot be read, the marker is what an option says instead:

| Option | What it does to the mark |
|---|---|
| [`RequiredOverride`](options.md#requiredoverride) | Declares the answer in both directions: `Required` marks, `NotRequired` unmarks (the value means "not known to be required", never "proven optional"); the marker and `aria-required` read the same answer. |
| [`RequiredIndicatorContent`](options.md#requiredindicatorcontent) | Supplies the text inside the span; `""` keeps the element, empty, for a CSS-drawn glyph. |
| [`ShowRequiredIndicators`](options.md#showrequiredindicators) | `false` renders no marker anywhere on the form and leaves `aria-required` where it was. |

## `FormidableSummary`

Renders a live, severity-grouped list of the currently visible issues across the form: every one of
them until you say otherwise.

| Parameter | Type | Default | One line |
|---|---|---|---|
| `Show` | `SummaryFilter` | `SummaryFilter.All` | [Which severities this summary renders](#showing-one-severity-band), and with them which regions its wrapper holds. |
| `ErrorsHeading` | `string?` | `null` | [A heading for the error band](#heading-each-band). Unset renders neither the element nor its `aria-labelledby`. |
| `WarningsHeading` | `string?` | `null` | The same for the warning band. |
| `InfosHeading` | `string?` | `null` | The same for the info band. |
| `HeadingLevel` | `int` | `2` | The `h1`–`h6` level whichever bands carry a heading render at. Anything outside 1–6 throws from `OnParametersSet`. |
| `ItemTemplate` | `RenderFragment<VisibleIssue>?` | `null` | [Replaces what each entry's button contains](#deciding-what-an-entry-says), and nothing else about the entry. The button element and its [`formidable-summary__link`](css-and-accessibility.md#the-structural-class-inventory) class stay the component's, so a reworded entry still reads as the button its list item promises. |
| `GroupByField` | `bool` | `false` | [One entry per field per band](#one-entry-per-field) instead of one per issue. |
| `MaxItems` | `int?` | `null` | [The most entries a band renders](#capping-the-list). `null` is all of them; a negative value throws from `OnParametersSet`. |
| `OverflowTemplate` | `RenderFragment<IReadOnlyList<VisibleIssue>>?` | `null` | What a capped band renders in place of [the entries it held back](#capping-the-list), which it receives — never an empty list, and once for each band that held anything back. |
| `FocusFallback` | `Func<FieldIdentifier, ValueTask<bool>>?` | `null` | Recovers a click whose focus move missed: make the element reachable, return `true`, and it retries once. |
| `PrepareFocus` | `Func<FieldIdentifier, ValueTask>?` | `null` | Awaited once per click, before the first focus attempt, so the page can clear the way first; a `FocusFallback` retry does not run it again. |
| `AdditionalAttributes` | `IReadOnlyDictionary<string, object>?` | `null` | Splatted onto the persistent wrapper and reaching nothing below it, since the regions and every element inside them are contract. A splatted `class` merges, the splatted value first and `formidable-summary` after. |

```razor
<div class="summary-slot">
    <FormidableSummary GroupByField="_groupByField"
                       MaxItems="@(_capped ? 3 : (int?)null)"
                       ItemTemplate="@(_nameTheField ? FieldName : null)"
                       OverflowTemplate="@(_reveal ? HeldBack : null)" />
</div>
```

<!-- Excerpt from `samples/Formidable.Sample/Pages/SummaryShape.razor` -->

| When | What you see |
|---|---|
| You click an entry | Focus moves to its field through `IFormidableFocusService`; a row outside a virtualized window, or inside a collapsed section, is a miss. |
| You change `Show` while matching issues are on screen | The added region arrives with its first band in one render; the other region's element stays. |

The markup is built to be announced. One persistent `<div class="formidable-summary">` wrapper holds
fixed-role live regions that render from the first paint and stand empty while the form has nothing
to show. Errors band into `formidable-summary__region--errors`, which has carried `role="alert"`
since it rendered; warnings and infos band into `formidable-summary__region--advisories` and its
politer `role="status"`.
[CSS and accessibility](css-and-accessibility.md#why-does-the-summary-render-empty-regions-before-anything-is-wrong) has why a live
region announces reliably only when its role was there before the content.

[`FocusFallback`](#focusfallback) is the escape hatch for a miss, and
[`PrepareFocus`](#preparefocus) beside it is for a field the caret can reach but the visitor cannot
see.

### The order entries appear in

Entries follow the page: within each severity group, issues list in the document order of the fields
that render them, so the first error a visitor reads about is the topmost one rather than whichever
rule the validator declared first.

`FormidableForm` supplies that order. After a render that changed the registered field set, or one a
browser-side observer reports moved the form's elements around, it asks
`IFormidableFieldOrderService` where those fields sit and hands the answer to its engine, which
sorts `GetVisibleIssues()` by it. The seam is JS-backed, only the browser knowing where an element
is, and public so a test can fake it ([Testing](testing.md#the-form-under-bunit)).

The seam's currency is the field rather than the rendered element id, an id being unreadable back
into the field it came from:

```csharp
ValueTask<IReadOnlyList<FieldIdentifier>?> OrderAsync(IReadOnlyList<FieldIdentifier> fields);
```

<!-- Source: `src/Formidable.Blazor/IFormidableFieldOrderService.cs` -->

`FormidableFieldId.For(field)` maps the other way, so an implementation can answer from DOM position
or from anything else it knows about a field. Five properties of the resulting order are deliberate:

| Property | What it means |
|---|---|
| Two issues on one field keep validator order | The sort is by field, so it never reorders what one field reported. |
| A verdict about the whole form is reported first | The model-level field rides along on every resolution, and its element is the `<form>`, which contains every field on the page, so document order puts the all-suppressed gate's explanation and a validator fault first. An implementation need not special-case it: `FormidableFieldId.For` derives its id the same way. |
| A field the page cannot place sorts last | The service answers only for the fields it can locate, so anything it leaves out sorts after everything it placed: a control that renders no id of its own, and a row held only by [`KeepRegistered`](#virtualize-and-keepregistered), which stays registered precisely because it has left the DOM. |
| An empty answer and no answer are different answers | An empty list says none of these fields are on the page, which is taken as the order. `null` says the order could not be resolved at all, and the form asks again on a later render, as it does after an interop call that threw. Answer `null` where empty was meant and the form re-resolves on every render; answer empty where `null` was meant and it settles on validator order until the registered field set next changes. |
| Before the first resolution, the order is the engine's own | A resolve lands after the render that produced the elements, so until one has, `GetVisibleIssues()` reports channel by channel: the fault issue, then submit errors, then advisories, then the live channel. The same is true of a host that never resolves an order (`FormidableValidator` in attach mode, or an app that registered no order service), which keeps the summary working and costs it only the reading order ([Migration guide](migration-guide.md#attach-mode-lists-issues-in-the-engines-order-not-the-pages)). |

Document order is the default, being the order a visitor reads the form in. A form wanting another
sets [`FormidableOptions.OrderIssues`](options.md#orderissues), a re-sort over what the seam
resolved; an order the layout decides needs the seam itself
([Recipes](recipes.md#i-want-the-summary-ordered-by-where-fields-appear-on-screen)). Re-sorting is
the whole of what that delegate can do:

| The delegate | What happens |
|---|---|
| Leaves a field out of its result | The field is appended, in document order, rather than dropped: withholding an issue is disclosure's job, with its own diagnostic, never a re-sort's. |
| Names a field twice | Only its first position is kept. |
| Names a field it was never handed | That name is ignored, so a short answer padded out to the right length cannot push a real field out of the map. |
| Runs | Once per resolution, as above: not per render, and not per `GetVisibleIssues()` call. A criterion that moves on its own (a runtime "blocking ones first" toggle) is not picked up until the next resolution. |
| Throws | Nothing catches it, and it runs inside a render, so the throw takes the form down. It is synchronous by design: async ordering has its home in the seam above. |

### Showing one severity band

`Show` defaults to `SummaryFilter.All`, the single combined list above, and the other four members
narrow it. `Advisories` means every non-error issue (warnings, infos, and any severity outside
those two) exactly as `ValidationReport.Advisories` does. A page that wants the blocking problems
apart from the commentary renders two:

```razor
<FormidableSummary Show="SummaryFilter.Errors" />

<fieldset>
    ...
</fieldset>

<p>Worth a look before you send this:</p>
<FormidableSummary Show="SummaryFilter.Advisories" />
```

| `Show` | The regions its wrapper holds |
|---|---|
| `All` | Both. |
| `Errors` | The `role="alert"` region alone. |
| `Advisories`, `Warnings`, `Infos` | The `role="status"` region alone. |

| When | What you see |
|---|---|
| A filter matches nothing | Its region renders empty, as on a clean form, persisting so later arrivals announce from an element already holding its role. |
| A paragraph above a quiet advisory summary | The page hides it keyed on the bands (`:has(.formidable-summary__band)`), present only while the summary has something to say. |
| A default summary beside a filtered one | Each reads the same issues and filters alone, so those show twice, as two announcements ([CSS and accessibility](css-and-accessibility.md#what-changes-when-show-splits-the-summary)). |

### Heading each band

`ErrorsHeading`, `WarningsHeading` and `InfosHeading` give a severity band its own heading. Set one
and the summary renders it as an `h{HeadingLevel}` inside that band, with an id this component mints
and wires to the band's `<ul>` via `aria-labelledby`. No English default stands in for one you leave
unset: the label is your own wording in your own language
([`/severity`](../samples/Formidable.Sample/Pages/SeverityLevels.razor) gives its errors summary
`ErrorsHeading` and its advisories summary the other two).

The band itself is a `<div class="formidable-summary__band formidable-summary__band--{severity}">`
wrapping the heading, if any, and the `<ul>`. It renders whether or not that band carries a heading.

The band sits inside its severity's fixed-role region, which matters when styling with child
combinators: the chain is `.formidable-summary` > `__region` > `__band` > `ul`, so a
`.formidable-summary > ul` selector matches nothing and descendant selectors are the durable choice.

### Deciding what an entry says

`ItemTemplate` receives the entry's `VisibleIssue`, the field and the issue together. The common
reason to reach for it is the field's name rather than the rule's complaint: `Issue.DisplayName`
([Profiles](profiles.md#where-do-display-names-and-localized-messages-come-from) has where that name comes from).

```razor
<FormidableSummary>
    <ItemTemplate Context="entry">@entry.Issue.DisplayName</ItemTemplate>
</FormidableSummary>
```

| When | What you see |
|---|---|
| The issue carries no `DisplayName` | Nothing, under a name-only template: the gate's explanation, a validator fault and a [response-body error](server-integration.md#the-wire-contract) carry none. |
| The issue is a server advisory | A `DisplayName` only where the response supplied one. |
| You read `SubmitOutcome.VisibleErrorSummary` instead | It falls back to the issue's `Path`, then to [`ModelLevelDisplayName`](options.md#modelleveldisplayname). A template does neither. |

Rewording an entry changes what it reads as and nothing about where its click takes the visitor.
Different markup *around* the entries is a summary of your own.
[Recipes](recipes.md#i-want-my-own-summary-markup) walks that.

### One entry per field

A summary lists one entry per issue, so a field failing two rules is listed twice
([`/summary-shape`](../samples/Formidable.Sample/Pages/SummaryShape.razor)'s Ticket reference,
required and shaped like `TKT-0000`, once the empty form is submitted). That is right for a list of
messages and wrong for a list of names. `GroupByField` keeps each field's first issue and drops the
rest:

```razor
<FormidableSummary Show="SummaryFilter.Errors" GroupByField="true">
    <ItemTemplate Context="entry">@entry.Issue.DisplayName</ItemTemplate>
</FormidableSummary>
```

Four things about it are decided rather than incidental:

| Decided | Why |
|---|---|
| It groups by field identity, not by display name | Two genuinely different fields are free to carry the same `WithName(...)`, and merging those would drop one of them from a list whose whole job is to be complete. Field identity is what the click has to resolve anyway. |
| It groups within a band | A field carrying an error and a warning is listed once under each, which is one field described two ways rather than one description repeated. |
| Entries keep the position of each field's first issue | So a grouped band is still in [the order the page is read in](#the-order-entries-appear-in). |
| Every model-level issue shares one field identifier | So grouping collapses those too: a band holding both a validator fault and the all-suppressed gate's explanation renders one entry for the pair. |

| When | What you see |
|---|---|
| `SubmitOutcome.VisibleErrorSummary` listed beside the grouped errors band | Distinct by *name* where the band is by *field*: two fields sharing one `WithName(...)` leave it one short. |
| One field's two error rules carry different `WithName` values | `VisibleErrorSummary` runs one long against the band, since `WithName` applies to the rule that declared it. |

### Capping the list

`MaxItems` counts entries, which is issues by default and fields under `GroupByField`. So
`GroupByField="true"` with `MaxItems="4"` is "the first four fields with a problem", where
`MaxItems="4"` alone is "the first four problems".

```razor
<FormidableSummary Show="SummaryFilter.Errors" GroupByField="true" MaxItems="4">
    <ItemTemplate Context="entry">@entry.Issue.DisplayName</ItemTemplate>
    <OverflowTemplate Context="held">+ @held.Count more to fix</OverflowTemplate>
</FormidableSummary>
```

| The value | What a band does with it |
|---|---|
| Any value | Caps that band alone, never the summary. A band is a list of its own with a heading of its own, and counting across the summary would let a run of warnings decide how many errors a visitor gets to read. |
| `null`, the default | Renders every entry it holds. |
| At or above the band's entry count | Nothing changes. |
| `0` | Renders that band's list with no entries in it, which is how a summary hands `OverflowTemplate` the band whole and lists none of it itself. |
| Negative | Throws from `OnParametersSet`, as an out-of-range `HeadingLevel` does: a number reached by arithmetic that has gone below zero is a mistake worth seeing, not a list that quietly empties. |

| When | What you see |
|---|---|
| `OverflowTemplate` set, entries held back | It receives the entries (never a count), each the `VisibleIssue` `ItemTemplate` gets, inside the component's `<li class="formidable-summary__overflow">`. |
| `OverflowTemplate` is unset | A capped band renders nothing in place of what it dropped: no element, no sentence. |

A line that only counts the entries reads `Count`, as above; having them is what lets an expander
name what did not fit
([`/summary-shape`](../samples/Formidable.Sample/Pages/SummaryShape.razor)'s overflow line is one,
and its last toggle takes the fragment away so the band ends in silence).

## `FormidableValidator<TModel>`, attaching to an existing form

The root for a page whose `EditForm` is already its own. It attaches the engine to the cascaded
`EditContext` rather than creating one, so the `<form>` element and its submit handler stay the
page's. [Migration guide](migration-guide.md) has when to reach for it.

| Parameter | Type | Default | One line |
|---|---|---|---|
| `Options` | `FormidableOptions?` | `null` (app-wide default, then `new FormidableOptions()`) | The [engine options](options.md) this component builds its engine with. A different instance arriving without a new `EditContext` throws, since the engine [reads `Options` once](options.md#formidableoptions-is-read-once). |
| `Validator` | `IModelValidator<TModel>?` | `null` (from the container) | The validator this form validates through, whole ([Need to know](#need-to-know)). |
| `ChildContent` | `RenderFragment<FormidableFormContext>?` | `null` | The content the cascade reaches, handed the `FormidableFormContext` as `context` (or as whatever a `Context="..."` renames it to, which the Razor compiler asks for on this element or on the `EditForm` around it). With none, the component renders nothing at all, cascade included, rather than an empty wrapper. |
| `FocusFirstErrorOnInvalidSubmit` | `bool` | `true` | Whether a submit blocked through `ValidateForSubmitAsync()` moves focus itself. It never gates `FocusFirstErrorAsync()`, which is the page deciding. |
| `FocusFallback` | `Func<FieldIdentifier, ValueTask<bool>>?` | `null` | [Recovers a move that missed](#focusfallback): make the element reachable, return `true`, and it retries once. |
| `PrepareFocus` | `Func<FieldIdentifier, ValueTask>?` | `null` | [Awaited before each move this component makes](#preparefocus), so the page can clear the way first. |

There is no `Model` parameter: the model is the cascaded `EditContext`'s, and one of another type
throws. Replacing that `EditContext` rebuilds the engine.

```razor
<EditForm Model="_report" OnSubmit="HandleSubmit" id="@GateId" tabindex="-1">
```

```razor
<FormidableValidator TModel="ExpenseReport" @ref="_validator" FocusFallback="MakeSubmitterReachableAsync" Context="formidable">
    <div class="summary-slot">
        <FormidableSummary FocusFallback="MakeSubmitterReachableAsync" />
    </div>
```

<!-- Excerpt from `samples/Formidable.Sample/Pages/AttachMode.razor` -->

| When | What you see |
|---|---|
| A submit through `ValidateForSubmitAsync()` blocks | Focus lands on the first error as under `FormidableForm`: same `FocusFirstErrorOnInvalidSubmit` switch, `PrepareFocus` awaited, `FocusFallback` threaded in. |
| A summary lists issues, or "first" is chosen | The engine's order, not the page's: nothing here resolves where the fields sit ([Migration guide](migration-guide.md#attach-mode-lists-issues-in-the-engines-order-not-the-pages)). |
| The page offers the [displaced-click guard](#the-click-a-disclosure-displaces) neither candidate | That section's diagnostic, once per context: it stays until the cascaded `EditContext` is replaced. |

`ChildContent` is a typed fragment and so is `EditForm`'s own, so the Razor compiler asks for a
`Context="..."` on one of the two. What collides is the declaration, not any use of it. Everything
needing the cascade nests inside this component rather than sitting beside it.

The members a page calls:

| Member | What it does |
|---|---|
| `ValidateForSubmitAsync()` | Runs the submit pipeline against this component's engine and hands back the [`SubmitOutcome`](severity.md#does-a-warning-or-an-info-block-the-submit) untouched, for the page's own `EditForm` handler to route. |
| `FocusFirstErrorAsync()` | [Makes the first-error move on demand](#asking-for-the-first-error-move) (the same code that submit runs) and answers whether an element took focus. |
| `ApplyServerIssues(...)` | Applies a server verdict, as a sequence of issues or a deserialized `FormidableValidationProblem`, and focuses nothing where `FormidableForm`'s overloads move; [Server integration](server-integration.md#why-did-focus-move-when-i-applied-the-reply) has the round trip. |
| `DiscloseLoadedValuesAsync(CancellationToken)` | [Says what the loaded values have earned](#saying-what-loaded-values-have-earned), that contract whole. |
| `NotifyFieldSetChanged()` | Reconciles the rendered field set now. Ordinary use never calls it: the component notices a field arriving or leaving on its own, shortly after the render that moved it, so removing a row prunes its live issues and [re-checks the whole form](collections-and-row-identity.md#what-happens-when-a-row-leaves-or-the-list-reorders) unasked ([`/attach`](../samples/Formidable.Sample/Pages/AttachMode.razor) removes a line with `List.Remove` and nothing more). |
| `Engine` | The engine as the non-generic `IFormidableEngine`. |

Call them from the renderer's synchronization context, and after the component has bound to its
`EditContext`. Before that there is no engine: the four that require one throw a message saying
exactly that, `Engine` reads `null`, and `NotifyFieldSetChanged()` does nothing.

**The `<form>` is the page's, and so is everything `FormidableForm` renders on one:**

| Attribute | Where it comes from here |
|---|---|
| `id` and `tabindex="-1"` | `FormidableFieldId.For(new FieldIdentifier(model, string.Empty))`, the model-level field's id, which the all-suppressed gate's summary entry and the focus service both address. |
| `novalidate` | The page, wherever it wants FluentValidation to answer a submit ahead of the browser's own constraint UI. Leaving it off is a real choice rather than an oversight. |
| `aria-describedby` | `FormidableFieldId.MessagesFor` of that same field, and owed only where the page renders a [`FormidableModelMessage`](#formidablemodelmessage). Compute it rather than appending `-messages` by hand, and leave the attribute off with the component: an id naming nothing is ignored by a screen reader, and is what a scanner reports. |
| `inert` | The page, for [the prerender window](hosting-models.md#what-happens-inside-the-prerender-window) a Blazor Web App opens. `FormidableForm` renders it on the `<form>` it owns while interactivity is coming and has not arrived; there is no `<form>` here for this component to render it on. Write `inert="@(!RendererInfo.IsInteractive)"` on a page that carries a render mode for the same refusal. |

An attribute `EditForm` does not recognise lands on the `<form>` it renders, which is how each of
them gets there.

## `FormidableCollectionMessage<TValue>`

A `List<T>` property can carry its own rule (`RuleFor(x => x.Items).NotEmpty()`) with no single
control anywhere in the form to register the path that rule reports against. This is
`FormidableFieldMessage`'s collection-level sibling for exactly that gap.

| Parameter | Type | Default | One line |
|---|---|---|---|
| `For` | `Expression<Func<TValue>>` | required | Accessor naming the collection-level field, e.g. `() => Model.Teams`. |
| `KeepRegistered` | `bool` | `false` | [Keeps the field registered after the component is disposed](#virtualize-and-keepregistered), for a virtualized container. |
| `AdditionalAttributes` | `IReadOnlyDictionary<string, object>?` | `null` | Splatted onto the rendered `<ul>`, under [the message list's three positions](#formidablefieldmessagetvalue). |

```razor
<FormidableCollectionMessage For="() => _roster.Teams" />
```

<!-- Excerpt from `samples/Formidable.Sample/Pages/Collections.razor` -->

| When | What you see |
|---|---|
| It stands alone | Nothing to pair it with: it registers its own path, which no input could, so a submit can disclose the rule's message. |

The rendering is identical to `FormidableFieldMessage`'s, down to the persistent `<ul>` and the
splat policy. [Collections and row identity](collections-and-row-identity.md#how-do-i-nest-one-collection-inside-another)
has the nested-collection pattern this exists for.

## `FormidableField<TValue>` and `FormidableFieldContext`

Not every control belongs to the kit: a UI library's own `<select>`, a checkbox group, a third-party
date-picker widget. `FormidableField` is the any-UI-library integration point for those. It renders
no markup of its own: it registers its field, and hands its `ChildContent` a fresh
`FormidableFieldContext` on every render.

| Parameter | Type | Default | One line |
|---|---|---|---|
| `For` | `Expression<Func<TValue>>` | required | Accessor naming the field, e.g. `() => Model.Colour`. |
| `ChildContent` | `RenderFragment<FormidableFieldContext>` | required | Your markup, handed the field's context as `context` unless a `Context="..."` renames it. |
| `KeepRegistered` | `bool` | `false` | [Keeps the field registered after the component is disposed](#virtualize-and-keepregistered), for a virtualized container. |

[The foreign-control pattern](#the-foreign-control-pattern) below is the worked example: a plain
`<select>`, its label, and the change handler that commits the value.

| When | What you see |
|---|---|
| You splat `field.InputAttributes` | The `id`, the state class and the aria attributes; `NotifyChanged()` stays your handler's: only your markup knows which event commits the value. |
| You call `MarkTouched()` alone | The field goes touched with no check ever running, which looks like validation quietly doing nothing. |

What the context carries:

| Member | What it is |
|---|---|
| `Field` | The `FieldIdentifier` this context describes. |
| `ElementId` | The field's deterministic element id: what the control renders, and what a `<label for="...">` targets. |
| `State` | The field's current `FieldState`: whether it is touched, modified or being checked, the severities it carries, and `WouldPassSubmit`, meaning a submit would not fail the field. |
| `CssClass` | The state class string a Formidable input would compute for the same field. |
| `Issues` | The field's current issues, any severity. |
| `AriaInvalid` | True while the field carries error-severity issues. |
| `AriaDescribedBy` | The id of the element holding the field's messages, or `null` where it has none. Deliberately a single id rather than a merged list: a control carrying its own hint composes the two itself. |
| `Requirement` | How firmly the submit profile's rules demand a value: [`Required`, `ConditionallyRequired` or `NotRequired`](#formidablerequiredindicatortvalue). A control reading all three and deciding for itself is the only way to draw anything for the middle one. |
| `InputAttributes` | `id` and `class`, plus `aria-invalid`, `aria-describedby` and `aria-required` where each applies, bundled for one `@attributes` splat. |
| `NotifyChanged()` | States that a committed value change happened: it marks the field touched, engages it, and starts a live check. |
| `MarkTouched()` | Marks the field touched without notifying a change, for a blur or focus-out handler. |

### Naming a field whose type your own component doesn't know

`For` is `Expression<Func<TValue>>`, and `TValue` is inferred from the accessor. A shared component
of your own that has to name a field of any type declares its parameter `Expression<Func<object>>`
and forwards it:

```razor
@using System.Linq.Expressions

<label>@Label <FormidableRequiredIndicator For="For" /></label>
<FormidableFieldMessage For="For" />

@code {
    [Parameter, EditorRequired]
    public Expression<Func<object>> For { get; set; } = default!;

    [Parameter]
    public string? Label { get; set; }
}
```

| When | What you see |
|---|---|
| The call site writes `<MyField For="() => _order.Description" />` | Supported: the accessor is written exactly as for a typed `For`, landing on the same field. |
| The forwarding target is `FormidableField`, `FormidableFieldMessage`, `FormidableCollectionMessage`, `FormidableFieldAnchor` or `FormidableRequiredIndicator` | It works: the five take `TValue` from `For` alone, resolving through `FieldIdentifier.Create`. |
| The forwarding target is a typed input | It does not: an input's `TValue` is what it binds, so `FormidableInputText`'s `For` is `Expression<Func<string?>>`, nothing else. |

## `FormidableFieldAnchor<TValue>`

Registration and nothing else, for a field rendered by markup Formidable doesn't wrap and that isn't
using `FormidableField` either (a raw `<input>`, a native `<select>` bound by hand, a third-party
component). Putting it next to the raw control is the whole fix for a submit that never discloses
the field ([Disclosure](disclosure.md#formidablefieldanchor-for-raw-and-foreign-controls) has why
registration is the gate).

| Parameter | Type | Default | One line |
|---|---|---|---|
| `For` | `Expression<Func<TValue>>` | required | Accessor naming the field to register, e.g. `() => Model.SubmitterName`. |
| `KeepRegistered` | `bool` | `false` | [Keeps the field registered after the component is disposed](#virtualize-and-keepregistered), for a virtualized container. |

```razor
<FormidableFieldAnchor For="() => _report.SubmitterName" />
```

<!-- Excerpt from `samples/Formidable.Sample/Pages/AttachMode.razor` -->

| When | What you see |
|---|---|
| A check runs | Nothing: it renders nothing and observes no engine state, having no markup a check could make it re-render. |
| The field is registered (anchor or direct call) | A submit discloses its errors ([Disclosure](disclosure.md#why-did-it-appear-when-i-pressed-submit)); under [`EngagedAndVisible`](options.md#livedisclosure) its live ones too; [`NeverRegisteredFieldDiagnostic`](options.md#neverregisteredfielddiagnostic) goes quiet for it. |
| A handle held across a model swap | It belongs to a registry nothing consults: the host rebuilt engine and registry together. Register again. |
| A summary click should reach a directly registered control | Render `FormidableFieldId.For(field)` on it yourself: registration alone computes no element id. |

`FieldRegistry.Register` is public, and calling it yourself is supported for the case the anchor
cannot express: a field you already hold as a `FieldIdentifier` rather than as an accessor
expression. `FormidableFormContext.Registry` is the route to it, the `FieldRegistration` it returns
is the handle, and disposing that handle unregisters (unless the call passed
`keepRegistered: true`).

What the anchor adds over a bare call is the lifecycle around it: a component takes a new
registration whenever the cascaded context instance is replaced, so re-registering is a direct
caller's to own. The element id is owed either way: [Vanilla interop](#vanilla-interop) below
renders one beside an anchor on a native `InputText`.

## The foreign-control pattern

`FormidableField`'s sample page wraps a plain `<select>` (a control Formidable does not, and cannot
know how to, wrap itself):

```razor
<FormidableForm Model="_order" OnValidSubmit="HandleValid">
    <div class="summary-slot">
        <FormidableSummary />
    </div>

    <FormidableField For="() => _order.Colour" Context="field">
        <div class="field">
            <label for="@field.ElementId">Colour</label>
            <select @attributes="field.InputAttributes"
                    value="@_order.Colour" @onchange="args => OnColourChanged(args, field)">
                <option value="">Choose…</option>
                <option>Red</option>
                <option>Green</option>
                <option>Blue</option>
            </select>
        </div>
        <FormidableFieldMessage For="() => _order.Colour" />
    </FormidableField>

    <div class="actions">
        <button type="submit">Submit</button>
    </div>
</FormidableForm>
```

<!-- Excerpt from `samples/Formidable.Sample/Pages/ForeignControl.razor` -->

The change handler lives in the code-behind:

```csharp
private void OnColourChanged(ChangeEventArgs args, FormidableFieldContext field)
{
    _order.Colour = args.Value?.ToString() ?? string.Empty;
    field.NotifyChanged();
}
```

<!-- Excerpt from `samples/Formidable.Sample/Pages/ForeignControl.razor.cs` -->

| When | What you see |
|---|---|
| You label the control | `for="@field.ElementId"` rather than a wrap: the label has to target the foreign element's own id, which only the field context knows. |
| You render the `<select>` | `@attributes="field.InputAttributes"` carries the id, the state class and the aria attributes: what a kit input renders on itself. |
| The value changes | The handler sets the model, then `field.NotifyChanged()`: what `FormidableInputBase` does on a commit, called by hand since no base bakes it in. |

## `FocusFallback`

A focus move misses when nothing on the page carries the field's id, or when the element that does
will not take focus ([CSS and accessibility](css-and-accessibility.md#why-did-nothing-take-focus) has both
routes). `FocusFallback` is the escape hatch for both: it receives the field that could not be
reached.

`FormidableForm`, `FormidableValidator` and `FormidableSummary` each declare it, with the same
`Func<FieldIdentifier, ValueTask<bool>>?` shape, so a page wiring more than one hands the same
callback to each. What differs is what an unset one leaves behind:

| Where it is unset | What a miss does |
|---|---|
| `FormidableSummary` | Nothing, matching the component's pre-fallback behaviour: the click simply has no effect. |
| `FormidableForm` | Reports a diagnostic. Its three moves (a blocked submit, either `ApplyServerIssues` overload's move for a rejected round trip, and the one `FocusFirstErrorAsync()` asks for) have nowhere else for the visitor to land. |
| `FormidableValidator` | Reports a diagnostic, for the same reason. Two moves reach it rather than three, since applying server issues here focuses nothing. |

| When | What you see |
|---|---|
| Your callback returns `true` | The move is retried exactly once, into the element you made reachable. |
| Your callback returns `false` | The miss stands. |
| The field is merely covered by an overlay | No fallback fires: the element takes focus and reports success. [`PrepareFocus`](#preparefocus) clears the way first. |
| One callback on form and summary | The blocked submit's own move recovers too: a visitor who never clicks the summary lands in the failing row. |

[Virtualize and `KeepRegistered`](#virtualize-and-keepregistered) below walks a worked callback end
to end; [`/workout`](../samples/Formidable.Sample/Pages/Workout.razor) does the same with one
`RecoverMissedFocusAsync` on the form and the summary.

## `PrepareFocus`

[`FocusFallback`](#focusfallback) recovers a move that has already missed. A field can be
unreachable without ever producing one: a modal overlay covers it while leaving it perfectly
focusable, so the move succeeds, reports success, and puts the caret in a box the visitor cannot
see. `PrepareFocus` runs first, so the page can clear the way before a move is attempted at all.

`FormidableForm`, `FormidableValidator` and `FormidableSummary` each declare it as
`Func<FieldIdentifier, ValueTask>?`, so one page callback wires to all three.

| When | What you see |
|---|---|
| A dialog opens from `OnInvalidSubmit` | The form's own move lands behind the overlay unless [suppressed](#suppressing-the-automatic-focus). |
| A dismissing callback is wired to the form as well as the summary | The form's move runs it: the dialog closes the instant it appeared. |
| The form's move is suppressed | A summary click is the move left, and the one this hook prepares. |
| A `FocusFallback` retry follows a miss | The hook does not run again: once per move, ahead of the first attempt. |

The dialog sample's dismissing callback:

```csharp
    private async ValueTask DismissAnnouncementAsync(FieldIdentifier field)
    {
        if (_announcement is not null)
        {
            await _announcement.CloseAsync();
        }
    }
```

**Complete when the target is genuinely reachable, not when it has started becoming reachable.**
Closing a dialog runs a transition, removes an overlay and any scroll lock, and hands focus back to
whatever opened it: that hand-back takes a premature move straight back. So a dismissal
completes on the dialog's own closed event, not on the state change that starts the close.
[Recipes](recipes.md#i-want-a-modal-dialog-to-announce-a-blocked-submit) has the dialog's markup
and the two nestings it depends on.

`PrepareFocus` runs only where a move is actually about to be made:

| Where a move is called off | What the hook does |
|---|---|
| No `IFormidableFocusService` is registered | Nothing prepares, since nothing is going to move. |
| The move finds no visible issue to land on | Nothing prepares, for the same reason. |
| `FocusFirstErrorOnInvalidSubmit="false"`, or a handler suppressed that submit's move | The root's hook goes unrun. A summary nested inside it still prepares its own clicks, which is the dialog shape above. |

A throw is handled the way the path already handles a throwing `FocusFallback`. Where it surfaces
depends on what asked for the move:

| The move | Where a throw surfaces |
|---|---|
| A blocked submit, or the one a page asks for on either root or through the cascaded context | Out of that call: `SubmitAsync`, `ValidateForSubmitAsync` or `FocusFirstErrorAsync()`. |
| A summary entry's click | Out of the click. |
| Either of `FormidableForm`'s `ApplyServerIssues` overloads | Nowhere. Applying server issues is synchronous by contract, so its move is fire-and-forget and the throw becomes an unobserved task exception. |

## `AddFormidableBlazor()`

The one-call registration for a Blazor client. It adds everything `AddFormidable()` registers, plus
the three JS-backed services: focus, DOM value sync, and field order.
[Server integration](server-integration.md) has the server-side registration it mirrors.

```csharp
/// <summary>
/// Registers Formidable's core services (see <see cref="FormidableServiceCollectionExtensions.AddFormidable"/>)
/// plus <see cref="IFormidableFocusService"/>, <see cref="IFormidableDomValueSync"/> and
/// <see cref="IFormidableFieldOrderService"/>. The one-call registration for Blazor consumers.
/// Existing registrations are respected.
/// </summary>
public static IServiceCollection AddFormidableBlazor(this IServiceCollection services)
{
    ArgumentNullException.ThrowIfNull(services);
    services.AddFormidable();
    services.TryAddScoped<IFormidableFocusService, FormidableFocusService>();
    services.TryAddScoped<IFormidableDomValueSync, FormidableDomValueSync>();
    services.TryAddScoped<IFormidableFieldOrderService, FormidableFieldOrderService>();
    return services;
}
```

<!-- Source: `src/Formidable.Blazor/FormidableBlazorServiceCollectionExtensions.cs` -->

| When | What you see |
|---|---|
| One of the three is already registered | It stays: nothing here overwrites an existing registration, so a bUnit test registers its stand-ins first ([Testing](testing.md#the-form-under-bunit)). |
| You call the `Action<FormidableOptions>` overload | The configured instance becomes the app-wide default every form omitting `Options` uses, respecting an existing `FormidableOptions` registration the same way. |

Each of the three is a public interface over an internal JS-backed implementation, which is what
makes a stand-in possible; [Testing](testing.md#the-form-under-bunit) has what a double over each
one makes assertable.

The overload is the second step of the three-step options order stated in
[Need to know](#need-to-know): parameter, then this, then `new FormidableOptions()`. A design
system's class names belong here rather than on every page;
[Engine options](options.md#app-wide-defaults) has the copy a form builds when it wants those
defaults and one setting of its own, and what mutating a shared singleton reaches.

## Culture at WebAssembly boot

A WebAssembly app fixes its culture before `RunAsync()` and downloads its satellite resource
assemblies for that one culture, so a stored language choice is applied there, not from a page
afterwards ([Hosting models](hosting-models.md#culture-at-webassembly-boot) has the rule, and what
a Blazor Server host does instead).

Formidable ships nothing for this: where the choice is kept, under what key, and what a stale one
should do are the app's. The sample writes it in the open: `Program.cs` hands a reader to
[`CultureBootstrap`](../samples/Formidable.Sample.Shared/CultureBootstrap.cs), which parses the
stored name and sets both `CultureInfo.DefaultThreadCurrentCulture` and
`DefaultThreadCurrentUICulture` to it.

```csharp
var js = host.Services.GetRequiredService<IJSRuntime>();
await CultureBootstrap.ApplyStoredCultureAsync(
    () => js.InvokeAsync<string?>("formidableSample.getCulture").AsTask(),
    CultureInfo.GetCultureInfo("en-AU"));

await host.RunAsync();
```

<!-- Excerpt from `samples/Formidable.Sample/Program.cs` -->

| When | What you see |
|---|---|
| `ApplyStoredCultureAsync` reads a missing, blank or unrecognisable value | The fallback applies instead. |
| `ApplyStoredCultureAsync` is passed no fallback | The app keeps the culture it booted with, so a corrupted stored value cannot stop it starting. |
| You give `ApplyStoredCultureAsync` your own reader | Any storage fits, since the reader is a delegate: `localStorage` under `formidable.culture` here, a cookie or a profile elsewhere. |

Once the culture is set, FluentValidation's own message translations follow
`CultureInfo.CurrentUICulture` with no further wiring
([Profiles](profiles.md#where-do-display-names-and-localized-messages-come-from) has the localization story and the
display-name half of it).

## Virtualize and `KeepRegistered`

A `Virtualize` container disposes rows that scroll out of view even though they remain part of the
form, and without `KeepRegistered` a scrolled-away row's field unregisters and its message goes
quiet ([Disclosure](disclosure.md#keepregistered-and-virtualization) has the rule and the four types
that take the parameter). The sample pairs it with [`FocusFallback`](#focusfallback) on both the
form and the summary, so a row far outside the render window is kept disclosed and stays reachable
by a click:

```razor
<FormidableForm Model="_order" Options="_options" OnValidSubmit="HandleValid" FocusFallback="ScrollToRowAsync">
    <div class="summary-slot">
        <FormidableSummary FocusFallback="ScrollToRowAsync" />
    </div>

    <div class="scroll-panel">
        <Virtualize Items="_order.Gadgets" ItemSize="RowHeight" Context="gadget">
            <div class="field" @key="gadget">
                <label>Serial
                    <FormidableInputText @bind-Value="gadget.Serial" KeepRegistered="true" />
                </label>
                <FormidableFieldMessage For="() => gadget.Serial" />
            </div>
        </Virtualize>
    </div>

    <div class="actions"><button type="submit">Submit</button></div>
</FormidableForm>
```

<!-- Excerpt from `samples/Formidable.Sample/Pages/Virtualized.razor` -->

| When | What you see |
|---|---|
| A never-rendered row fails | Listed in the summary from the first submit: the sample's [`DisclosureOverride`](options.md#disclosureoverride) lifts the visibility gate; validation always ran the full model. |
| You click a summary entry for a far row | `ScrollToRowAsync` scrolls the panel to `index * RowHeight`, waits 120 ms for `Virtualize` to render it, returns `true`. |

`ScrollToRowAsync` is the one callback wired to both `FocusFallback` parameters above:

```csharp
private const float RowHeight = 118f;
```

```csharp
private async ValueTask<bool> ScrollToRowAsync(FieldIdentifier field)
{
    if (field.Model is not Gadget gadget)
    {
        return false;
    }

    var index = _order.Gadgets.IndexOf(gadget);
    if (index < 0)
    {
        return false;
    }

    await Js.InvokeVoidAsync("formidableSample.scrollPanelTo", ".scroll-panel", index * RowHeight);
    await Task.Delay(120);
    return true;
}
```

<!-- Excerpt from `samples/Formidable.Sample/Pages/Virtualized.razor.cs` -->

A summary entry for a row inside the current render window focuses it directly. After the scroll,
the retry's own `scrollIntoView` centres the row exactly, so `RowHeight` only needs to be close.

## Vanilla interop

Because `FormidableForm` renders a real `EditForm`, plain Blazor form components work inside it
unmodified. Here a native `InputText` and `ValidationMessage` sit beside a Formidable input in the
same form:

```razor
<FormidableForm Model="_order" OnValidSubmit="HandleValid" @ref="_form">
    <div class="summary-slot">
        <FormidableSummary />
    </div>

    <div class="field">
        <label>Nickname (native InputText)
            <InputText @bind-Value="_order.Nickname"
                       id="@NicknameId"
                       aria-invalid="@NicknameAriaInvalid"
                       aria-describedby="@NicknameAriaDescribedBy" /></label>
        <ValidationMessage For="() => _order.Nickname" id="@NicknameMessagesId" />
        <FormidableFieldAnchor For="() => _order.Nickname" />
    </div>

    <div class="field">
        <label>Colour (Formidable input)
            <FormidableInputText @bind-Value="_order.Colour" /></label>
        <FormidableFieldMessage For="() => _order.Colour" />
    </div>

    <div class="actions">
        <button type="submit">Submit</button>
    </div>
</FormidableForm>
```

<!-- Excerpt from `samples/Formidable.Sample/Pages/VanillaInterop.razor` -->

| When | What you see |
|---|---|
| The native `InputText` renders | The state classes a Formidable input gets, pending included, with nothing wired: [the class provider](css-and-accessibility.md#the-fieldcssclassprovider-bridge) the engine installs. |
| The first committed change | Engages the field, so its live errors reach the native `ValidationMessage` whether or not anything registered it ([Disclosure](disclosure.md#why-isnt-my-message-showing-yet)). |
| An attribute reads engine state, as `aria-invalid` does here | The page must re-render on change: the sample subscribes to `Engine.StateChanged`, as the kit's inputs do. |

Three additions finish the crossing, one per concern, because a native input has no field context to
take any of them from:

| The addition | What it buys |
|---|---|
| `FormidableFieldAnchor` beside the control | A submit's disclosure: a plain `InputBase` registers nothing, so without the anchor no submit reveals `Nickname` ([Disclosure](disclosure.md#formidablefieldanchor-for-raw-and-foreign-controls)). Under [`LiveIssueDisclosure.EngagedAndVisible`](options.md#livedisclosure) the live channel needs the same registration. |
| The field's own id | Focus: a Formidable input renders `FormidableFieldId.For(field)` as its element id, and here `NicknameId` computes it. An `<input>` carrying it takes a summary's click exactly like a wrapped one ([CSS and accessibility](css-and-accessibility.md#where-does-focus-go-on-a-blocked-submit)). |
| `aria-invalid` and `aria-describedby` | The assistive-technology half: `GetFieldState(field).HasErrors` answers the first, and `EditContext.GetValidationMessages(field)` decides whether the second names the `-messages` id at all, since a native `ValidationMessage` renders no element while the field is clean. [CSS and accessibility](css-and-accessibility.md#why-did-my-own-aria-invalid-vanish-from-a-native-inputtext) has what a Blazor `InputText` does with a named `aria-invalid` and with an absent one. |
