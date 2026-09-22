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

That's the contract. What follows is every property, what it defaults to, and — for the ones a page
can show — where the sample demonstrates it.

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
has happened, before the debounced refresh re-answers `SubmitProfile` to keep inline errors
current. The refresh arms at this duration whether or not `LiveDebounce` is set.

### `LiveDebounce`

`TimeSpan?`, defaults to `null` — a live pass runs immediately on every field change. Set it and a
field change arms a single timer instead: another change inside the window re-arms that timer
rather than starting a second pass, and when the window elapses quietly one pass runs, scoped to
every field the window collected. The window is shared across fields rather than tracked per field,
the same shape `RefreshDebounce` already has.

Reach for it when live rules are expensive enough that one per keystroke is the wrong trade — an
async availability check being the obvious case. It changes how often live passes run — and, when
`TrackFormValidity` is also on, how often its validity probe runs too, since the probe rides this
same window rather than firing on a schedule of its own (see below). After a submit, the same edit
that arms this window also arms the post-submit refresh.

The two arm independently: the refresh at plain `RefreshDebounce`, this window at its own width, and
neither timer reads the other. At the default, with no live debounce at all, the live pass runs on
the edit itself and the refresh follows it 300 ms later. Set this above `RefreshDebounce` (400 ms
against the 300 ms default, as [`/async`](../samples/Formidable.Sample/Pages/AsyncRules.razor)
does) and the refresh comes due first instead. The order is free to vary because the cost is not:
verdict reuse is keyed by rule and edit stamp rather than by which pass ran first, so whichever
pass lands first executes the stale rules and the other serves the stored verdicts. What each pass
files differs — a live pass files the engaged fields' verdicts, a refresh files the whole model's
submit-profile answer — and what a field shows is read from both. With the refresh in front, what
submit disclosed updates a beat before the field's own live message does, a transient reordering
that leaves the settled state identical.

