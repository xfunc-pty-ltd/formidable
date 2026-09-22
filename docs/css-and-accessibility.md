# CSS and accessibility

**You should already know:** how a Formidable form wires up end to end
([Quickstart](quickstart.md)).

Formidable ships no CSS and no visual opinion. What it ships is a small surface of class names and
ARIA attributes, and every field exposes it the same way: a kit input, a renderless
`FormidableField` template, and a plain `InputBase` beside them all answer alike.

This page catalogs that surface — the five configurable state classes, the fixed structural names,
the bridge that classes native inputs, and the ids, ARIA and focus wiring built on the same field
state.

## Need to know

One static rule computes a field's state class, shared by every Formidable component that computes
one. `FormidableCss.Compute` reads a `FieldState` and returns a space-joined class string. The
five names it applies are [`FormidableOptions.CssClasses`](options.md#cssclasses), each
configurable and each with a default.

The rule is an order rather than a set. The first of the five tiers that matches decides the
class, and pending is decided separately, on top of whatever that left:

| In order | When | Class |
|---|---|---|
| 1 | The field has error-severity issues | `Invalid` (`formidable-invalid`), gated by nothing else: a field the user must still fix never reads as merely advisory |
| 2 | The field is neither touched nor modified | No class at all, whatever it carries |
| 3 | It has a warning-severity issue | `Warning` (`formidable-warning`) |
| 4 | Its only issues are info-severity | `Info` (`formidable-info`) |
| 5 | It has no issues left to show *and* would pass submit | `Valid` (`formidable-valid`) |
| Always | A pass involving the field is in flight | `Pending` (`formidable-pending`), appended to whichever tier applied, or standing alone where none did — it never replaces a tier's own class |

#### What puts green on a field

The `Valid` tier asks for two things, and both are deliberate. A field still carrying an advisory
is not `Valid`, because it still has something the user might act on. And green is a promise about
submit rather than a note that the visitor stopped by.

So it waits until every rule the submit profile selects has an answer for the value as it stands,
and until none of those answers faults the field. A failing answer the form has not disclosed yet
counts, which is what stops an emptied required box wearing a confirmation border.

Whichever pass answered those rules last is the one being read. On the default profiles that is an
ordinary live pass: [`LiveProfile`](options.md#liveprofile) tracks the submit profile, so the pass
behind any committed change lands the answer green is asking about, anywhere on the form.

The submit answers the same way, and so does the debounced refresh: the one behind every
post-submit edit, and the one behind any move in the rendered field set. That second refresh is
partly green's own.

A row arriving or a virtualized panel scrolling throws away the answers green was resting on,
because the markup may have moved together with the model and nothing announced the second half.
Rather than blink, the engine holds the answer it last gave and serves it until the pass that same
move arms produces a new one.

So markup moving is not on its own something green reacts to. A value moving is, for that one
field, and so is a model changed alongside the markup without saying so, once that refresh lands
and says which fields it fails.

An edit opens a second gap, narrower than the first. Each field edited since the held answer was
computed loses it the moment its change commits, since its value is the one that answer no longer
describes. Every other field keeps what it had for as long as a fresh answer is demonstrably on
its way:

| A fresh answer counts as on its way when | The exception |
|---|---|
| a pass is already validating | A narrowed live pass cannot answer the submit profile, so it counts only for the refresh armed behind it. |
| a live-debounce window is open on a live channel that runs the submit profile | A window on a narrowed channel promises a pass that cannot answer, so it counts for nothing. |
| a post-submit refresh is armed | Nothing narrows this one, since the refresh runs the submit profile itself. |

The live-debounce window and the armed refresh count only while their own debounce is finite. An
`InfiniteTimeSpan` spelling arms a timer that never fires, so a window that cannot close promises
nothing either.

One keystroke therefore does not blank every other field's confirmation border for the gap behind
it, which is the debounce window plus the rule's own flight.

The cover is not indefinite. A pass still running past thirty seconds loses it, and a pass that
ends without landing — a fault, a cancellation — drops the held answer outright rather than riding
out whatever cover remained.

A pass superseded by a newer one is not itself a drop. The answer then stands or falls on whether
the pass that displaced it, or something still armed, promises a fresh one. Either gap closes the
same way: the pass the hold was waiting on lands, and its answer is what green reads from there.

[`TrackFormValidity`](options.md#trackformvalidity)'s probe covers what is left: a form on which
nothing has happened at all, one that narrows `LiveProfile` past those rules, and a validator with
no rule-level seam, where a live pass is never taken as a coverage source. A page that calls
`DiscloseLoadedValuesAsync` answers the first of those itself, with no probe.

Until one of them has answered, a clean-looking field wears no tier class rather than a green one.

**Where the computed class is applied.** `FormidableInputBase<TValue>.CssClass`, and
`FormidableFieldContext.CssClass` for the renderless path, call `FormidableCss.Compute` with
whatever `CssClasses` instance the form's options currently hold, then merge the result with any
consumer-splatted `class`. See [Component kit](component-kit.md) for the merge itself.

The message components and `FormidableSummary` use a related, fixed convention of their own for
the messages they render. See [Severity](severity.md) for the `formidable-message--{severity}` and
`formidable-summary__group--{severity}` class families, and
[Component kit](component-kit.md#heading-each-band) for the `formidable-summary__band--{severity}`
band each group sits inside.

That is the entire class-name contract: rename the five strings, and the order above still decides
when each one applies.

## Configuring `FormidableCssClasses`

`FormidableCssClasses` is a plain settings object: a mutable class with one `string` property per
name in the tier table under [Need to know](#need-to-know). Construct one, set whichever names
should match your own stylesheet or design system, and assign it to `FormidableOptions.CssClasses`
(see [Options](options.md)).
Formidable does not care what the strings are, only when each one applies.

`CssClasses` is read at each class computation, by kit components and by the provider that classes
native `InputBase` components alike. So renaming a class reaches both surfaces from the next
computation each makes, whether you set properties on the instance you already hold or assign a
whole new `FormidableCssClasses`.

The `FormidableOptions` object around it is the thing that cannot be swapped. Handing the form a
different instance on a later render throws rather than quietly changing nothing. See
[Options](options.md#formidableoptions-is-read-once) for that rule.

## The structural class inventory

The five configurable names cover what the engine computes onto elements *you* render. Every class
on an element the library itself renders is fixed. These names are contract, published here so a
stylesheet can key on any of them and know it is keying on something that will not change. This is
all of them — twenty-two names:

| Class | Where it renders |
|---|---|
| `formidable-message-list` | the persistent message list (`<ul>`) all three message components render — `FormidableFieldMessage`, `FormidableCollectionMessage`, `FormidableModelMessage` |
| `formidable-message` | every message item (`<li>`) in those lists |
| `formidable-message--error`, `formidable-message--warning`, `formidable-message--info` | appended to the item for its severity |
| `formidable-required` | the marker `<span>` `FormidableRequiredIndicator` renders |
| `formidable-summary` | `FormidableSummary`'s persistent wrapper |
| `formidable-summary__region` | every fixed-role live region the wrapper holds — which ones, `Show` decides |
| `formidable-summary__region--errors` | appended to the `role="alert"` region |
| `formidable-summary__region--advisories` | appended to the `role="status"` region |
| `formidable-summary__band` | one band per non-empty severity group |
| `formidable-summary__band--error`, `--warning`, `--info` | appended to the band for its severity |
| `formidable-summary__group` | the band's message list (`<ul>`) |
| `formidable-summary__group--error`, `--warning`, `--info` | appended to the group for its severity |
| `formidable-summary__heading` | the optional heading above a band's list |
| `formidable-summary__item` | each summary entry (`<li>`) |
| `formidable-summary__link` | the entry's click-to-focus button |
| `formidable-summary__overflow` | the list item holding what an `OverflowTemplate` renders for the entries `MaxItems` held back |

These names are deliberately not configurable, and that is a ruling rather than a gap. A
`FormidableCssClasses`-style seam for the structural names is additive later, and it could arrive
without breaking anything, while the defaults above are what consumer stylesheets key on
regardless. The names stay fixed vocabulary either way.

What exists today is the splat. The message lists and the summary wrapper accept unmatched
attributes, so utility classes and data hooks reach the containers, with a splatted `class`
merging ahead of the structural name. The fixed-role regions and everything inside them, the
message items, and the required marker take no splat; they are what the table freezes.

### Cueing severity without colour

Severity is one thing a stylesheet should not say in colour alone, and an inline message item
gives it little to work with. Each item is its own text plus two classes: `formidable-message`,
and one of `--error`, `--warning` or `--info`.

That modifier is the only thing on the item saying which severity it is. Amber against red is then
the whole difference between "you have to fix this" and "worth knowing" for a reader who sees the
two as one colour.

`FormidableSummary` can put that difference into words. `ErrorsHeading`, `WarningsHeading` and
`InfosHeading` label each band in your own wording, with no English default standing in for them.
An inline list has no such parameter, so its cue has to come from the stylesheet or from the
message text: a word ahead of the message, an icon with a text alternative, anything visible that
survives being read in one colour.

`formidable-valid` and `formidable-pending` are the same question with less to fall back on.
`formidable-invalid` at least pairs with the input's `aria-invalid` and with the message that
explains it, while the kit renders no text for a confirmed field or a checking one, and no
`aria-busy` anywhere. Severity, confirmed and checking each want a cue of their own; which cue, and
how it is worded, is yours.

## The `FieldCssClassProvider` bridge

A field rendered by a plain Blazor `InputBase` still needs a class that reflects its validation
state, and `InputBase` gets its class from the `EditContext`'s `FieldCssClassProvider` rather than
from anything Formidable's own components compute. So the engine installs
`FormidableFieldCssClassProvider` on the `EditContext` as it is built, and a native input picks up
the same configured class names with nothing wired for it.

This is the same class names as `FormidableCss.Compute`, and genuinely the same rule. The provider
builds its own `FieldState` — `IsModified` and `HasErrors` read straight off the `EditContext`,
and `IsTouched`, `IsValidating`, `HasWarnings`, `HasInfos` and `WouldPassSubmit` read from the
engine — then hands it to `FormidableCss.Compute`, the one place the
invalid/warning/info/valid/pending decision is made.

Concretely: a field the engine considers touched (`FieldState.IsTouched`, set by `MarkTouched()`)
reaches the `Valid` tier through this path on exactly the terms a Formidable input reaches it on,
`WouldPassSubmit` included, even before the `EditContext` has ever seen `NotifyFieldChanged` for
it. `FormidableCss.Compute`'s `IsTouched || IsModified` branch is the one decision both paths
share, not two decisions that happen to agree.

Two things about how it reads the engine:

| Detail | What it means |
|---|---|
| The engine-sourced reads go through one internal fast path | The provider probes for it with a single `as` check rather than a per-member one, and falls back to `GetFieldState(fieldIdentifier)` for all of them at once where an `IFormidableEngine` does not implement it (a test double, say). No in-between case exists. |
| A `FieldState` built without an engine defaults `WouldPassSubmit` to `true` | A hand-rolled provider or a test double keeps the `Valid` tier reachable rather than losing it to a bit it never set. |

A native input inside a Formidable form also shows the same "checking…" cue a Formidable input
does, automatically. See the Vanilla interop section of [Component kit](component-kit.md) for the
provider wired into a native `InputText` beside a Formidable one.

**Installation is the engine's job**, so a form never constructs a provider to get these classes.
The public constructor is for the case where an `EditContext` no longer has Formidable's provider
on it: `SetFieldCssClassProvider` holds exactly one, so a consumer's own call replaces it, and the
way back is `new FormidableFieldCssClassProvider(engine)` with the engine read from
`FormidableFormContext.Engine`. The same construction lets a consumer's own provider delegate to
Formidable's and append classes of its own.

It takes the engine and nothing else, so the names it applies are always the ones the form's kit
inputs apply. A provider that should answer with a different map is a provider of your own, built
out of the same two public pieces this one uses: `FormidableCss.Compute` over
`IFormidableEngine.GetFieldState`.

One lifetime note for `FormidableValidator`, which attaches to an `EditContext` it does not own.
Disposing the validator leaves Formidable's provider installed on that `EditContext`, still
pointing at the disposed engine, so a page that keeps using the `EditContext` afterwards should
install whichever provider it wants for that next life.

## Accessibility wiring

### Deterministic ids

Every id Formidable assigns comes from one function, `FormidableFieldId.For`, keyed by the owning
object instance and the field name together. An input's `id`, a message list's `id` and the target
the focus service looks for are all derived there.

The shape is `formidable-{owner-hash}-{name-hash}-{sanitized-name}`, and the name reaches it twice
because each trip does a different job.

Sanitizing lowercases letters and digits and turns everything else into `-`, which is what makes
an id legible and selectable: `Url`, `URL` and `url` all end `-url`, so a stylesheet or a browser
locator can say `[id$='-url']` and mean it.

That legibility is also a collision, since those are three different fields. Two elements sharing
a DOM id is invalid HTML, and it misdirects everything keyed by the id (the sites are below). The
hash of the original name, case and punctuation intact, is what separates them again.

**The name hash sits in front of the sanitized name rather than after it.** The owner segment is
an object-identity hash, opaque rather than addressable: it names the instance, so a different
model or a different collection row gives a different one, all but certainly — the exception is
below.

Its value is drawn from a sequence the runtime advances on each first identity-hash request, so
anything the app asks about earlier moves it along. Nothing you write can name it. The suffix is
what stays put, and putting the name anywhere but last would take that away.

The name hash itself is FNV-1a, spelled out in the library rather than taken from
`string.GetHashCode()`. That one, unlike the identity hash beside it, really is randomized per
process, so an id computed in your test run would not match the one the app renders.

Thirty-two bits over the names one object owns makes two ids overwhelmingly likely to differ
rather than certain to. The sanitizer collision it replaces was certain.

#### When two fields draw the same id

An id's owner segment prints in the same eight hex digits as the name hash, and is narrower than
it looks. The runtime keeps an object's identity hash in part of the object's header rather than
in a full `int`: twenty-six bits of it on CoreCLR, the runtime under Blazor Server and every
server-side render.

So among enough owner objects rendered at once, two can draw the same value, and their same-named
fields then render the same id.

The odds climb with the square of the count:

| Owner objects rendered at once | Chance two share a value |
|---|---|
| the hundreds of rows a form usually shows | negligible |
| a thousand | under one percent |
| five thousand | about one in six |

Validation is untouched, because the engine tells fields apart by the owner reference and never by
this hash. What a duplicate id disturbs is whatever is keyed by the id:

| Site | What a duplicate does to it |
|---|---|
| Focus | A summary click or a blocked submit's focus lands on the first of the two in the document, so it reaches the first row. |
| `aria-describedby` | The second row's value names the first row's message list. |
| The DOM value sync on blur | It writes into the first row's box. |
| The field-order service | A shared id can name only one field. The one the registry lists later keeps it and takes the first row's place in the resolved order; the other never enters that order, so its issues sort after every placed field's. |

Virtualizing a form that large keeps the rendered set to the rows in view, which is the count that
matters.

**The model-level field gets `form` as its name segment** rather than an empty one. That is the
field with an empty `FieldIdentifier.FieldName`, the one the defensive all-suppressed gate in
[Disclosure](disclosure.md) targets.

`FormidableForm` renders that id on its own `<form>` element (see
[Component kit](component-kit.md)), so a page only reaches for `FormidableFieldId` by hand for a
field that owns no input of its own: a collection container, or the `<form>` element in attach
mode.

An overload of `For` takes a member-access expression instead of a `FieldIdentifier` for that
case — `FormidableFieldId.For(order, o => o.Description)` — so the id can be computed without a
`nameof` step to keep in sync with the property it names. It evaluates the object part the way
`FieldIdentifier.Create` evaluates its own, so `o => o.Address.City` names the field the `Address`
instance owns, which is the one a component renders.

There is no expression shape for the model-level field itself. Construct
`new FieldIdentifier(model, string.Empty)` and pass it to the `FieldIdentifier` overload, as
`FormidableForm` does internally.

**The owner segment separates instances, not roots.** Two roots over one model instance — two
`FormidableForm`s, or a `FormidableForm` and a `FormidableValidator` — compute identical ids for
every field they both render, which duplicates ids the same way a sanitizer collision would.

Two `FormidableForm`s duplicate the `<form>` element's own id whatever else they render, since
each puts the model-level field's id there. Bind each root to its own model instance.

**The message list's id is the same id with `-messages` appended**, and
`FormidableFieldId.MessagesFor` is the one place that appends it: the `aria-describedby` contract
has an owner rather than a convention. Call it when wiring a control by hand. The kit's inputs,
the message components and `FormidableFieldContext.AriaDescribedBy` all get their string from it.

### `aria-invalid` and `aria-describedby`

`FormidableInputBase<TValue>.AddCommonAttributes` renders three ARIA attributes, each from a
source of its own: `aria-invalid` from the same field state the CSS class rule reads,
`aria-describedby` from the field's issues, and `aria-required` from what the submit profile's
rules demand.

| Attribute | Renders while | Value |
|---|---|---|
| `aria-invalid="true"` | the field has an error-severity issue | Fixed. Warnings and infos do not raise it. |
| `aria-describedby` | the field has an issue of any severity, warnings and infos included, since those are still rendered and still worth announcing | The field's messages id, merged behind anything the consumer splatted. |
| `aria-required="true"` | `IFormidableEngine.GetFieldRequirement` reports `FieldRequirement.Required` | Fixed, and untouched by any validation pass. |

The `aria-describedby` merge puts a persistent hint's own value first and appends the messages id
after it while issues exist. That is the same consumer-first policy as the `class` merge, so an
error joining the field extends the announced sequence instead of replacing the hint.

With nothing splatted, the value is the messages id alone: `FormidableFieldId.MessagesFor(field)`,
the field's id plus `-messages`. That is exactly the id a field's own message list carries,
whether `FormidableFieldMessage` or `FormidableCollectionMessage` rendered it, and a splatted `id`
on that list is ignored rather than honoured — this one is the contract every `aria-describedby`
on the page relies on. [Component kit](component-kit.md#formidablefieldmessagetvalue) has the
three positions the list takes against the splat.

`FormidableFieldContext`, the renderless path's equivalent, computes the identical pair from the
same inputs and reports the field's requirement beside them. A hand-rolled control driven by
`FormidableField` gets the same wiring a `FormidableInputBase` descendant does.

One deliberate difference: `AriaDescribedBy` there is always the single messages id, never a
merged list, because the consumer composes the markup themselves. A control that also carries a
hint writes the hint's id and `field.AriaDescribedBy` into the attribute in that order, by hand.

**`aria-required` follows a different question from the other two.** `aria-invalid` and
`aria-describedby` describe what the field's values are currently doing. `aria-required` describes
what the submit profile's rules demand of it, so no validation pass touches it.

That is why it is asked separately from the state and the issues the rest of
`AddCommonAttributes` works from. The derived answer is reused, so asking per field per render is
a lookup. [`RequiredOverride`](options.md#requiredoverride) is the part that can change on its
own, so it alone is invoked on every ask rather than cached with the rest.

**It is `aria-required` rather than the native `required` attribute, deliberately.** `required`
makes every empty required field match `:invalid` from first paint, which is the opposite of the
untouched-fields-stay-quiet discipline the state classes above follow. `novalidate` does not
reach that: it switches off interactive validation rather than the constraint computation.

It also arms the browser's own submit-time enforcement. `FormidableForm`'s default `novalidate`
(see [Component kit](component-kit.md#formidableformtmodel)) keeps that from firing.

A form without it is the case to know: attach mode's consumer-owned `EditForm`, or a splat that
removed the default. There the browser refuses the submit before Formidable's pass ever runs, and
puts its own bubble in front of the message the form was going to show, in the browser's wording
and placement.

`aria-required` states the same fact to assistive technology and leaves the verdict where the rest
of the form's verdicts live.

**The visible mark and the announced fact are deliberately separate elements.**
[`FormidableRequiredIndicator`](component-kit.md#formidablerequiredindicatortvalue) draws the mark
as `<span class="formidable-required" aria-hidden="true">`, and the input beside it carries
`aria-required="true"`.

Splitting them is what stops a screen reader announcing the field as "Title star". An
`aria-hidden` subtree is excluded from the accessible name computed from the label around it, so
the mark can sit inside the `<label>`, where sighted readers expect it, without changing the
input's name by a character.

Style the mark through `formidable-required`; the library ships no styling.
[`RequiredIndicatorContent`](options.md#requiredindicatorcontent) supplies the text inside it, and
an empty string keeps the empty element for a glyph drawn with `::before`. Turning
[`ShowRequiredIndicators`](options.md#showrequiredindicators) off removes the mark and leaves
`aria-required` in place, because whether a value is demanded is a fact about the input rather
than a decoration.

#### An input the page renders itself

An input the page renders itself owes `aria-required` and `aria-describedby` by hand, since
neither arrives from a field context it never had. `GetFieldRequirement(field)` answers the first;
the second carries a condition of its own, below.

**`aria-invalid` is the one a Blazor `InputText` sees to itself**, and it answers from the field's
messages rather than from anything the markup says. It recomputes every time its parameters are
set and every time validation state changes, and each time it reads the splat it is holding right
then rather than the markup:

| What the component is holding | With messages on the field | With none |
|---|---|---|
| The `aria-invalid` key, at any value | It leaves the held value alone: a `"false"` stands, and so does a `null`, which the Razor compiler still counts as naming the attribute and which renders nothing. | It removes the key and writes nothing, whatever the markup says. |
| No such key | It writes `aria-invalid="true"`. | It writes nothing. |

The removal is what puts render order in charge, since it empties the component's own copy rather
than the markup: the page's value returns at the next render of the page around the input, and not
before. Until that render, a page naming the attribute behaves exactly like one that does not, so
a validation-state change arriving on its own puts `"true"` on the input over whatever the page
asked for.

An edit is not the case to watch, because its change event runs through the page and re-renders
it. Formidable's other passes do not: a debounced live pass, an async rule landing, the
post-submit refresh and a background `ApplyServerIssues` all move validation state with no render
of the page in the loop.

What supplies one is a subscription a hand-rendering page already wants. `Engine.StateChanged`
keeps an engine-derived attribute fresh, and the render it triggers is also what puts the page's
own `aria-invalid` back. Without it the framework's answer is the one that stands.

`/vanilla` and `/workout` name the attribute and answer `"true"` or nothing from
`GetFieldState(field).HasErrors`, which is the same pair of answers the framework writes, so the
ordering cannot put a third thing on the input. Both take out that subscription. `/attach` names
nothing and takes the framework's.

**`aria-describedby` carries a condition a kit input paired with a `FormidableFieldMessage` never
meets:** it has to name an element that is on the page. A native `ValidationMessage` renders one
`<div>` per message and nothing at all when there are none, where `FormidableFieldMessage` renders
its list empty and leaves it standing.

So a page pointing at a native message element renders the attribute only while that element
exists, and the question to ask is the one that component itself answers,
`EditContext.GetValidationMessages(field)`. Every sample pairing a hand-rendered input with a
native `ValidationMessage` does it that way: `/workout`'s venue input, `/attach`'s "Submitted by",
and `/vanilla`'s nickname.

#### A reference that resolves to nothing

The `aria-describedby` attribute renders whether or not its target actually exists. A kit input
points it at the field's messages id only while the field carries issues, and `FormidableForm`
points its `<form>` element's at the model-level messages id unconditionally, whether or not the
model has anything to say.

Either id resolves only where the matching component is actually placed: a field's
`FormidableFieldMessage` or `FormidableCollectionMessage`, or, for the form, a
`FormidableModelMessage`. Leave the component out and the attribute still renders, naming an id
nothing on the page carries.

That is not a bug to chase down. WAI-ARIA 1.2 §8.6.1 tells user agents to ignore a reference that
resolves to nothing, and ARIA 1.3 permits an author to leave one dangling too. That permission
rests on the general ground that a modern page's DOM can be populated when necessary, rather than
on naming this exact case.

The real cost sits elsewhere. A scanner cannot tell whether the reference was meant to resolve, so
axe-core reports it as `needs review` at `impact: critical`, once per run for each element whose
reference resolves to nothing, rather than as a violation. Add the matching component and the
finding disappears with it.

### `FormidableSummary` as a live region

A live region announces reliably only when the element carrying the role was in the DOM before the
content arrived. Assistive technology is inconsistent about a role that enters, or changes, in the
same render as the text it should announce — and the announcement that matters most, the first
blocked submit, is exactly the one that shape most plausibly drops.

`FormidableSummary` is built so that moment cannot depend on it. Its persistent wrapper holds
fixed-role region elements that render from the first paint and stand empty until there is
something to say:

| Region | Role | Holds |
|---|---|---|
| `formidable-summary__region--errors` | `alert` | The blocking half, announced assertively. |
| `formidable-summary__region--advisories` | `status` | Warnings and infos, announced politely. |

No role on any element ever changes, and every issue that arrives after a region's own first
render inserts into a live region whose role was already there. Each region is wrapped in its own
render-tree sequence region, so the element the role sits on is the same DOM node across renders
(see [Component kit](component-kit.md#formidablesummary) for that half of the wiring).

**`aria-atomic="false"` is the same argument one level down.** `status` and `alert` are atomic by
DEFAULT — each role carries an implicit `aria-atomic` of true — so a region left to itself
announces the whole of itself again on every change. Fix one field on a form blocked by five and
the visitor is read the four that remain, assertively, before they reach the next box.

Spelling the attribute out narrows each announcement to the entries that actually changed. A bare
`aria-live`, which is what [`InlineMessageLive`](options.md#inlinemessagelive) puts on a message
list, carries no such implication and needs no such correction.

The persistent element is what makes that possible in the first place. The entries come and go
inside a region that stays, so there is a difference between the region and its parts for
`aria-atomic` to be about.

**Entries carry a key for the same reason**, and it is a rendering decision with an accessibility
consequence. Blazor matches unkeyed siblings by position, so correcting the field the first entry
names would rewrite the text of every entry below it rather than removing that one.

To a screen reader on a non-atomic region, a band that churns wholesale is a band that announces
wholesale. Keying each entry by the issue it carries makes a removal read as a removal.

The component subscribes to the engine's `StateChanged` event itself, so a region's content stays
current through every kind of update rather than only the moment of submit: a submit that fails, a
live-typed correction that clears an error, a server-applied issue landing.

Blocking problems and commentary announce at different urgencies by construction, since errors
band into the `alert` region and warnings and infos into the `status` one. A follow-up edit that
clears the last error empties the assertive region rather than changing what any element is.

**The regions are per summary, not per form**, which matters as soon as a page splits the bands
with `Show` (see [Component kit](component-kit.md#showing-one-severity-band)). `Show` decides
which regions a summary renders, so an `Errors` summary carries only the `alert` region and an
`Advisories` one only the `status` region.

A submit that produces both kinds announces from both regions, the blocking half assertively and
the advisory half politely. That is equally true of one combined summary and a split pair, which
render the same two regions between them. What `All` saves is coordination, not announcements: one
summary cannot double-list an issue the way a default summary rendered beside a filtered one can.

Because `Show` is a parameter, changing it at runtime is where a region's persistence has its
boundary. A region added while matching issues are already on screen renders together with its
first band; only what arrives afterwards lands in a region the DOM already held. A `Show` fixed in
the markup, which is the usual case, never reaches that.

### Focus service

Every entry in `FormidableSummary` is a button that calls `IFormidableFocusService.FocusAsync`,
which locates and focuses the DOM element carrying a field's deterministic id:

```csharp
ValueTask<bool> FocusAsync(FieldIdentifier field);
```

<!-- Source: `src/Formidable.Blazor/IFormidableFocusService.cs` -->

The summary is not its only caller. `FormidableForm` moves focus through the same service on every
blocked submit, unless `FocusFirstErrorOnInvalidSubmit="false"` or the invalid-submit handler says
otherwise, and on every move a page asks for by calling `FocusFirstErrorAsync()` (see
[Component kit](component-kit.md#formidableformtmodel)).

It aims at the first error rather than the first visible issue, because issue order follows the
page and the topmost field may be carrying only a warning. A keyboard visitor whose submit was
refused should arrive at the thing that refused it, not at an advisory above it.

In the rare case where a submit blocks with no error on screen at all, it falls back to the first
visible issue, so focus still moves rather than being left wherever the submit button was.

**`FocusAsync` returns `true` when the element took focus and `false` when nothing did.** There
are two routes to `false`:

| Route | What it looks like |
|---|---|
| No element carries the field's id | A virtualized row outside the render window; a control that renders no such id at all. |
| The element carries it and will not take focus | Disabled, hidden, not a focusable kind of element, or sealed off by an ancestor: a closed `<details>`, an `inert` subtree, a native `<dialog>` open elsewhere on the page. |

Both reach the caller as one answer, because the visitor is in the same place on either. An
element merely covered by an overlay does take focus, so that shape answers `true`.

The shipped implementation is a thin JS-interop wrapper: it hands the JS side the field's
`FormidableFieldId` for the focus target and its `MessagesFor` id for the scroll target, and
returns whatever that side reports.

**Focus and scroll come apart there, deliberately.** Focus always lands on the field's own
element, because that is what a keyboard visitor has to be able to type into. The scroll prefers
the field's message list. So an issue with no input of its own — a collection-level rule, whose
element is the container holding every row — brings the message that named the problem into view
rather than the middle of the group.

Then the target's own height decides the alignment: taller than 60% of the viewport and it aligns
to its top, smaller and it centres. Centring is right for a field wrapper and wrong for a
container that fills the screen, whose centre is somewhere down among its rows.

**How it moves is yours.** The scroll asks for `behavior: "auto"`, which means each scrolling box
the move touches uses its own `scroll-behavior`. A page that says nothing gets an instant jump; a
page that asks for `scroll-behavior: smooth` gets an animation. A visitor who has asked their
system for less motion is answered by the same stylesheet, through a media query the library is in
no position to write:

```css
html {
    scroll-behavior: smooth;
}

@media (prefers-reduced-motion: reduce) {
    html {
        scroll-behavior: auto;
    }
}
```

Put it on the root element: `scroll-behavior` propagates to the viewport from `html` and, unlike
`overflow`, not from `body`. A scrollable container of your own — a panel holding a `Virtualize`,
say — is a second scrolling box and needs its own declaration, since a focus move inside one
scrolls the container as well as the page.

Write the guard rather than assuming it. Browsers differ on whether `scroll-behavior: smooth`
reads the preference by itself, and Chromium does not, so without the media query a visitor who
asked for less motion gets the animation anyway.

Watch what else the declaration catches, too. Assigning `scrollTop`, or calling `scrollTo` without
an explicit `behavior`, animates once the box is smooth, which is rarely what a programmatic jump
wants.

#### What a miss does

`document.getElementById(id)` locates the focus target and the scroll target alike, and a miss on
the focus target is reported rather than swallowed. The JS side answers
`document.activeElement === element`, which is what makes it report both routes to `false` rather
than only the first, and `FocusAsync` propagates that bool straight back to its caller.

Reading the answer back rather than assuming the call took is what makes the second route
recoverable at all. An element that is present and refuses focus would otherwise be reported as a
move that landed, and the seams below would never see it.

A miss on the scroll id alone is not an error: it falls back to the focus element itself, which is
always the size-aware scroll's minimum viable target.

What happens next diverges by caller. `FormidableSummary`'s click-to-focus handler no-ops silently
when `FocusAsync` reports a miss and no `FocusFallback` is set, or the fallback itself fails to
recover it.

The form's own moves read the identical miss and retry it through its own `FocusFallback`
parameter, the same name and delegate shape as the summary's and typically wired to the same
callback. With none wired they do not share the summary's silent no-op: a visitor sent to a field
nobody clicked has nowhere else to land, where a summary click simply has no effect, so the form
reports a diagnostic instead (see [Component kit](component-kit.md#focusfallback)).

The gap `FocusFallback` recovers, on either component, matters for three cases:

| The case | What it is |
|---|---|
| A field scrolled out of a `Virtualize` window | It has no current DOM element, so the focus misses — the case `FocusFallback` exists to recover (see [Component kit](component-kit.md#focusfallback)). |
| A raw or foreign control whose markup never rendered `field.ElementId` as its `id` | Which is why `FormidableField`'s `ForeignControl.razor` sample splats `@attributes="field.InputAttributes"` onto its `<select>`: one splat carrying the id, the state class, and `aria-invalid`, `aria-describedby` and `aria-required` whenever each applies (see [Component kit](component-kit.md)). |
| An element that carries the id and will not take focus | A disabled control, one inside a closed `<details>` or an `inert` subtree, or a container given the id without the `tabindex="-1"` that makes a `<div>` or a `<fieldset>` focusable at all. |

A `FormidableFieldAnchor`-only registration with no id on the control it anchors has nothing for
the focus service to find. [`/workout`](../samples/Formidable.Sample/Pages/Workout.razor) closes
that gap, giving its native `InputText` the field's id alongside the anchor;
[`/attach`](../samples/Formidable.Sample/Pages/AttachMode.razor) illustrates it instead, holding
the id back until a focus misses and supplying it from `FocusFallback` for the retry.

#### Focusing an element that is not an input

Because the field's deterministic id is the whole of the lookup, a field with no input of its own
can be focused just as well. Give any element the field's `FormidableFieldId.For(...)` id and a
`tabindex="-1"` so it can hold focus, and that element is where the summary entry lands.

`FormidableForm` does this for the model-level field itself, on the `<form>` element it renders
(see [Component kit](component-kit.md)), so the all-suppressed defensive gate's summary entry
lands there with no page wiring.

A collection's rules fail against the list rather than against any one control, so nothing renders
that id automatically. The `/collections` sample gives the container holding every row the
collection's id by hand.

A container shows nothing when it takes focus, so the sample stylesheet marks those landings with
an outline, scoped to `:focus-visible`. The scope matters because `tabindex="-1"` leaves an
element focusable by mouse, and a plain `:focus` rule would paint the whole container whenever a
click landed on its padding.

Activating a summary entry from the keyboard carries focus-visible through to the programmatic
focus, so the keyboard path keeps the mark while a mouse click gets `scrollIntoView` alone.

### Pointer activation and a button that moves

Focus is not the only thing a form has to keep steady under an input device. A click is a press
and a release, and the browser fires one only when both landed on the same element.

So a button that moves out from under a still pointer between the two produces no click at all,
and the visitor's submit is silently discarded. Disclosing a message above the button is exactly
what moves it, which makes this the ordinary case on a form rather than an exotic one.

Keyboard activation is immune, because focus follows the element rather than a coordinate: Tab to
the button, press Enter or Space, and the button is still the button whatever the layout does.

Pointer and touch are the whole of the gap, and Formidable closes it by default. A root installs a
guard that re-delivers the displaced click to the button it began on. `FormidableForm` renders the
element that guard scopes to, so it always has one; attach mode looks for one and reports it when
there is nothing to find.

See [Component kit](component-kit.md#the-click-a-disclosure-displaces) for the mechanism, the
conditions and the attach-mode diagnostic, and [`ClickRecovery`](options.md#clickrecovery) for the
opt-out.

## Where this is demonstrated

- The class rule and its interaction with the `Pending` state — every sample using
  `FormidableInputText` shows it implicitly; [Async validation](async-validation.md)'s
  pending-UI section is the most direct look at `Pending` specifically.
- Renaming two of `FormidableCssClasses`' five class names to fit a UI library's own —
  [`/bootstrap`](../samples/Formidable.Sample/Pages/BootstrapFitting.razor), which points
  `Invalid`/`Valid` at Bootstrap's `is-invalid`/`is-valid` and leaves `Warning`, `Info` and
  `Pending` at their defaults. Bootstrap has no advisory tier to remap onto, so the page invents
  none — harmless there, since its validator never raises a warning or an info.
- A consumer stylesheet keying off those same class names with CSS custom properties instead of
  fixed colours — [`/css-colours`](../samples/Formidable.Sample/Pages/CssColours.razor).
- The `FieldCssClassProvider` bridge and a native `InputBase` picking up the same classes —
  [`/vanilla`](../samples/Formidable.Sample/Pages/VanillaInterop.razor), covered in
  [Component kit](component-kit.md).
- `aria-invalid`/`aria-describedby` on a hand-rolled control —
  [`/foreign`](../samples/Formidable.Sample/Pages/ForeignControl.razor).
- `FormidableSummary`'s live regions and click-to-focus, including the virtualize limit —
  [`/virtualized`](../samples/Formidable.Sample/Pages/Virtualized.razor).
- The field id on markup the page renders itself — a native input, a collection's container —
  [`/vanilla`](../samples/Formidable.Sample/Pages/VanillaInterop.razor),
  [`/collections`](../samples/Formidable.Sample/Pages/Collections.razor) and
  [`/workout`](../samples/Formidable.Sample/Pages/Workout.razor) (the venue region input and the
  attendees group). The model-level gate id no longer needs a page's help:
  [`/disclosure`](../samples/Formidable.Sample/Pages/Disclosure.razor) and `/workout` both show
  the all-suppressed gate landing on the `<form>` element `FormidableForm` renders it on.
