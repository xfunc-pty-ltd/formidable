# The component kit

**You should already know:** the four components that make a working form
([Quickstart](quickstart.md)), and the first look at `FormidableField` and
`FormidableCollectionMessage` around a list of rows
([A list of members](tutorial/4-collections.md)).

Formidable's kit is headless. Every piece renders unstyled markup, and a field's state reaches
your stylesheet and assistive technology as class names and ARIA attributes ([CSS and
accessibility](css-and-accessibility.md)). This page catalogs every component and seam in the kit:
what each declares, one example, and the behaviour a signature does not give away.

| Name | What it is |
|---|---|
| [`FormidableForm<TModel>`](#formidableformtmodel) | The root that owns the `EditContext` and renders the `<form>`. |
| [`FormidableInputText` and `FormidableInputBase<TValue>`](#formidableinputtext-and-formidableinputbasetvalue) | The kit's text box, and the base every input here shares. |
| [`FormidableInputSelect<TValue>`](#formidableinputselecttvalue) | A `<select>` over a typed field, converting the string the DOM reports. |
| [`FormidableInputTextArea`](#formidableinputtextarea) | The multiline sibling of `FormidableInputText`, differing only in the element tag. |
| [`FormidableInputNumber<TValue>`](#formidableinputnumbertvalue) | An `<input type="number">` converted through the invariant culture. |
| [`FormidableInputDate<TValue>`](#formidableinputdatetvalue) | An `<input type="date">` formatted and parsed through the invariant culture. |
| [`FormidableFieldMessage<TValue>`](#formidablefieldmessagetvalue) | One field's current issues, any severity, as a persistent list. |
| [`FormidableModelMessage`](#formidablemodelmessage) | A message list for verdicts about the form rather than about any field. |
| [`FormidableRequiredIndicator<TValue>`](#formidablerequiredindicatortvalue) | Marks a field the submit profile demands a value for. |
| [`FormidableSummary`](#formidablesummary) | The visible issues, grouped by severity, each entry focusing its field. |
| [`FormidableValidator<TModel>`](#formidablevalidatortmodel-attaching-to-an-existing-form) | The root that attaches to an `EditForm` the page already owns. |
| [`FormidableCollectionMessage<TValue>`](#formidablecollectionmessagetvalue) | `FormidableFieldMessage`'s sibling for a collection-level rule. |
| [`FormidableField<TValue>` and `FormidableFieldContext`](#formidablefieldtvalue-and-formidablefieldcontext) | Renderless: hands your own markup the field's state, ids and issues. |
| [`FormidableFieldAnchor<TValue>`](#formidablefieldanchortvalue) | Registration and nothing else, for a control the kit does not render. |
| [The foreign-control pattern](#the-foreign-control-pattern) | `FormidableField` around a plain `<select>`, worked end to end. |
| [`FocusFallback`](#focusfallback) | The parameter that recovers a focus move that missed. |
| [`PrepareFocus`](#preparefocus) | The parameter awaited before a move is attempted. |
| [`AddFormidableBlazor()`](#addformidableblazor) | The one-call registration, and where app-wide options are set. |
| [Culture at WebAssembly boot](#culture-at-webassembly-boot) | Where a WebAssembly app applies a stored language choice. |
| [Virtualize and `KeepRegistered`](#virtualize-and-keepregistered) | Keeping a scrolled-away row disclosed and reachable. |
| [Vanilla interop](#vanilla-interop) | Native Blazor inputs and `ValidationMessage` inside a Formidable form. |

## Need to know

Four names make a complete form, met together in [Quickstart](quickstart.md). `FormidableForm` is
the one with a contract worth stating first, and `FormidableValidator` resolves identically through
the same factory. Three things resolve as the engine is built, and where a parameter exists for one,
what you passed wins:

- **The validator.** An explicit `Validator` parameter wins; otherwise `IModelValidator<TModel>`
  comes from the container. What wins is the whole validator, capabilities included: the shipped
  FluentValidation adapter implements `IRuleInspectingValidator<TModel>` and
  `IRuleLevelValidator<TModel>` beside the validation seam, and a wrapper written against
  `IModelValidator<TModel>` alone presents neither. Derive one from
  `DelegatingModelValidator<TModel>`, which forwards all three:
  [wrap the validator](recipes.md#i-want-to-wrap-the-validator-without-losing-what-it-can-do).
- **The introspector.** `IModelIntrospector` comes from the container or throws. There is no
  parameter for it.
- **The options.** An `Options` parameter wins, then an app-wide default registered through
  [`AddFormidableBlazor(...)`](#addformidableblazor), then `new FormidableOptions()`.

Two more resolve with no parameter to win. The logger comes from `ILoggerFactory` where one is
registered and stays `null` otherwise. It carries the
[suppressed-issue report](options.md#suppressedissuediagnostic), and an Information line naming a
validator that cannot report its own rules, which
[wrapping a validator](recipes.md#i-want-to-wrap-the-validator-without-losing-what-it-can-do)
explains.

The clock comes from `TimeProvider`, falling back to `TimeProvider.System`. Every timer the engine
arms rides it, so a test can drive the debounce windows with a `FakeTimeProvider` —
[Testing](testing.md#the-form-under-bunit) shows the shape.

Resolution fails loudly, and the validator's two failures are how a first form fails to start:

```csharp
return resolved ?? throw new InvalidOperationException(
    $"No IModelValidator<{FriendlyTypeName.Of(typeof(TModel))}> is registered in the " +
    "container this render is resolving from — call services.AddFormidableBlazor() and " +
    "register the FluentValidation validator there. A two-project Blazor Web App has " +
    "one container per project, and a page that prerenders or runs on the server's " +
    "circuit resolves from the server's, so register there too.");
```

<!-- Excerpt from `src/Formidable.Blazor/FormidableEngineFactory.cs` -->

```csharp
/// <summary>Names the missing validator registration and the two ways to make it.</summary>
internal static string For(Type modelType)
{
    var name = FriendlyTypeName.Of(modelType);
    return $"No FluentValidation validator for '{name}' is registered, so Formidable's " +
        $"IModelValidator<{name}> adapter cannot be constructed. Register one with " +
        $"services.AddScoped<IValidator<{name}>, {name}Validator>(), or register a whole " +
        "assembly's validators at once with services.AddValidatorsFromAssembly().";
}
```

<!-- Source: `src/Shared/MissingFluentValidatorMessage.cs` -->

The first fires when the container this render is resolving from has no `IModelValidator<TModel>`.
[Hosting models](hosting-models.md#the-server-builds-the-form-too) has the page shape a two-project
app most often meets it on.

The second is the commoner: Formidable is registered, so the open-generic adapter exists, but the
FluentValidation validator it wraps does not. The container throws while *building* the adapter
rather than returning null, so that exception is caught and renamed, with the original kept inside.
`Formidable.AspNetCore` reports it in the same words. Both strings are Formidable's own, so a
trimmed WebAssembly build keeps them readable.

## `FormidableForm<TModel>`

The primary root component. It owns the `EditContext`, and nothing else in a Formidable-based form
creates or replaces one. It renders a real `EditForm` underneath, so standard Blazor forms interop
keeps working: native `InputBase` descendants, `ValidationMessage`, a `DataAnnotationsValidator`
alongside it.

| Parameter | Type | Default | One line |
|---|---|---|---|
| `Model` | `TModel` | required | The object being edited. Omitting it throws a message naming the component and the parameter; binding a different instance rebuilds the `EditContext` and the engine. |
| `ModelChanged` | `EventCallback<TModel>` | unbound | Invoked when `ResetAsync` swaps the model; `@bind-Model` is what binds it. |
| `Validator` | `IModelValidator<TModel>?` | `null` (from the container) | The validator this form validates through, whole — see [Need to know](#need-to-know). |
| `Options` | `FormidableOptions?` | `null` (app-wide default, then `new FormidableOptions()`) | The [engine options](options.md) this form is built with. A different instance arriving without a new `Model` throws, since the engine [reads `Options` once](options.md#formidableoptions-is-read-once). |
| `ChildContent` | `RenderFragment<FormidableFormContext>?` | `null` | The form's content, handed the cascaded `FormidableFormContext` as `context`: the engine's members, plus `FocusFirstErrorAsync()`, which lives there because `PrepareFocus` and `FocusFallback` are the form's. Nesting another typed fragment that also leaves its parameter name implicit makes the Razor compiler ask for a `Context="..."` on one of the two, since what collides is the declaration rather than any use of it. |
| `OnValidSubmit` | `EventCallback<SubmitOutcome>` | unbound | Runs when the submit pipeline passes, with the [`SubmitOutcome`](severity.md#warnings-and-infos-never-block) it produced. A parameterless handler binds too. |
| `OnInvalidSubmit` | `EventCallback<FormidableInvalidSubmitContext>` | unbound | Runs when it blocks, with a context carrying that outcome and the [suppression call](#suppressing-the-automatic-focus). A parameterless handler binds too. |
| `FocusFirstErrorOnInvalidSubmit` | `bool` | `true` | Whether the form moves focus itself on a blocked submit and on a rejected round trip. |
| `FocusFallback` | `Func<FieldIdentifier, ValueTask<bool>>?` | `null` | Recovers a move that missed: make the element reachable, return `true`, and it retries once. |
| `PrepareFocus` | `Func<FieldIdentifier, ValueTask>?` | `null` | Awaited before each move this form makes, so the page can clear the way first. |
| `AdditionalAttributes` | `IReadOnlyDictionary<string, object>?` | `null` | Splatted onto the rendered `<form>`, under the three positions below. |

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
| `ApplyServerIssues(...)` | Applies a server verdict — a sequence of issues, or a deserialized `FormidableValidationProblem`. Focuses the page's first error where the payload carries one, under `FocusFirstErrorOnInvalidSubmit` — [Server integration](server-integration.md#client-round-trip) has the round trip. |
| `Engine` | The engine as the non-generic `IFormidableEngine`, where `IsValidating`, `HasSubmitted` and `IsFormValid` are read — the last meaning nothing until [`TrackFormValidity`](options.md#trackformvalidity) is on. |

Call the methods from the renderer's synchronization context, and after the form's first render —
before that there is no engine and they throw.

**The `<form>` takes three positions of its own against the splat**, across the four attributes it
renders:

| Attribute | Against the splat | What it is |
|---|---|---|
| `id` and `tabindex="-1"` | win outright | The model-level field's deterministic `FormidableFieldId`, which the all-suppressed gate's summary entry and the focus service both address, so its value cannot be a consumer's to choose. |
| `novalidate` | loses outright | Renders by default. Splat `novalidate="@false"` — the `bool`, since the string `"false"` would still render the attribute — to hand the submit back to the browser's own constraint UI. |
| `aria-describedby` | merges | Splatted ids first, the model-level message list's id appended, so a `FormidableModelMessage` describes the form with nothing wired and a page's own hint stays where the page put it. |

`novalidate` renders by default so that the browser's own interactive constraint validation never
answers a submit ahead of FluentValidation. `FormidableValidator` renders no `<form>`, so in attach
mode all four attributes are the page's own to write —
[see its section](#formidablevalidatortmodel-attaching-to-an-existing-form).

Where the form and the engine both declare a member, the engine's is what the context reaches, so
`context.Engine.ApplyServerIssues(...)` is the quiet background apply. `ResetAsync` stays out of
reach, since it rebuilds the engine the context belongs to. An inline *read* through the context
refreshes when the form itself re-renders, not on every validation pass, so a live spinner wants
something subscribed to `Engine.StateChanged` to re-render the markup holding it.

A page rendered statically with no interactivity coming can render a form but never submit one, so
the form asks for a render mode in its own words. [Troubleshooting](troubleshooting.md) has the 400
a host answers where that guard cannot help.

**A blocked submit moves focus to the first error**, not the first entry: [issue order follows the
page](#the-order-entries-appear-in), so a field above the failing one may carry nothing worse than
a warning. [CSS and accessibility](css-and-accessibility.md#focus-service) has the rest of why, and
what a miss looks like. [`FocusFallback`](#focusfallback) recovers a miss and
[`PrepareFocus`](#preparefocus) prevents one; with no `IFormidableFocusService` registered nothing
moves and nothing throws.

The fallback to the first visible issue of any severity applies only where a blocked submit shows
no error at all. Supersession produces that state: a second submit, or the pass
`DiscloseLoadedValuesAsync` runs, landing before this one's verdict did — the two things a caller
starts and awaits. Every other block writes an error-severity issue, the all-suppressed gate's
explanation included. A validator fault during a submit propagates to the caller rather than
blocking at all.

**Sample:** [`/scroll-focus`](../samples/Formidable.Sample/Pages/ScrollFocus.razor) — a toggle
flips `FocusFirstErrorOnInvalidSubmit`, so the automatic move and the opt-out sit side by side.

### Asking for the first-error move

`FocusFirstErrorAsync()` makes the move on demand. It is the same move the submit path makes, so
the field it lands on, the `PrepareFocus` awaited ahead of the attempt and the `FocusFallback` that
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

It answers `true` when an element took focus and `false` when nothing did — no visible issue, no
`IFormidableFocusService` registered, or an element the miss and its fallback could not reach
between them. That reports the move rather than the form, so read `Engine.GetVisibleIssues()` to
tell a quiet form from an unreachable error.

A component nested *inside* the form asks for the same move through the cascaded context, with no
`@ref` to reach for: `context.FocusFirstErrorAsync()` calls the root's method, so everything above
applies. A `FormidableFormContext` built through its public constructor (the shape
[Testing](testing.md) points at) has no root behind it, so it moves nothing and answers `false`.

### Suppressing the automatic focus

A dialog announcing a blocked submit is the case `FocusFirstErrorOnInvalidSubmit` alone handles
badly. The form's own move runs immediately after `OnInvalidSubmit` returns, inside the same submit
call, so the caret lands in a box the overlay is covering. Turning the parameter off fixes that
submit and every other one, including those where no dialog opens.

The handler declares it instead, at the moment it opens the dialog:

```csharp
    private async Task AnnounceAsync(FormidableInvalidSubmitContext context)
    {
        context.SuppressFirstErrorFocus();
        await _announcement!.OpenAsync();
    }
```

That is a statement about this submit: the form builds a fresh `FormidableInvalidSubmitContext` for
every block, so nothing latches. `FirstErrorFocusSuppressed` reads what has been said so far, for a
handler delegating to a helper.

It suppresses; it does not request. Asking is
[`FocusFirstErrorAsync()`](#asking-for-the-first-error-move), and the sequence ends there: suppress,
open the dialog, close it, focus. The form cannot make the call itself, because what breaks the
move is a handler having *covered the form*, and one that logs telemetry or scrolls a banner into
view has not.

**Sample:** [`/dialog-submit`](../samples/Formidable.Sample/Pages/DialogSubmit.razor) — a toggle
skips the suppressing call, so the failure it prevents is visible rather than described.

### Returning the form to pristine

`ResetAsync` is the named verb for "start over" — the clean slate a `Model` swap produces, without
needing a different object to get it:

```csharp
await _form!.ResetAsync();                // same instance, back to pristine
await _form!.ResetAsync(new Order());     // a different instance — needs @bind-Model
```

Called with nothing, it rebuilds the engine and the `EditContext` over the model instance already
bound. Touched and modified state, the message store, the advisory buckets and `HasSubmitted` all
clear, and any pending refresh is cancelled: none of it survives the engine it belonged to.

An in-flight `SubmitAsync` is abandoned with that engine too. Neither submit callback fires, focus
does not move, no render is triggered, and it returns a blocked outcome carrying nothing.

Called with a model, it swaps to that instance — durably only if the parent's own field moves too,
since Blazor re-supplies `Model` from whatever the parent still holds on every one of the
*parent's* renders. `ResetAsync` invokes `ModelChanged` for that, and `@bind-Model` binds it:

```razor
<FormidableForm @bind-Model="_order" @ref="_form" OnValidSubmit="HandleValid">
```

Supplying a new model with no `ModelChanged` bound throws instead, naming that fix: a targeted
exception beats a swap that works until an unrelated re-render undoes it.

**Sample:** [`/profiles`](../samples/Formidable.Sample/Pages/Profiles.razor) — a Reset button
beside the draft and submit buttons whose state it clears.

### Saying what loaded values have earned

A form filled from somewhere other than this visitor's typing — a saved draft, a record opened for
editing — looks pristine however good or bad its contents are, since writing model properties
notifies nothing. `DiscloseLoadedValuesAsync` answers for what is already there:

```csharp
_proposal.Title = "Progressive disclosure in practice";
_proposal.ContactEmail = "ada.lovelace";
_proposal.Summary = string.Empty;

await _form!.DiscloseLoadedValuesAsync();
```

<!-- Excerpt from `samples/Formidable.Sample/Pages/DraftLoad.razor.cs` -->

It validates the whole model under `SubmitProfile`, then decides field by field on whether the
field **holds a value**:

| The field | What it is marked | What you see |
|---|---|---|
| Holds a value no rule fails with an error | Touched and engaged | The valid class, or whichever advisory tier its warnings and infos earn |
| Holds a value the rules do fail | Touched and engaged | Its message, inline and in the summary |
| Holds nothing: null, a blank or whitespace-only string, an empty collection, or a non-nullable value type's own default | Neither | Nothing, unstyled and silent, with any required mark it carries still standing |

"Holds a value" is FluentValidation's own `NotEmpty()` negated, taken against the type the member
is **declared** as: a saved `false` in a `bool?` is an answer, an untouched `bool` is not. The
ambiguity is exactly the non-nullable value types, and it errs towards silence. Model an optional
value as `T?` and a load reads it exactly.

Disclosing a wrong value needs only the model, so it happens whatever the validator is. Confirming
a good one needs the validator's own list of the fields it has rules for: nothing else separates a
field whose rules all passed from one no rule mentions. A validator that cannot report its rules
therefore confirms nothing.

That list is the validator's declared shape, so a field inside a collection row is confirmed
exactly as a top-level one is.

Where a value cannot be read at all — a nested path whose owner is null, a model-level failure
naming no member — nothing is claimed, and the field is left unstyled rather than painted red.

A seeded value is a value: seed one your own rules reject and the form rejects it the moment the
values load. Seeding the default leaves the field silent.

The cost is that whole-model pass, async rules included, plus the live pass that discloses what it
found. It moves no focus, and a form that never calls it is unaffected in every respect.

**Sample:** [`/draft-load`](../samples/Formidable.Sample/Pages/DraftLoad.razor) — three saved
values and three different answers, side by side.

## `FormidableInputText` and `FormidableInputBase<TValue>`

`FormidableInputText` is the kit's text box; `FormidableInputBase<TValue>` is the base under it and
under every other input here. The base is public because deriving from it is [the supported
way](#deriving-your-own-input) to get a control the kit does not ship.

Every input here takes these parameters; the input sections below list only what each one adds.

| Parameter | Type | Default | One line |
|---|---|---|---|
| `For` | `Expression<Func<TValue>>?` | `null` | The explicit field accessor, `() => _order.Description`. Supply it when the input has no `@bind-Value`, or to point registration, messages and focus at a field other than the one the value binds. It wins over `ValueExpression` whenever both are present, silently and by design. |
| `Value` | `TValue?` | `default` | The field's current value. |
| `ValueChanged` | `EventCallback<TValue?>` | unbound | Raised when the input commits a new value. |
| `ValueExpression` | `Expression<Func<TValue>>?` | `null` | The accessor behind `Value`, filled in by the Razor compiler for every `@bind-Value` — the same `Value`/`ValueChanged`/`ValueExpression` triple native inputs take, so ordinary markup names the field once. Not one you write by hand. With neither this nor `For`, the input throws as its parameters are set, naming itself and both spellings. |
| `KeepRegistered` | `bool` | `false` | Keeps the field registered after the component is disposed, for a virtualized container — see [Virtualize and `KeepRegistered`](#virtualize-and-keepregistered). |
| `UpdateOn` | `InputUpdateMode` | `InputUpdateMode.OnChange` | Which DOM event commits the value, and whether the engine hears about it then or at the next blur. |
| `AdditionalAttributes` | `IReadOnlyDictionary<string, object>?` | `null` | Splatted onto the rendered element, ahead of every value the component computes. |

```razor
<div class="field"><label>Name <FormidableInputText @bind-Value="_contact.Name" /></label>
    <FormidableFieldMessage For="() => _contact.Name" /></div>
```

<!-- Excerpt from `samples/Formidable.Sample/Pages/Quickstart.razor` -->

Label-wrapping is what every sample using `FormidableInputText` does: a wrapping label needs no
`for`. Markup that cannot wrap addresses the input by its id, as [the foreign-control
pattern](#the-foreign-control-pattern) does.

`AdditionalAttributes` enters the render tree ahead of every value the component computes, and
Blazor applies last-write-wins. That is what settles the three attributes below; the `onblur`
binding is the deliberate exception to it, chaining rather than racing:

| Splatted | What the input does with it |
|---|---|
| `class` | Merges: the splatted value first, the computed state class after. A consumer writing `class="form-control"` keeps it and still gets whichever state class applies, plus `formidable-pending` while a pass is in flight. |
| `aria-describedby` | Merges: the splatted ids first, the messages id appended, so a persistent hint keeps its association as issues come and go. |
| `id` | Dropped. The rendered id is always the deterministic one, because the message list, `aria-describedby` and `IFormidableFocusService` all address the field by it. |
| `@onblur` | Chains rather than being claimed: the consumer's handler runs first and is awaited, then the kit's own blur work follows. |

Which aria attributes render, and when, is [CSS and
accessibility](css-and-accessibility.md#aria-invalid-and-aria-describedby)'s subject:
`aria-invalid` for error-severity issues, `aria-describedby` for issues of any severity, and
`aria-required` for what the submit profile's rules demand rather than what the value is doing.

**`UpdateOn` decides which DOM event commits the value.** `OnChange` (the default) and `OnInput`
commit and notify the engine together. `OnBlur` splits the two: `change` commits the value and arms
a notification, `blur` delivers it. However many commits accumulate before that blur it
delivers exactly one, a blur with none armed delivers none, and a string the control cannot parse
commits nothing and so arms nothing.

Registration is what makes a field's issues visible to progressive disclosure. It happens whenever
the cascaded context is a new instance, which is where the field and its two ids resolve: a rebind
is exactly when those answers can have changed.

[`FormidableFieldAnchor`](#formidablefieldanchortvalue) is the route for registering a field and
nothing else. `FormidableComponentBase`, under `FormidableInputBase<TValue>` and
`FormidableFieldAnchor` alike, is public only because a public component cannot inherit a less
accessible base, and is not an extension point.

### The click a disclosure displaces

Under `OnChange`, pressing Submit blurs the field the visitor was in, and that blur is the commit.
The message it discloses is inserted above the button, the button leaves the pointer, and the
browser dispatches the click on the nearest common ancestor of the press and the release — for a
submit button, usually the `<form>`, where nothing is listening.

[CSS and accessibility](css-and-accessibility.md#pointer-activation-and-a-button-that-moves) has
why that is the ordinary case on a form rather than an exotic one. Both roots close it with a
guard, installed by default, that re-delivers the click to the button it began on;
[`ClickRecovery`](options.md#clickrecovery) turns it off.

The press has to have begun on a `<button>`, an `<input type="submit">` or an
`<input type="image">` inside the root, and then all three of these hold together:

| Condition | Why recovery waits for it |
|---|---|
| The browser retargeted the click to an ancestor that contains the pressed button | A click delivered to the button, or to anything inside it, arrived where it was meant to and needs nothing done for it. |
| The pointer stayed where it was pressed, within a few pixels of drift | This is the discrimination the platform cannot make for itself, since cancel-on-a-different-target reads the same for a pointer dragged off a still button. A press dragged off its button still cancels. |
| The button's border box changed | Measured against the viewport, so a scroll under a still pointer counts. An unchanged box means something merely opened over the button, and intercepting a click is what an element opened over one is for. |

What the re-delivered click is:

| Property | Consequence |
|---|---|
| One click, not two | The mis-delivered click is suppressed as the recovery goes out, so a delegated click handler, an analytics listener or a click-outside-to-close guard each see one press as one click. |
| Untrusted | `isTrusted` reads `false` on the button and on every element along its path, so code gating on that flag, yours or a third-party widget's, can decline it. Script cannot dispatch a trusted event, so that is recovery's price rather than something the option can settle. |
| Still activated | Transient user activation survives, because the delivery happens inside the browser's own handling of the displaced click. An activation-gated API still works from the recovered click. |

`FormidableValidator` renders no element of its own, so it looks in two places for one to scope to:
the element carrying the model-level field id, taken when that element is a `<form>` or has a
recoverable button inside it, and failing that the `<form>` a field this component registered sits
in.

A page offering neither gets no guard, and is told so through the same `Trace` line plus
`ILoggerFactory` warning an unwired `FocusFallback` miss uses. The message names three ways out:
put the model-level `FormidableFieldId` on the `EditForm` this attaches to, place it around a
field this component registers, or set [`ClickRecovery`](options.md#clickrecovery) to `None` and
ask for no guard at all.

Two things a page can do instead, both of which remove the shift rather than recovering from it:

| Instead of recovering | What it takes |
|---|---|
| `UpdateOn="InputUpdateMode.OnInput"` on the fields above the button | Disclosure then happens as the visitor types, so the message is already on screen when the button is pressed and nothing moves. It costs a validation pass per keystroke, the trade [`/normalize`](../samples/Formidable.Sample/Pages/Normalize.razor) and [`/css-colours`](../samples/Formidable.Sample/Pages/CssColours.razor) make. |
| Reserve the space the messages will take | Real height, not a persistent wrapper alone, because an empty element is zero-height. `FormidableFieldMessage` renders its list element always, which is what gives a stylesheet something stable to give a `min-height` to. The sample's own `.summary-slot` shows the weaker version's limit: the summary's wrapper and regions persist, and the button below them still moves when the bands arrive. |

### Deriving your own input

The kit wraps a control when the wrapper meaningfully improves its validation UX. A native
`<input>` whose `type` only changes what the browser renders — `type="range"`, `type="tel"`,
`type="password"` — needs no wrapper: splat the type onto `FormidableInputText`, whose binding is a
plain `string` with no parsing in it and so no culture.

A control binding an actual `decimal` or `DateOnly` through the base's generic overload gets real
parsing instead, under the current thread's culture. That is the gap `FormidableInputNumber` and
`FormidableInputDate` close, and the reason they ship at all.

A control the kit has no wrapper for — a checkbox binding through `checked`, a third-party
component — stays native, paired with [`FormidableFieldAnchor`](#formidablefieldanchortvalue) or
driven by [`FormidableField`](#formidablefieldtvalue-and-formidablefieldcontext).

The bar the kit applies to itself decides what the kit ships, not what you ship: derive when a
control in your own form reads better wrapped. A validated range slider, in full:

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

Consume it like any kit input — `<RatingInput @bind-Value="_feedback.Rating" min="0" max="5" />` —
where `min` and `max` splat through `AdditionalAttributes` untouched.

| What the base gives you | What deriving asks of you |
|---|---|
| `AddCommonAttributes(builder, sequence)` renders the splat, then the `id`, the `class` and the aria attributes, in the order that merges a consumer's `class` and `aria-describedby` and drops their `id` | Call it first, immediately after opening the element. It consumes `sequence` through `sequence + 3`, so the control's own attributes start at `sequence + 4`. |
| `AddValueBinding(builder, sequence, …)` renders the commit attribute `UpdateOn` calls for, and `blur` where the mode or the control needs it. Three overloads, one implementation underneath, so a mode the control knows nothing about still gets a correct binding | Call it last, immediately before closing the element. What the control renders between the two calls lands after the splat, so it wins the duplicate-attribute race too. Rendering an attribute *before* `AddCommonAttributes` is the deliberate opposite: it offers a default a consumer's splat can override, which is what `step="any"` does on the number input below. |
| `Field`, `ElementId` and `MessagesElementId` are the resolved identifier and the two deterministic ids, computed once at registration rather than per render | Render `ElementId` as written. `Register` is sealed: which field the control speaks for is not adjustable, because an input that resolved a different field would render no usable id and register nothing for disclosure. |
| `State` and `CssClass` are the field's current state and the merged class string | Nothing. `Context` is the route to everything the rest does not cover: `Context.Engine.GetIssues(Field)` to render messages yourself, `Context.EditContext`, `Context.Registry`. |
| `SetCurrentValueAsync`, `CommitValueAsync` and `NotifyChanged` are commit-and-notify in one call, or the two halves apart | For a control driving a commit from a handler of its own rather than through `AddValueBinding`. |
| `SyncsDomValueOnBlur` and `SyncDomValueAsync` bind `blur` in every `UpdateOn` mode and run your write there — after any consumer-splatted `@onblur`, and before the notification `OnBlur` delivers | Override both and inject `IFormidableDomValueSync`. The write is the override's whole job. |
| `OnParametersSet` is where the field resolves, registers, and binds the engine subscription | Call `base` from any override, or the control registers nothing and never re-renders when a pass lands. |
| `Dispose` is non-virtual and always releases the registration and the subscription | Override `DisposeCore` for resources of your own. A control implementing `IAsyncDisposable` has to call `Dispose()` from its `DisposeAsync`, since Blazor calls only the async overload when a component implements both. |
| `ObservesEngineState` and `OnEngineStateChanged` are why a control re-renders when a validation pass lands | Leave them alone unless you mean it: `false` freezes the state class, `aria-invalid`, `aria-describedby` and `aria-required` at whatever the last render gave them. Override the relay and call `base` so the re-render still happens. |

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

`TValue` can be `string`, `bool`, an enum, or anything else `BindConverter` converts from a string:
native `InputSelect<TValue>`'s set, no broader, reached by the same
`BindConverter.TryConvertTo<TValue>` call with the same `bool`/`bool?` special case, since
`BindConverter` reserves boolean conversion for conditional HTML attributes.

One it cannot convert at all throws an `InvalidOperationException` naming the component and the
type the first time a change commits, exactly as native does. Multi-select, an array-typed
`TValue`, is out of scope.

A committed string that merely parses badly leaves the model untouched rather than raising a parse
error of its own, and should not arise anyway when every `<option>`'s `value` was formatted the way
the component formats it. Formidable has no native-parse-error side channel the way
`InputBase<TValue>` does, so FluentValidation stays the only source of validation truth.

One divergence from native, and the only one: a null `bool?` formats as no selection, a blank
`<option>`, where native formats it as the string `"false"`. That is what lets a blank option clear
a `bool?` field back to unanswered.

The control hands the base a formatted value and a try-parse-and-commit step, so which event
carries the commit stays the base's. One coercion comes with the element: a `<select>` has no
meaningful `input` event distinct from `change`, so `OnInput` behaves exactly like `OnChange`.
`OnBlur` still commits on that same `change` and defers only the notification to the blur.

A `<label>` wrapping a `<select>` takes in every `<option>`'s text along with its own, defeating an
exact-match label lookup in test tooling, though nothing about it is an accessibility defect. An
explicit `for=` sidesteps it: `CategoryId` above is `FormidableFieldId.For(_post, p => p.Category)`,
the expression overload of the id the component computes internally, so no `nameof` has to stay in
sync.

**Sample:** [`/custom-profiles`](../samples/Formidable.Sample/Pages/CustomProfiles.razor) —
`Category` is the select, carrying `UpdateOn="OnBlur"` so picking an option commits at once while
the message waits for the blur.

## `FormidableInputTextArea`

The multiline sibling of `FormidableInputText`: same base, same parameters, same `UpdateOn` choice.
The only difference is the element tag, mirroring native `InputTextArea` exactly.

```razor
<div class="field"><label>Body <FormidableInputTextArea @bind-Value="_note.Body" UpdateOn="InputUpdateMode.OnInput" /></label>
    <FormidableFieldMessage For="() => _note.Body" /></div>
```

<!-- Excerpt from `samples/Formidable.Sample/Pages/Normalize.razor` -->

Unlike a `<select>`, a `<textarea>`'s accessible name from an implicit label wrap behaves the same
as an `<input>`'s, since its content is its value rather than enumerable child elements. The usual
wrap works exactly the way it does for `FormidableInputText`.

**Sample:** [`/normalize`](../samples/Formidable.Sample/Pages/Normalize.razor) — `Body` is the
textarea.

## `FormidableInputNumber<TValue>`

An `<input type="number">` reports its value period-decimal — `"12.5"`, never `"12,5"` — whatever
the browser's locale, while the base's typed binding resolves the current thread's culture. Under a
comma-decimal culture that pairing reads `12.5` as `125` rather than failing loudly.

`FormidableInputNumber<TValue>` converts through `CultureInfo.InvariantCulture` instead, supplying
its own synchronous parser to the base's string-projected overload. `OnInput` binds a real `input`
event here: a number box fires one per keystroke where a `<select>` does not.

It adds no parameters of its own to [the shared
set](#formidableinputtext-and-formidableinputbasetvalue).

```razor
<div class="field"><label>Read minutes <FormidableInputNumber @bind-Value="_post.ReadMinutes" /></label>
    <FormidableFieldMessage For="() => _post.ReadMinutes" /></div>
```

<!-- Excerpt from `samples/Formidable.Sample/Pages/CustomProfiles.razor` -->

`TValue` is checked once, in a static constructor, against native `InputNumber<TValue>`'s set:
`int`, `long`, `short`, `float`, `double`, `decimal` and their nullable forms. An unsupported one
never reaches an instance — the runtime wraps the thrown `InvalidOperationException` in a
`TypeInitializationException` the first time that closed generic is touched.

Two attributes take fixed positions against the splat:

| Attribute | Position | Why |
|---|---|---|
| `step="any"` | Before the splat, so a consumer's own `step` wins | HTML's own default `step` is `1`, which makes any fractional value a native `stepMismatch`. The default retires that at the source, matching native `InputNumber<TValue>` for every type it supports. |
| `type="number"` | After the splat, so the component wins | The same position `RatingInput`'s `type="range"` takes above. |

Under `FormidableForm` a `stepMismatch` would not block a submit, since the form renders
`novalidate` (see [above](#formidableformtmodel)). But `novalidate` switches off interactive
validation rather than the constraint computation, so the field would still match `:invalid` and
the spinner buttons would still snap. [CSS and
accessibility](css-and-accessibility.md#aria-invalid-and-aria-describedby) has what a form without
`novalidate` does with a constraint the browser enforces.

A string the parser rejects, an emptied box where `TValue` is not nullable included, leaves the
field uncommitted: the model stays as it was, and under `OnBlur` nothing is armed for the blur to
deliver. Model an optional number as `int?` or `decimal?` so an emptied box commits `null` for
`NotNull()` to judge.

A native number box admits the characters of scientific notation, so it can display text like `e3`
while reporting an empty value — a difference no render-tree diff can see. Every blur therefore
writes the model's formatted value into the element through `IFormidableDomValueSync`.

**Sample:** [`/custom-profiles`](../samples/Formidable.Sample/Pages/CustomProfiles.razor) —
`Read minutes`, required and range-checked under the `Submit` ruleset.

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

`TValue` is checked the way `FormidableInputNumber` checks its own: a static constructor against
`DateTime`, `DateTimeOffset`, `DateOnly` and their nullable forms, failing the same
`TypeInitializationException`-wrapped way. Rendering follows the identical shape — `type="date"` in
the component-wins position, the same string-projected overload doing the parsing.

**Prefer `UpdateOn="InputUpdateMode.OnBlur"` for this component specifically.** Chromium fires a
native date input's `change` event once per typed segment — day, month, year — rather than once per
completed date, so the default `OnChange` can run a live pass, and briefly show a stale verdict,
against a year the visitor has not finished typing.

Under `OnBlur` the model still commits on every segment's `change`, so a wrapping form always reads
the field's current value, and the engine is notified once, on `blur`, after the value has had a
chance to settle. That notification is delivered only because those segment commits armed it:
tabbing through without committing anything notifies nothing.

[Options](options.md#updateon-per-input-not-a-formidableoptions-property) covers the same
per-segment problem for the general case; this component answers it directly rather than by
splatting `type="date"` onto a text box.

The same uncommitted-value, blur-sync and nullable-modelling rules as `FormidableInputNumber`
apply: an unparseable or emptied non-nullable box leaves the model untouched, every blur writes the
model's value back so half-entered segments cannot linger, and an optional date modelled as
`DateOnly?`, `DateTime?` or `DateTimeOffset?` commits `null` when emptied.

**Sample:** [`/custom-profiles`](../samples/Formidable.Sample/Pages/CustomProfiles.razor) —
`Publish date`, required under the `Submit` ruleset.

## `FormidableFieldMessage<TValue>`

Every field needs somewhere to show what's wrong with it. `FormidableFieldMessage` renders one
field's current issues, any severity, as an accessible list.

| Parameter | Type | Default | One line |
|---|---|---|---|
| `For` | `Expression<Func<TValue>>` | required | Accessor naming the field these messages speak for, e.g. `() => Model.Email`. |
| `AdditionalAttributes` | `IReadOnlyDictionary<string, object>?` | `null` | Splatted onto the rendered `<ul>`, under the three positions below. |

```razor
<div class="field"><label>Ticket reference <FormidableInputText @bind-Value="_ticket.Reference" /></label>
    <FormidableFieldMessage For="() => _ticket.Reference" /></div>
```

<!-- Excerpt from `samples/Formidable.Sample/Pages/SummaryShape.razor` -->

**Pair it with something that registers the same field** — a `FormidableInputBase` descendant,
`FormidableField` or `FormidableFieldAnchor` — or nothing a submit finds for that field reaches
the list. The live channel is the deliberate exception: it discloses an engaged field's verdict
without consulting registration (see
[Disclosure](disclosure.md#the-live-channel-plays-by-its-own-rule)), so an unpaired list can carry
live messages while staying silent about everything a submit revealed.

The `<ul>` itself renders always, empty when the field has no issues, with the items coming and
going inside it. That is what lets a consumer's CSS transition the list open and closed, and what
puts a configured [`InlineMessageLive`](options.md#inlinemessagelive) on an element that persists
across renders rather than one entering alongside the text it announces.

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
back every message still standing. `aria-live` costs neither: the list stays a list, atomicity
stays false, and only what changed is announced.

Its two bases, `FormidableMessageBase<TValue>` and `FormidableAccessorComponentBase<TValue>`, are
public only because a public component cannot inherit a less accessible base, and are extension
points no more than `FormidableComponentBase` is. Neither constructor is accessible outside the
assembly, so this component and
[`FormidableCollectionMessage`](#formidablecollectionmessagetvalue) are the only two shapes the
first takes.

## `FormidableModelMessage`

Some verdicts are about the form rather than about any field: the defensive gate's explanation for
a blocked submit that can show nothing (see [Disclosure](disclosure.md)), a server error applied
with an empty path, and the fault issue a throwing live rule leaves behind, which `GetIssues`
orders last.

They live on the model-level field, an empty `FieldIdentifier.FieldName`, which no
`FormidableFieldMessage` can name: `For` takes an accessor expression, and no expression reaches
that field. A form built on inline messages alone would show the gate — the one message whose
whole purpose is being seen — nowhere.

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

It renders the same persistent list as the field messages, under the model-level message id: the
form element's own id plus `-messages`, per `FormidableFieldId.MessagesFor`. A configured
[`InlineMessageLive`](options.md#inlinemessagelive) sits on that persistent element, which is the
point on a summary-less form. The gate's explanation arrives inside a live region assistive
technology already knew about.

It registers nothing. Registration is how a *field's* submit errors earn disclosure; the
model-level field is always disclosed, because its element is the form's own, on the page for as
long as the form is.

Beside a [`FormidableSummary`](#formidablesummary) the component is redundant — the summary already
lists model-level issues among everything else — so it earns its place on the form that has no
summary.

Attach mode changes the component not at all, only what points at the list. `FormidableForm`
describes its own `<form>` element with the list's id, while `FormidableValidator` renders no
`<form>` and cannot reach the one the page owns, so the page writes that `aria-describedby` itself,
beside the gate id — see
[the attach-mode section below](#formidablevalidatortmodel-attaching-to-an-existing-form).

**Sample:** [`/disclosure`](../samples/Formidable.Sample/Pages/Disclosure.razor) — the
summary-less variant under *Without a summary*: submit with the trip details collapsed and the
gate's explanation appears.

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

The mark is derived from the validator's rules rather than declared on the markup, so a presence
rule moving between profiles moves the mark with it. The submit profile is the one that decides,
because "required" on a form means "required before this can be submitted": a narrowed
`LiveProfile` changes when a message appears, never whether the value is demanded.

**It is `aria-hidden`, deliberately, and it is not the accessible half of this feature.** That a
value is demanded belongs on the input as `aria-required="true"`, which the kit's inputs and
`FormidableFieldContext.InputAttributes` put there.
[CSS and accessibility](css-and-accessibility.md#aria-invalid-and-aria-describedby) has why the
two are separate elements.

**Placement is markup position.** The component needs the cascaded context and its `For`, and
reads nothing from what surrounds it:

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

It registers nothing — a marker is not an input — so the validated input beside it or a
`FormidableFieldAnchor` keeps the field registered for disclosure. Nor does it observe engine
state: requiredness is a property of the rules, not of what the values are doing, so no validation
pass changes what it draws and a change to [`RequiredOverride`](options.md#requiredoverride) or
`SubmitProfile` lands on the next render.

Requiredness is a three-valued answer, and only one of the three draws anything:

| `FieldRequirement` | What it means | What the component draws |
|---|---|---|
| `Required` | The submit profile selects a presence rule for the field, and it carries no condition | `<span class="formidable-required" aria-hidden="true">` around [`RequiredIndicatorContent`](options.md#requiredindicatorcontent) — `"*"` unless you say otherwise |
| `ConditionallyRequired` | Every presence rule the profile selects for the field is conditional | Nothing. A condition cannot be evaluated without a model instance, so drawing the same mark would assert a demand the library cannot verify, and a validator whose presence rules are all conditional would mark every field on the form. A page that wants to say something there reads `FormidableFieldContext.Requirement` from a `FormidableField` and renders its own markup |
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

`NotRequired` therefore means "not known to be required", never "proven optional". Where the rules
cannot be read, the marker is what an option says instead:

| Option | What it does to the mark |
|---|---|
| [`RequiredOverride`](options.md#requiredoverride) | Declares the answer in both directions, and decides the marker and `aria-required` together so the two cannot disagree. |
| [`RequiredIndicatorContent`](options.md#requiredindicatorcontent) | Supplies the text inside the span. Formidable ships no styling, so `formidable-required` is a hook for your own stylesheet, and `""` renders the element empty — which keeps it on the page for a stylesheet's `::before` to draw a CSS-only glyph into. |
| [`ShowRequiredIndicators`](options.md#showrequiredindicators) | `false` turns every marker on the form off at once, with no element at all, and leaves `aria-required` exactly where it was. |

One practical note for tests: a query matching raw text sees the marker, and a role-and-name query,
which runs the accessible-name algorithm, does not. `aria-hidden` separates them, and the second
kind reflects what a visitor using assistive technology hears.

**Sample:** [`/workout`](../samples/Formidable.Sample/Pages/Workout.razor) — marked and unmarked
fields side by side across kit inputs, a foreign select, a native input and the attendee rows.

## `FormidableSummary`

Renders a live, severity-grouped list of the currently-visible issues across the form — every one
of them until you say otherwise.

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

The markup is built to be announced. One persistent `<div class="formidable-summary">` wrapper
holds fixed-role live regions that render from the first paint and stand empty while the form has
nothing to show. Errors band into `formidable-summary__region--errors`, which has carried
`role="alert"` since it rendered; warnings and infos band into
`formidable-summary__region--advisories` and its politer `role="status"`.

That split is the point of the two regions: an errors-free submit surfacing only advisories should
not interrupt the way a blocking error does. No role ever changes on any element, and every issue
arriving after a region's own first render lands inside a live region whose role was already there.
A role entering the DOM in the same render as the content it should announce is the shape that gets
dropped.

Each region also builds inside its own render-tree sequence space, so Blazor's sibling-by-sequence
diff cannot replace one region's element when the other's bands change. `Show` decides which
regions exist, and it is a parameter, so changing it at runtime while matching issues are already
on screen is where a region and its first band still share a render.

Each item is a button, and a click on it moves focus to the offending field through
`IFormidableFocusService`. Click-to-focus reaches a rendered element that will take focus: a row
outside a virtualized container's window has no DOM element yet, and one inside a collapsed section
has an element that refuses. Either way the click lands nowhere and the entry is a miss.

[`FocusFallback`](#focusfallback) is the escape hatch for a miss, and
[`PrepareFocus`](#preparefocus) beside it is for a field the caret can reach but the visitor cannot
see.

### The order entries appear in

Entries follow the page: within each severity group, issues list in the document order of the
fields that render them, so the first error a visitor reads about is the topmost one rather than
whichever rule the validator declared first.

`FormidableForm` supplies that order. After a render that changed the registered field set, or one
a browser-side observer reports moved the form's elements around, it asks
`IFormidableFieldOrderService` where those fields sit and hands the answer to its engine, which
sorts `GetVisibleIssues()` by it. The seam is JS-backed, only the browser knowing where an element
is, and public so a test can fake it ([Testing](testing.md#the-form-under-bunit)).

The seam's currency is the field rather than the rendered element id, an id being unreadable back
into the field it came from:

```csharp
ValueTask<IReadOnlyList<FieldIdentifier>?> OrderAsync(IReadOnlyList<FieldIdentifier> fields);
```

<!-- Source: `src/Formidable.Blazor/IFormidableFieldOrderService.cs` -->

`FormidableFieldId.For(field)` maps the other way, so an implementation can answer from DOM
position or from anything else it knows about a field. Five properties of the resulting order are
deliberate:

| Property | What it means |
|---|---|
| Two issues on one field keep validator order | The sort is by field, so it never reorders what one field reported. |
| A verdict about the whole form is reported first | The request is not only the registered fields: the model-level field rides along on every resolution, and its element is the `<form>`, which contains every field on the page. Document order therefore puts the all-suppressed gate's explanation and a validator fault ahead of everything else, which is where they belong. An implementation need not special-case it — `FormidableFieldId.For` derives its id the same way. |
| A field the page cannot place sorts last | The service answers only for the fields it can locate, so anything it leaves out sorts after everything it placed: a control that renders no id of its own, and a row held only by [`KeepRegistered`](#virtualize-and-keepregistered), which stays registered precisely because it has left the DOM. There is nowhere on the page to send a visitor for either. |
| An empty answer and no answer are different answers | An empty list says none of these fields are on the page, which is taken as the order. `null` says the order could not be resolved at all, and the form treats it as "ask again on a later render", the same way it treats an interop call that threw. Answer `null` where empty was meant and the form re-resolves on every render; answer empty where `null` was meant and it settles on validator order until the registered field set next changes, which on a stable form is never. |
| Before the first resolution, the order is the engine's own | A resolve lands after the render that produced the elements, so until one has, `GetVisibleIssues()` reports channel by channel: the fault issue, then submit errors, then advisories, then the live channel. The same is true of a host that never resolves an order at all — `FormidableValidator` in attach mode, or an app that registered no order service — which keeps the summary working and costs it only the reading order (see [Migration guide](migration-guide.md#what-to-check-after-migrating)). |

Document order is the default, being the order a visitor reads the form in. A form wanting another
sets [`FormidableOptions.OrderIssues`](options.md#orderissues), a re-sort over what the seam
resolved; an order the layout decides needs the seam itself
([Recipes](recipes.md#i-want-the-summary-ordered-by-where-fields-appear-on-screen)). Re-sorting is
the whole of what that delegate can do:

| The delegate | What happens |
|---|---|
| Leaves a field out of its result | The field is appended, in document order, rather than dropped: an issue nobody reports is an issue the visitor cannot act on, and withholding one is what disclosure is for, with its own diagnostic. |
| Names a field twice | Only its first position is kept. |
| Names a field it was never handed | That name is ignored, so a short answer padded out to the right length cannot push a real field out of the map. |
| Runs | At the same cadence as the resolution above: not per render, and not per `GetVisibleIssues()` call. Its answer is baked into the ordinal map the engine sorts by, so reading it back is a lookup per issue rather than another run of the delegate, and a criterion that moves on its own — a runtime "blocking ones first" toggle — is not picked up until the next resolution. |
| Throws | Nothing catches it, and it runs inside a render, so the throw takes the form down with it. It is synchronous by design: measuring the DOM has to be async, and async ordering already has a home in the seam above. |

### Showing one severity band

`Show` defaults to `SummaryFilter.All`, the single combined list above, and the other four members
narrow it. `Advisories` means every non-error issue — warnings, infos, and any severity outside
those two — exactly as `ValidationReport.Advisories` does. A page that wants the blocking problems
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

A filter matching nothing renders its region empty, the same as a clean form, the region persisting
so that what arrives later is announced from an element already carrying its role. A paragraph
above a quiet advisory summary is the page's to hide, keyed on the bands
(`:has(.formidable-summary__band)`), which exist exactly while the summary has something to say.

Each summary reads the same visible issues and filters them independently, so a default summary
beside a filtered one shows those issues twice, and two summaries are two live regions and two
announcements (see
[CSS and accessibility](css-and-accessibility.md#formidablesummary-as-a-live-region)).

**Sample:** [`/severity`](../samples/Formidable.Sample/Pages/SeverityLevels.razor) — two disjoint
summaries at the top of the form, the one with nothing to say taking no space.

### Heading each band

`ErrorsHeading`, `WarningsHeading` and `InfosHeading` give a severity band its own heading. Set one
and the summary renders it as an `h{HeadingLevel}` inside that band, with an id this component
mints and wires to the band's `<ul>` via `aria-labelledby`: the relationship is
the summary's to get right, not a consumer's to reconstruct. No English default stands in for one
you leave unset: the label is your own wording in your own language.

The band itself is a `<div class="formidable-summary__band formidable-summary__band--{severity}">`
wrapping the heading, if any, and the `<ul>`. It renders whether or not that band carries a
heading, so a stylesheet has one consistent shape to target: a panel per severity to style as one,
rather than styling spread across the `<ul>` and its `formidable-summary__group` class.

The band sits inside its severity's fixed-role region, which matters when styling with child
combinators: the chain is `.formidable-summary` > `__region` > `__band` > `ul`, so a
`.formidable-summary > ul` selector matches nothing and descendant selectors are the durable choice.

**Sample:** [`/severity`](../samples/Formidable.Sample/Pages/SeverityLevels.razor) — the errors
summary carries `ErrorsHeading`, the advisories one beside it both others.

### Deciding what an entry says

`ItemTemplate` receives the entry's `VisibleIssue`, the field and the issue together. The common
reason to reach for it is the field's name rather than the rule's complaint: `Issue.DisplayName` is
the `WithName(...)` value, or FluentValidation's own split of the property path.

```razor
<FormidableSummary>
    <ItemTemplate Context="entry">@entry.Issue.DisplayName</ItemTemplate>
</FormidableSummary>
```

An issue that carries no `DisplayName` renders nothing under a name-only template. The
all-suppressed gate's explanation and a validator fault carry none, and neither does an error
applied from a response body — the `errors` dictionary carries a path and a message and nothing
else. A server advisory carries one only where the response supplied it.

`SubmitOutcome.VisibleErrorSummary` falls back to the issue's `Path`, and to
[`ModelLevelDisplayName`](options.md#modelleveldisplayname) where that is empty too. A template
here does neither.

Rewording an entry changes what it reads as and nothing about where its click takes the visitor.
Different markup *around* the entries is a summary of your own —
[Recipes](recipes.md#i-want-my-own-summary-markup) walks that.

**Sample:** [`/summary-shape`](../samples/Formidable.Sample/Pages/SummaryShape.razor) — a toggle
puts the template in and takes it away over one blocked submit.

### One entry per field

A summary lists one entry per issue, so a field failing two rules is listed twice. That is right
for a list of messages and wrong for a list of names, where "Email" twice reads as a mistake in the
summary rather than in the form. `GroupByField` keeps each field's first issue and drops the rest:

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

`SubmitOutcome.VisibleErrorSummary` counts differently, and a page listing one beside the other
should know how. That list is the distinct *names* among the disclosed errors, where the grouped
errors band is distinct by *field* — so two fields carrying a single `WithName(...)` leave its
count one short of that band. One field whose two error rules carry different names leaves it one
long, since `WithName` applies to the rule that declared it.

**Sample:** [`/summary-shape`](../samples/Formidable.Sample/Pages/SummaryShape.razor) — its Ticket
reference field breaks two rules, so grouping is the difference between listing that name once or
twice.

### Capping the list

`MaxItems` counts entries, which is issues by default and fields under `GroupByField` — so
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
| Negative | Throws from `OnParametersSet`, for the reason an out-of-range `HeadingLevel` does: this is a number pages arrive at by arithmetic, and arithmetic that has gone below zero is a mistake worth seeing rather than a list that quietly empties itself. |

`OverflowTemplate` gets the entries that band held back, never a count of them, and a band that
held nothing back never reaches the fragment at all.

The component supplies the `<li class="formidable-summary__overflow">`; the fragment supplies what
goes inside. A line that only counts the entries reads `Count`, as above; having them is what lets
an expander reveal what did not fit instead. Each is the same `VisibleIssue` `ItemTemplate` gets
for a shown entry.

Leave it unset and a capped band renders **nothing** in place of what it dropped: no element, no
sentence. "And 3 more" is a sentence with a language and a plural rule behind it, and the summary
can pick neither.

**Sample:** [`/summary-shape`](../samples/Formidable.Sample/Pages/SummaryShape.razor) — its
overflow line is an expander naming what did not fit, and one more toggle takes the fragment away
so the band ends in silence.

## `FormidableValidator<TModel>`, attaching to an existing form

The root for a page whose `EditForm` is already its own. It attaches the engine to the cascaded
`EditContext` rather than creating one, so the `<form>` element and its submit handler stay the
page's. [Migration guide](migration-guide.md) has when to reach for it.

| Parameter | Type | Default | One line |
|---|---|---|---|
| `Options` | `FormidableOptions?` | `null` (app-wide default, then `new FormidableOptions()`) | The [engine options](options.md) this component builds its engine with. A different instance arriving without a new `EditContext` throws, since the engine [reads `Options` once](options.md#formidableoptions-is-read-once). |
| `Validator` | `IModelValidator<TModel>?` | `null` (from the container) | The validator this form validates through, whole — see [Need to know](#need-to-know). |
| `ChildContent` | `RenderFragment<FormidableFormContext>?` | `null` | The content the cascade reaches, handed the `FormidableFormContext` as `context` — or as whatever a `Context="..."` renames it to, which the Razor compiler asks for on this element or on the `EditForm` around it. With none, the component renders nothing at all, cascade included, rather than an empty wrapper. |
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

`ChildContent` is a typed fragment and so is `EditForm`'s own, so the Razor compiler asks for a
`Context="..."` on one of the two. What collides is the declaration, not any use of it. Everything
needing the cascade nests inside this component rather than sitting beside it.

The members a page calls:

| Member | What it does |
|---|---|
| `ValidateForSubmitAsync()` | Runs the submit pipeline against this component's engine and hands back the [`SubmitOutcome`](severity.md#warnings-and-infos-never-block) untouched, for the page's own `EditForm` handler to route. |
| `FocusFirstErrorAsync()` | [Makes the first-error move on demand](#asking-for-the-first-error-move) — the same code that submit runs — and answers whether an element took focus. |
| `ApplyServerIssues(...)` | Applies a server verdict, as a sequence of issues or a deserialized `FormidableValidationProblem`, and focuses nothing where `FormidableForm`'s overloads move — [Server integration](server-integration.md#client-round-trip) has the round trip. |
| `DiscloseLoadedValuesAsync(CancellationToken)` | [Says what the loaded values have earned](#saying-what-loaded-values-have-earned), that contract whole. |
| `NotifyFieldSetChanged()` | Reconciles the rendered field set now. Ordinary use never calls it: a registry signal the component defers past the render batch already does it, so removing a row prunes its live issues and arms a reconciling refresh unasked. |
| `Engine` | The engine as the non-generic `IFormidableEngine`. |

Call them from the renderer's synchronization context, and after the component has bound to its
`EditContext`. Before that there is no engine: the four that require one throw a message saying
exactly that, `Engine` reads `null`, and `NotifyFieldSetChanged()` does nothing.

**The `<form>` is the page's, and so is everything `FormidableForm` renders on one:**

| Attribute | Where it comes from here |
|---|---|
| `id` and `tabindex="-1"` | `FormidableFieldId.For(new FieldIdentifier(model, string.Empty))`, the model-level field's id, which the all-suppressed gate's summary entry and the focus service both address. |
| `novalidate` | The page, wherever it wants FluentValidation to answer a submit ahead of the browser's own constraint UI. Leaving it off is a real choice rather than an oversight. |
| `aria-describedby` | `FormidableFieldId.MessagesFor` of that same field, and owed only where the page renders a [`FormidableModelMessage`](#formidablemodelmessage). Compute it rather than appending `-messages` by hand, and leave the attribute off with the component: an id naming nothing is inert to a screen reader, and is what a scanner reports. |

An attribute `EditForm` does not recognise lands on the `<form>` it renders, which is how each of
them gets there.

**Focus parity is not order parity.** A blocked submit lands the visitor on the first error exactly
as it does under `FormidableForm`. What nothing resolves here is where the fields sit, so a summary
lists issues in the engine's channel order and "first" means first in that order.
[Migration guide](migration-guide.md#what-to-check-after-migrating) has the rest to check.

The [displaced-click guard](#the-click-a-disclosure-displaces) has to hunt for an element to scope
itself to here, and it hunts once per context. A page that offered it neither candidate keeps that
diagnostic until the cascaded `EditContext` is replaced.

**Sample:** [`/attach`](../samples/Formidable.Sample/Pages/AttachMode.razor) — a plain `InputText`
left unmigrated beside a Formidable-managed list, and a row whose removal needs nothing extra.

## `FormidableCollectionMessage<TValue>`

A `List<T>` property can carry its own rule — `RuleFor(x => x.Items).NotEmpty()` — with no
single control anywhere in the form to register the path that rule reports against. This is
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

The rendering is identical to `FormidableFieldMessage`'s, down to the persistent `<ul>` and the
splat policy. What differs is registration: this component makes its own, so it needs no pairing
with anything else, because a `List<T>` property with a collection-level rule has no rendered input
to register the path at all. See
[Collections and row identity](collections-and-row-identity.md) for the nested-collection pattern
this exists for.

## `FormidableField<TValue>` and `FormidableFieldContext`

Not every control belongs to the kit: a UI library's own `<select>`, a checkbox group, a
third-party date-picker widget. `FormidableField` is the any-UI-library integration point for
those. It renders no markup of its own: it registers its field, and hands its `ChildContent` a
fresh `FormidableFieldContext` on every render.

| Parameter | Type | Default | One line |
|---|---|---|---|
| `For` | `Expression<Func<TValue>>` | required | Accessor naming the field, e.g. `() => Model.Colour`. |
| `ChildContent` | `RenderFragment<FormidableFieldContext>` | required | Your markup, handed the field's context as `context` unless a `Context="..."` renames it. |
| `KeepRegistered` | `bool` | `false` | [Keeps the field registered after the component is disposed](#virtualize-and-keepregistered), for a virtualized container. |

[The foreign-control pattern](#the-foreign-control-pattern) below is the worked example: a plain
`<select>`, its label, and the change handler that commits the value.

What the context carries:

| Member | What it is |
|---|---|
| `Field` | The `FieldIdentifier` this context describes. |
| `ElementId` | The field's deterministic element id — what the control renders, and what a `<label for="...">` targets. |
| `State` | The field's current `FieldState`: touched, modified, validating, errors, warnings. |
| `CssClass` | The state class string a Formidable input would compute for the same field. |
| `Issues` | The field's current issues, any severity. |
| `AriaInvalid` | True while the field carries error-severity issues. |
| `AriaDescribedBy` | The id of the element holding the field's messages, or `null` where it has none. Deliberately a single id rather than a merged list: a control carrying its own hint composes the two itself. |
| `Requirement` | How firmly the submit profile's rules demand a value: [`Required`, `ConditionallyRequired` or `NotRequired`](#formidablerequiredindicatortvalue). A control reading all three and deciding for itself is the only way to draw anything for the middle one. |
| `InputAttributes` | `id` and `class`, plus `aria-invalid`, `aria-describedby` and `aria-required` where each applies, bundled for one `@attributes` splat. |
| `NotifyChanged()` | States that a committed value change happened: it marks the field touched, engages it, and runs the engine's live pass. |
| `MarkTouched()` | Marks the field touched without notifying a change — for a blur or focus-out handler. |

`NotifyChanged()` is wiring the splat cannot do for you, since only your own markup knows which
native event commits the control's value. Call `MarkTouched()` alone and the field goes touched
with no live pass ever running, which looks like validation quietly doing nothing.

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

That is supported, and the call site writes the accessor exactly as it would for a typed `For`:
`<MyField For="() => _order.Description" />`.

It works on the five components whose `TValue` comes from `For` alone: `FormidableField`,
`FormidableFieldMessage`, `FormidableCollectionMessage`, `FormidableFieldAnchor` and
`FormidableRequiredIndicator`. Each resolves the accessor through `FieldIdentifier.Create`, which
reads past the boxing conversion the compiler inserts for a value type. It lands on the same field
a typed `For` names.

The typed inputs are the exception, and not by omission. An input's `TValue` is the type it binds,
so `FormidableInputText` is a `FormidableInputBase<string?>` and its `For` is an
`Expression<Func<string?>>` and nothing else.

## `FormidableFieldAnchor<TValue>`

Registration and nothing else, for a field rendered by markup Formidable doesn't wrap and that
isn't using `FormidableField` either — a raw `<input>`, a native `<select>` bound by hand, a
third-party component. It renders nothing, and it observes no engine state either: a component with
no markup of its own has nothing a validation pass could make it re-render.

| Parameter | Type | Default | One line |
|---|---|---|---|
| `For` | `Expression<Func<TValue>>` | required | Accessor naming the field to register, e.g. `() => Model.SubmitterName`. |
| `KeepRegistered` | `bool` | `false` | [Keeps the field registered after the component is disposed](#virtualize-and-keepregistered), for a virtualized container. |

```razor
<FormidableFieldAnchor For="() => _report.SubmitterName" />
```

<!-- Excerpt from `samples/Formidable.Sample/Pages/AttachMode.razor` -->

Without something registering a field, a submit's verdict about it is never disclosed, and putting
the anchor next to the raw control is the whole fix. [Vanilla interop](#vanilla-interop) below
wires one beside a native `InputText`, and [Disclosure](disclosure.md) has why registration is the
gate.

`FieldRegistry.Register` is public, and calling it yourself is supported for the case the anchor
cannot express: a field you already hold as a `FieldIdentifier` rather than as an accessor
expression. `FormidableFormContext.Registry` is the route to it, the `FieldRegistration` it returns
is the handle, and disposing that handle unregisters — unless the call passed
`keepRegistered: true`.

What it buys is what the anchor buys. The field counts as rendered, so the submit channel stops
suppressing its errors as unrevealed, and under
[`LiveIssueDisclosure.EngagedAndVisible`](options.md#livedisclosure) the live channel stops
filtering them too. It also records the field as having registered at all, which is what makes it
eligible for the engagement prune once it leaves, and what quiets
[`NeverRegisteredFieldDiagnostic`](options.md#neverregisteredfielddiagnostic) for it.

What the anchor adds over a bare call is the lifecycle around it. A component takes a new
registration whenever the cascaded context instance is replaced, and a host that swaps its model
rebuilds engine and registry together, so a handle held across that swap belongs to a registry
nothing consults any more.

Calling the registry directly means owning that re-registration. It also means owning the field's
`FormidableFieldId.For` id, if a summary click is to reach the control.

## The foreign-control pattern

`FormidableField`'s sample page wraps a plain `<select>` — a control Formidable does not, and
cannot know how to, wrap itself:

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

The page uses `<label for="@field.ElementId">` rather than wrapping the control in a label,
because the label has to target the foreign element's own id, and only the field context knows it.
The change handler lives in the code-behind:

```csharp
private void OnColourChanged(ChangeEventArgs args, FormidableFieldContext field)
{
    _order.Colour = args.Value?.ToString() ?? string.Empty;
    field.NotifyChanged();
}
```

<!-- Excerpt from `samples/Formidable.Sample/Pages/ForeignControl.razor.cs` -->

`field.NotifyChanged()` there is exactly what the base's own `NotifyChanged` does for
`FormidableInputBase` descendants — mark touched, notify the `EditContext` — called explicitly
because there is no base class here to bake it into.

## `FocusFallback`

A focus move misses two ways. Nothing on the page carries the field's id, as for a virtualized row
outside the render window; or the element that carries it will not take focus, as one inside a
collapsed section will not.

`FocusFallback` is the escape hatch for both. It receives the field that could not be reached: make
the element reachable, return `true`, and the move is retried exactly once. Return `false` to leave
the miss as it is.

`FormidableForm`, `FormidableValidator` and `FormidableSummary` each declare it, with the same
`Func<FieldIdentifier, ValueTask<bool>>?` shape, so a page wiring more than one hands the same
callback to each. What differs is what an unset one leaves behind:

| Where it is unset | What a miss does |
|---|---|
| `FormidableSummary` | Nothing, matching the component's pre-fallback behaviour: the click simply has no effect. |
| `FormidableForm` | Reports a diagnostic. Its three moves — a blocked submit, either `ApplyServerIssues` overload's move for a rejected round trip, and the one `FocusFirstErrorAsync()` asks for — have nowhere else for the visitor to land. |
| `FormidableValidator` | Reports a diagnostic, for the same reason. Two moves reach it rather than three, since applying server issues here focuses nothing. |

[`PrepareFocus`](#preparefocus) is the better seam wherever the way can be cleared first: a field
merely covered by an overlay takes focus and reports success, so no fallback ever fires for it.

[Virtualize and `KeepRegistered`](#virtualize-and-keepregistered) below walks a worked callback end
to end. `/workout` does the same with one method: the `RecoverMissedFocusAsync` that recovers a
summary click for an off-screen session row also recovers the form's own auto-focus on a blocked
submit, so a visitor who never clicks the summary still lands in the row that failed.

## `PrepareFocus`

[`FocusFallback`](#focusfallback) recovers a move that has already missed. A field can be
unreachable without ever producing one: a modal overlay covers it while leaving it perfectly
focusable, so the move succeeds, reports success, and puts the caret in a box the visitor cannot
see.

`PrepareFocus` runs first, so the page can clear the way before a move is attempted at all — the
better seam even where a miss would be reported, since clearing the way beats recovering after.

`FormidableForm`, `FormidableValidator` and `FormidableSummary` each declare it as
`Func<FieldIdentifier, ValueTask>?`, so one page callback wires to all three. It returns no verdict
because there is none to give: the kit awaits it, then runs its usual try, fall back once, retry
pipeline unchanged.

**A dialog opened from `OnInvalidSubmit` has to
[suppress the form's own move](#suppressing-the-automatic-focus)** — left alone that move runs the
moment the dialog opens, and lands behind the overlay. Wire a dismissing `PrepareFocus` to the form
too and it closes the dialog the instant it appeared. Suppressed, a summary click is the move left,
and the one this hook prepares:

```csharp
    private async ValueTask DismissAnnouncementAsync(FieldIdentifier field)
    {
        if (_announcement is not null)
        {
            await _announcement.CloseAsync();
        }
    }
```

Two nestings in the dialog's markup are load-bearing, and
[Recipes](recipes.md#i-want-a-modal-dialog-to-announce-a-blocked-submit) shows them. The summary
sits inside the dialog so clickable entries are in front of the overlay, and the dialog sits inside
the form because `FormidableSummary` throws outside a root's cascade.

**Complete when the target is genuinely reachable, not when it has started becoming reachable.**
Closing a dialog runs a transition, removes an overlay and any scroll lock, and hands focus back to
whatever opened it — and that hand-back takes a premature move straight back. So a dismissal
completes on the dialog's own closed event, not on the state change that starts the close.

It runs once per move, ahead of the first attempt, and a `FocusFallback` retry does not run it
again. It runs only where a move is actually about to be made:

| Where a move is called off | What the hook does |
|---|---|
| No `IFormidableFocusService` is registered | Nothing prepares, since nothing is going to move. |
| The move finds no visible issue to land on | Nothing prepares, for the same reason. |
| `FocusFirstErrorOnInvalidSubmit="false"`, or a handler suppressed that submit's move | The root's hook goes unrun. A summary nested inside it still prepares its own clicks, which is the dialog shape above. |

A throw is handled the way the path already handles a throwing `FocusFallback`, which leaves one
rule to learn rather than two. Where it surfaces depends on what asked for the move:

| The move | Where a throw surfaces |
|---|---|
| A blocked submit, or the one a page asks for on either root or through the cascaded context | Out of that call — `SubmitAsync`, `ValidateForSubmitAsync` or `FocusFirstErrorAsync()`. |
| A summary entry's click | Out of the click. |
| Either of `FormidableForm`'s `ApplyServerIssues` overloads | Nowhere. Applying server issues is synchronous by contract, so its move is fire-and-forget and the throw becomes an unobserved task exception. |

**Sample:** [`/dialog-submit`](../samples/Formidable.Sample/Pages/DialogSubmit.razor) — a toggle
makes the dismissal report ready before the dialog has closed, so the premature move is visible.

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

`TryAddScoped` is why a consumer who has already registered their own `IFormidableFocusService`
keeps it: this call never overwrites an existing registration.

That is also what makes all three testable. Each is a public interface over an internal JS-backed
implementation, so a bUnit test registers its own stand-in first and this call leaves it alone. A
focus double makes which field a blocked submit moved to assertable. A DOM value sync double keeps a
number or date input off interop entirely. An `IFormidableFieldOrderService` double states a
document order without a document (see [Testing](testing.md#the-form-under-bunit)).

An overload takes an `Action<FormidableOptions>` and registers the configured instance as the
app-wide default, respecting an existing registration exactly as the call above does. Every form
omitting its own `Options` parameter then uses it.

That is the second step of the three-step options order stated in [Need to
know](#need-to-know): parameter, then this, then `new FormidableOptions()`. A design system's class
names belong here rather than on every page. [Engine options](options.md#app-wide-defaults) has the
copy a form builds when it wants those defaults and one setting of its own, and what mutating a
shared singleton reaches.

## Culture at WebAssembly boot

A WebAssembly app downloads its satellite resource assemblies for whatever culture is current when
`RunAsync()` is called, and never again. An app that lets a visitor choose a language therefore
applies the stored choice *before* that line, not from a page afterwards.

Formidable ships nothing for this, deliberately. Where the choice is kept, under what key, and what
a stale one should do are the app's decisions rather than a validation library's. The plumbing is
short, and the sample writes it in the open: `Program.cs` hands a reader to
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

A missing, blank or unrecognisable value applies the fallback instead. Pass no fallback and the app
keeps the culture it booted with, so a corrupted stored value cannot stop it starting.

The reader is a delegate rather than a storage call, which is what keeps the choice of storage the
app's: the sample reads `localStorage` under `formidable.culture`, and a cookie, a user profile
fetched from the server or a canned test value all fit the same shape.

Once the culture is set, FluentValidation's own message translations follow
`CultureInfo.CurrentUICulture` with no further wiring. See [Profiles](profiles.md) for the
localization story and the display-name half of it.

This is a WebAssembly concern only. A Blazor Server host sets culture from the request instead, and
needs no boot-time step at all.

**Sample:** [`/localization`](../samples/Formidable.Sample/Pages/Localization.razor) — stores the
choice and reloads, which is what makes the boot-time read the only place the culture can change.

## Virtualize and `KeepRegistered`

A `Virtualize` container disposes rows that scroll out of view even though they remain part of the
form. Without `KeepRegistered`, a scrolled-away row's field unregisters and its already-showing
error goes quiet while the row still sits in the model. Four types call the registry, and each
takes the parameter: `FormidableInputBase<TValue>` and so every input built on it,
`FormidableFieldAnchor`, `FormidableField` and `FormidableCollectionMessage`.

The sample pairs it with [`FocusFallback`](#focusfallback) on both the form and the summary, so a
row far outside the render window is kept disclosed and stays reachable by a click:

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

The full page wraps this in a `TeachingPanel`; the form markup itself is unchanged from what is
shown here.

The sample also sets a [`DisclosureOverride`](options.md#disclosureoverride) for the collection, so
even rows `Virtualize` has never rendered keep their place in the summary. Validation always runs
against the full model; the override only lifts the visibility gate.

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

A summary entry for a row inside the current render window focuses it directly. For a row scrolled
far away, the miss triggers `ScrollToRowAsync`, which scrolls `.scroll-panel` to the row's
approximate offset (`index * RowHeight`) and waits 120 ms for `Virtualize` to render it before
returning `true`. The caller retries the focus, and its own `scrollIntoView` centres the row
exactly.

A blocked submit reaches the identical miss on the same terms, so submitting with the first error
on an unrendered row needs no summary click at all to land there.

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

The native `InputText` gets the same state classes a Formidable input would, with nothing wired for
it. The engine installs a Formidable-aware provider on the `EditContext` at construction, and every
`InputBase` descendant in the form picks it up. [CSS and
accessibility](css-and-accessibility.md#the-fieldcssclassprovider-bridge) has which classes that
provider applies, and why both paths reach them through one `FormidableCss.Compute` call rather
than two that agree.

Three additions finish the crossing, one per concern, because a native input has no field context
to take any of them from:

| The addition | What it buys |
|---|---|
| `FormidableFieldAnchor` beside the control | Submit-channel disclosure. A plain `InputBase` never registers itself with Formidable's `FieldRegistry`, so without the anchor no submit reveals `Nickname` and its submit errors are suppressed as unrevealed. Opting into [`LiveIssueDisclosure.EngagedAndVisible`](options.md#livedisclosure) puts the live channel behind the same registration, which is the other thing the anchor buys. |
| The field's own id | Focus. A Formidable input renders `FormidableFieldId.For(field)` as its element id; here `NicknameId` computes it. Looking the target up is the id's whole job, and an `<input>` takes focus without further help, so the native field takes a summary's click exactly like a wrapped one. |
| `aria-invalid` and `aria-describedby` | The assistive-technology half. `GetFieldState(field).HasErrors` answers the first, and `EditContext.GetValidationMessages(field)` decides whether the second names the `-messages` id at all — a `null` value renders no attribute, and the native `ValidationMessage` renders no element to be described by while the field is clean. |

The live error lands either way. The first committed change engages the field, and an engaged
field's verdict reaches the store — and so that `ValidationMessage` — whether or not anything
registered it.

Naming `aria-invalid` in the markup also settles who answers for it, which is its own question.
[CSS and accessibility](css-and-accessibility.md#aria-invalid-and-aria-describedby) has what a
Blazor `InputText` does with a named attribute and with an absent one.

Attributes derived from engine state need the page to re-render when that state changes. So the
sample subscribes to `Engine.StateChanged`, the same subscription the kit's own inputs make for
themselves.

**Samples:** [`/foreign`](../samples/Formidable.Sample/Pages/ForeignControl.razor),
[`/virtualized`](../samples/Formidable.Sample/Pages/Virtualized.razor),
[`/vanilla`](../samples/Formidable.Sample/Pages/VanillaInterop.razor), and
[`/bootstrap`](../samples/Formidable.Sample/Pages/BootstrapFitting.razor) for the CSS merge
(consumer `class` kept, computed state classes remapped onto a UI library's own).