Same verdicts, same per-rule cost, in either order. The reuse a post-submit edit gets without
`LiveDebounce` set applies here unchanged — see
[Async validation](async-validation.md#the-refresh-runs-only-what-the-live-pass-did-not) for the
mechanism.

**Sample:** [`/async`](../samples/Formidable.Sample/Pages/AsyncRules.razor) — a checkbox swaps
between the immediate default and a 400 ms window, with the "checking…" indicator showing the
difference.

### `TrackFormValidity`

`bool`, defaults to `false`. Turns on the whole-form validity probe behind
`IFormValidationEngine.IsFormValid` — the answer a disabled Submit button needs.

Opt-in, and off by default for a reason: a form with nothing reading `IsFormValid` gets nothing
for the work. What the work costs depends on the validator. One the engine can take rule by rule
(an `AbstractValidator<TModel>` whose `ClassLevelCascadeMode` is `Continue`, FluentValidation's
own default; see
[Async validation](async-validation.md#the-refresh-runs-only-what-the-live-pass-did-not))
shares the engine's verdict store with the probe: the probe executes only the submit-selected
rules with no fresh verdict when it starts, files what it ran, and the passes that plan after it
serve those verdicts instead of running the same rules again. The probe and the passes beside it
end up paying for one execution per rule per model state rather than one each, and where every
selected rule is already answered — a refresh that landed earlier in the same debounce window, say
— the probe executes nothing at all and `IsFormValid` is a read of the store. What decides the
sharing is what has landed, not what is running, and a pass files its whole plan in one act at the
end: a pass still awaiting an async rule has filed nothing yet, so a probe starting in the
meantime plans those same rules and both executions are paid. Where nothing yields, which of the
two starts first does not matter: an all-synchronous plan runs to completion before the call that
started it returns, so whichever goes first has already filed everything the other would have
planned, and they share in full. On any other
validator there is no store to share, and each probe is one whole `SubmitProfile` validation on
top of the live pass — usually the more expensive of the two,
since `SubmitProfile` is by default a superset of `LiveProfile`: the default rules again plus the
whole Submit ruleset, where the expensive async rules tend to live.

What the probe is not is an engine pass: no disclosure, no message-store write, no pending
indicator, nothing about it ever reaches the screen. It runs once when the engine is built, so a
pristine form answers truthfully before anyone has typed, and again on every field change
afterwards at whatever cadence the live pass runs at — per change, or once per window when
`LiveDebounce` is set too. With tracking off, `IsFormValid` always reads `false`; with it on, it
reads `false` until that first probe completes. The answer is client-side only: issues a server
applied through `ApplyServerIssues` are not part of it.

The verdicts a probe files are read by more than `IsFormValid`. They are also what lets a field
wear the `Valid` class before any submit, since green asks whether the submit-selected rules would
pass (see [CSS and accessibility](css-and-accessibility.md#need-to-know)). On a page whose
`LiveProfile` doesn't already cover the submit profile, turning tracking on is what gets a clean
field confirmed while the visitor types, rather than at the first submit.

A probe in flight doesn't cancel one already running from an earlier change — they overlap. Which
answer sticks is decided by the stamp each probe took as it started, not by the order they finish
in: a probe a newer one has superseded discards its own answer instead of overwriting the fresher
one, and the verdicts it executed are filed only if no edit arrived while it ran. That leaves cost
as the concern rather than correctness, and it bites when `SubmitProfile` carries a slow async
rule: without `LiveDebounce`, ten keystrokes can mean ten probes in flight together, each
answering for the model state it started at.

```razor
<button type="submit" disabled="@(_form?.Engine?.IsFormValid != true)">Submit</button>
```

**Sample:** [`/field-state`](../samples/Formidable.Sample/Pages/FieldStateVisualizer.razor) — a
live "Form valid" readout above a Submit button that stays disabled until the probe says yes.

### `NormalizeOnSubmit`

`bool`, defaults to `false`. When `true` and the model implements `INormalizableModel`, the submit
pass calls `model.Normalize()` in place before running `SubmitProfile`, so the profile judges the
cleaned values rather than whatever was typed. That mirrors the ASP.NET Core validation filters,
which have always normalized a request body before validating it — this is the client-side half,
and it is the only automatic client-side invocation there is (page code is otherwise free to call
`model.Normalize()` itself, which is what several samples do).

Calling it yourself outside the submit path takes one more step this option does for free: the
mutation changes the model directly, and the engine starts a live pass only when it hears
`EditContext.NotifyFieldChanged`. Call that per field the mutation actually changed (the
`/normalize` sample's own "Normalize now" button does exactly this). Strictly, the live pass
any one call starts re-judges every field a committed change has ever engaged, so an
already-engaged field's message follows along however the pass was triggered — naming each
mutated field is what engages the ones nothing has engaged yet, and it costs nothing. Skip the
calls entirely and every message on screen keeps judging the stale values until the next edit
or submit.

Because the mutation happens before the pass, the submit's own re-render repaints every bound input
straight from the normalized model: a value that normalization trimmed, cleared or collapsed
visibly updates on screen with no extra wiring.

**Sample:** [`/normalize`](../samples/Formidable.Sample/Pages/Normalize.razor) — a checkbox, and a
plain Submit button that never calls `Normalize()` itself, so the option is the only thing that can
clean the model.

### `DisclosureOverride`

`Func<ValidationIssue, bool?>?`, defaults to `null`. A tri-state override consulted per issue:
return `true` to force it visible, `false` to force it suppressed, or `null` to defer to the
field registry (whether a rendered field claimed that path). Model-level issues — an empty
`Path` — are always visible unless the override returns `false`.

At submit the answer decides whether that issue puts its field under watch, and watching is per
field: an issue forced suppressed still shows once another issue on the same field is disclosed.
See [Disclosure](disclosure.md#disclosureoverride-the-escape-hatch) for that boundary and for what
the override does to a server-applied issue.

### `LiveDisclosure`

`LiveIssueDisclosure`, defaults to `LiveIssueDisclosure.Engaged`. Which of an engaged field's
live issues the live channel discloses — the one lever over a channel registration otherwise
never touches.

Under the default, engagement alone discloses. A field the user has committed a change to shows
its live verdict on every surface — the engine's issue reads, `FormidableFieldMessage`,
`FormidableSummary`, and the `EditContext`'s message store — whether or not anything currently
renders that field. That is the contract native components depend on: a page of plain `InputBase`
inputs with no Formidable wrappers or anchors registers nothing at all, so a live error that
deferred to registration would have nowhere to go (see [Server
integration](server-integration.md#client-round-trip)). Departure is the one thing a registration
check decides here: a field something rendered once and nothing renders now has left the page, and
it leaves the engaged set with it. A field nothing has ever registered has not left. It never
arrived, so its live verdict survives any amount of registering and unregistering elsewhere.

`LiveIssueDisclosure.EngagedAndVisible` gates each live issue on the same override-aware
visibility the submit channel consults: `DisclosureOverride` first, the field's rendered
registration otherwise. It applies uniformly, message store included, so the kit and a native
`ValidationMessage` can never disagree about what a field is saying. Opting in means what it says
on a page that notifies changes for fields it does not currently render: those fields' live errors
are hidden everywhere until they render. An override returning `true` still forces one visible.

Reach for it when a page deliberately notifies unrendered fields and would rather they stayed
quiet until they appear. To list less in one place without changing what is disclosed anywhere,
use [`FormidableSummary`'s `Show`](component-kit.md#showing-one-severity-band) filter instead.

Read at every evaluation rather than once per pass, so a page that flips it mid-session gets the
new answer from the next read on — as long as something re-renders, since changing an option
notifies nothing by itself.

### `SuppressedIssueDiagnostic`

`Action<ValidationIssue>?`, defaults to `null`. Invoked once per error-severity issue that
submit suppresses because no rendered field registration matches it and `DisclosureOverride`
didn't force it visible. A `Trace`-output warning is written for every suppression regardless of
whether this callback is set — the callback is for surfacing suppressions in your own UI or
telemetry, not the only place they get recorded. When the host resolved an `ILoggerFactory`
(both Blazor components do so automatically when one is registered), the same suppression also
logs a `LogWarning` — WASM's default logging provider is the browser console, so this is the
channel that needs no consumer wiring at all to be seen.

### `NeverRegisteredFieldDiagnostic`

`Action<ValidationIssue>?`, defaults to `null`. Invoked alongside `SuppressedIssueDiagnostic`, for
the narrower half of what it reports: a suppressed issue whose field has no registration history at
all — nothing has rendered it since the engine was built.

That is the signature of a rule whose `.When(...)` fails to mirror the `@if` gating its field, so
the rule can fail in a state the field never renders in. It is *also* the signature of a perfectly
correct section the visitor simply has not opened yet, and this signal cannot tell the two apart:
on a first submit neither field has ever been registered. Treat it as a place to look, not a
verdict. What it does rule out is the ambiguous middle — a field that was registered and later
unregistered, a visited-then-collapsed section, stays silent here and reports only to
`SuppressedIssueDiagnostic`.

### `VerifyRowKeys`

`bool`, defaults to `false`. A development-time check that a collection's rows carry a `@key`. When
`true`, every component bound to a field re-reads its accessor on each parameter set and compares
the field it now names against the one it registered, throwing an `InvalidOperationException` that
names the field and the fix when the two diverge with no teardown in between.

Unlike the properties [Need to know](#need-to-know) says the engine re-reads on every pass, this
one is captured once, per component, the moment it binds — flipping it on a `FormidableOptions`
instance already in use does nothing for a component already bound, only for one that binds
afterward. Treat it as a startup switch decided at app configuration time, not something a running
form's own page can toggle mid-session.

That divergence is what an unkeyed row list produces: remove or reorder a row and Blazor reuses
each row's components for the next item along, while the registration, element id, aria attributes
and messages stay with the row that moved away. Nothing about the misfiling shows on screen, which
is what earns it an exception rather than a diagnostic. Correctly keyed rows never trip it, whatever
the edit: replacing a row retires its key and builds fresh components for the replacement, removing
one disposes its components and builds nothing, and adding or reordering disposes nothing at all —
a keyed diff permutes the components it already has. In all three, anything newly built registers
the row it was handed and everything retained still resolves to the row it already spoke for. See
[Collections and row identity](collections-and-row-identity.md) for the `@key` habit itself.

Recommended in Development builds only, which the app-wide overload states in one place:

```csharp
builder.Services.AddFormidableBlazor(options =>
    options.VerifyRowKeys = builder.HostEnvironment.IsDevelopment());
```

It costs an accessor resolution per bound component per render, and a form that reaches production
with the mistake should misfile a message rather than take the page down.
[`/collections`](../samples/Formidable.Sample/Pages/Collections.razor) is the one exception in
this corpus: it leaves the check on unconditionally, since demonstrating the guard is the page's
own point.

### `InlineMessageRole`

`string?`, defaults to `null`, which renders no `role` attribute at all. Set it to `"status"` and
every field- and collection-level message list becomes its own polite live region, announced as its
content changes. The list element renders always, so the role sits on a container that persists
across renders rather than one that enters alongside its own text — the reliable shape for a live
region, since assistive technology is inconsistent about announcing a role that arrives together
with the content it describes. Recommended on forms that render no `FormidableSummary` — the
summary already announces on its own, and two live regions saying the same thing is worse than
one. See [CSS and accessibility](css-and-accessibility.md) for how the summary's own role is
chosen.

### `OrderIssues`

`Func<IReadOnlyList<FieldIdentifier>, IReadOnlyList<FieldIdentifier>>?`, defaults to `null`, which
reports visible issues in the document order of the fields that render them (see
[Component kit](component-kit.md#the-order-entries-appear-in)). Set it and the delegate becomes a
stage after that: `IFormidableFieldOrderService` answers where the fields are, and this answers
what order to report them in.

It receives the fields already in document order and hands them back re-sorted, so a form that
only wants one group ahead of another says that much and no more:

```csharp
_options.OrderIssues = fields => fields
    .OrderBy(field => field.FieldName switch
    {
        "" => 0,                 // the verdict about the form as a whole
        "Email" or "Phone" => 1,
        _ => 2,
    })
    .ToList();
```

`OrderBy` is stable, so everything the key does not separate keeps the document order it arrived
in: the delegate moves the contact fields up, keeps the form-level verdict ahead of them, and
leaves the rest of the form alone.

The model-level field is in that list too, with an empty `FieldName`, because it is resolved like
any other field and the shipped service always places it (its id sits on the form's own element).
It carries the all-suppressed gate's explanation and any validator fault, which is why document
order puts it first. A delegate keying on field names should say where that one goes rather than
let it fall into a default bucket behind the named ones — the empty-name arm above is that, and
without it the two contact fields would be reported ahead of the verdict about the whole form.

Exceptions are yours. Nothing catches one the delegate throws, and it runs inside a render, so a
delegate that throws takes the form down with it.

Re-sorting is all it can do. A field left out of the result is appended, in document order, rather
than dropped: an issue nobody reports is an issue the visitor cannot act on, and withholding one
is what disclosure is for, with its own diagnostic. A field named twice keeps only its first
position, and a field the delegate was never handed is ignored, so a short answer padded out to
the right length cannot push a real field out of the map either.

It is synchronous by design. Measuring the DOM has to be async, and async ordering already has a
home in the service; an async delegate here would duplicate that reach without adding to it.

It runs once per order resolution, at the same cadence as the service — not per render, and not
per `GetVisibleIssues()` call. Its answer is baked into the ordinal map the engine sorts by, so
reading it back is a lookup per issue rather than another run of the delegate. One consequence
follows from that cadence: the map is rebuilt when the set of registered fields changes, or when a
browser-side observer reports the form's existing elements moved around without any of them
registering or unregistering, so a sort criterion that moves on its own, a runtime "group the
blocking ones first" toggle for instance, is not picked up until one of those two happens.

### `CssClasses`

`FormidableCssClasses`, defaults to a new instance. Class names field components and native
`InputBase` descendants apply based on field state:

| Property | Applied when | Default |
|---|---|---|
| `Invalid` | the field has error-severity issues | `formidable-invalid` |
| `Warning` | touched or modified, no errors, and has a warning-severity issue | `formidable-warning` |
| `Info` | touched or modified, no errors or warnings, and has an info-severity issue | `formidable-info` |
| `Valid` | the field is touched or modified, has no issues at all, and the engine can say a submit would not fail it | `formidable-valid` |
| `Pending` | a validation pass involving the field is in flight | `formidable-pending` |

A field carrying only advisories earns `Warning`/`Info`, not `Valid` — deliberately, so it never
reads as cleared while it still has something to say. `Valid` asks for that third condition
because green is a promise about submit: a field whose submit-selected rules have no answer for
the value as it stands, or an answer that fails it without showing why, wears nothing rather than
a confirmation it has not earned. `Pending` appends alongside whichever of the other four applies
rather than replacing it — see [CSS and accessibility](css-and-accessibility.md) for how the five
compose, and `TrackFormValidity` above for what keeps that answer current before a submit.

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
`change`, arming a notification the next `blur` delivers — one delivery however many commits
accumulate before it, and none at all on a blur nothing was committed before. The split exists
for a native control whose `change` event fires more than once per logical edit (a date input,
segment by segment, is the clearest case), so the live pass a notification starts waits for the
value to actually settle instead of running on a value still being typed.

```razor
<FormidableInputDate @bind-Value="Model.EventDate"
                     UpdateOn="InputUpdateMode.OnBlur" />
```

`OnBlur` is the one mode that binds an event a page may already want for itself, so it chains
rather than claims: an input carrying its own splatted `@onblur` runs that handler first, awaits
it, and only then delivers whatever notification a commit left pending. A field that marks
itself touched on blur keeps doing so after the mode is switched on.

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

- `LiveProfile` / `SubmitProfile` — [Profiles](profiles.md) and the
  [`/profiles`](../samples/Formidable.Sample/Pages/Profiles.razor) sample (which relies on the
  defaults rather than overriding them);
  [`/custom-profiles`](../samples/Formidable.Sample/Pages/CustomProfiles.razor) points
  `SubmitProfile` at a profile of its own.
- `LiveDebounce` — [`/async`](../samples/Formidable.Sample/Pages/AsyncRules.razor), toggled against
  the immediate default.
- `TrackFormValidity` (and `IsFormValid` with it) —
  [`/field-state`](../samples/Formidable.Sample/Pages/FieldStateVisualizer.razor), driving a
  disabled Submit button.
- `NormalizeOnSubmit` — [`/normalize`](../samples/Formidable.Sample/Pages/Normalize.razor), beside
  the two buttons that call `Normalize()` by hand.
- `SuppressedIssueDiagnostic` and `DisclosureOverride` —
  [`/disclosure`](../samples/Formidable.Sample/Pages/Disclosure.razor) and
  [Disclosure](disclosure.md).
- `CssClasses` — [CSS and accessibility](css-and-accessibility.md); remapped onto a UI
  library's own classes ([`/bootstrap`](../samples/Formidable.Sample/Pages/BootstrapFitting.razor))
  and recoloured live via CSS custom properties
  ([`/css-colours`](../samples/Formidable.Sample/Pages/CssColours.razor)).
- `VerifyRowKeys` — [`/collections`](../samples/Formidable.Sample/Pages/Collections.razor), on
  unconditionally rather than gated to Development, since the page's whole point is the row-key
  discipline the guard enforces.

Three have no sample page, deliberately. `NeverRegisteredFieldDiagnostic`
reports into your telemetry rather than onto the screen; `InlineMessageRole` changes only what a
screen reader announces, which a page cannot demonstrate visually; `OrderIssues` re-sorts a
reading order every sample page is already content with, since each lays its fields out top to
bottom. Each one's entry above is its worked example. For the case where the layout itself is what
makes document order wrong, and the delegate cannot help because it cannot measure, see
[Recipes](recipes.md#i-want-the-summary-ordered-by-where-fields-appear-on-screen).

Two components carry behaviour this page's options don't reach:
[`/scroll-focus`](../samples/Formidable.Sample/Pages/ScrollFocus.razor) toggles
`FormidableForm.FocusFirstErrorOnInvalidSubmit`, and
[`/profiles`](../samples/Formidable.Sample/Pages/Profiles.razor)'s Reset button calls
`ResetAsync()` — both are parameters and verbs on the component rather than engine settings, so
they live in [Component kit](component-kit.md#formidableformtmodel).

**Sample:** [`/disclosure`](../samples/Formidable.Sample/Pages/Disclosure.razor) — the page this
one quotes for the shape of a well-behaved `Options` field.
