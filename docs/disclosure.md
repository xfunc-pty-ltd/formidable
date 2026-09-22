# Progressive disclosure

**You should already know:** that a submit runs both rule buckets while an unchanged field stays
quiet until one ([Draft and submit rules](tutorial/2-draft-and-submit.md)), and how a
`ValidationProfile` picks which rules run at which moment ([Profiles](profiles.md)).

Hiding a field looks simple until its validation rule keeps running. Gate a shipping address
behind "ship to a different address," gate step two behind step one passing, and the rule
underneath never blinks. It still runs, still fails, against a field that isn't on screen. Show
that failure anyway and the user hits a dead end with no field to fix.

Suppress the failure by hand, per form, and you're one missed `@if` away from a submit button that
quietly does nothing. Formidable exists because that failure needs an audience-aware home, not a
per-form workaround. At submit, an issue surfaces when something says it has an audience: mounted
markup, a watch from an earlier disclosure, or an explicit override. A submit that can show
nothing says so out loud.

## Need to know

At submit, a field's issue reaches the screen if something is currently rendering that field. The
components that register one are `FormidableInputText` and other `FormidableInputBase<TValue>`
descendants, the renderless `FormidableField`, the registration-only `FormidableFieldAnchor`, and
`FormidableCollectionMessage` for the collection-level path it renders messages for. Each
registers the field for as long as it stays mounted.

Showing a field's error also starts watching that field, and the watch outlives the registration.
Until the form passes or is reset, that field's answer keeps surfacing whether or not anything
still renders it. The live pass that runs between submits answers to a different rule entirely:
engagement, not registration ([below](#the-live-channel-plays-by-its-own-rule)).

The one guardrail is this: if everything that's failing is unregistered and unwatched, a defensive
gate blocks the submit with a form-level explanation rather than letting it quietly do nothing.

```csharp
public string DefensiveGateMessage { get; set; } =
    "The form cannot be submitted because information that is not currently displayed is invalid.";
```

<!-- Source: `src/Formidable.Blazor/FormidableOptions.cs` -->

That sentence is a default rather than a fixture. It is English, so a form that addresses its
users in another language, or in a wording of its own, replaces it through
[`DefensiveGateMessage`](options.md#defensivegatemessage). The engine builds the gate's issue
where it reads that option, and a replacement reaches the next surface that asks.

The gate's explanation is a model-level issue: it belongs to the form, not to any field. Every
surface that reads model-level issues is served it, not the summary alone.

`FormidableSummary` lists the explanation, and its click-to-focus addresses the form by
`FormidableFieldId.For(new FieldIdentifier(model, string.Empty))`, exactly like a named field.
`FormidableForm` renders that id on its own `<form>` automatically, plus `tabindex="-1"` so the
otherwise-inert element can hold focus (see
[CSS and accessibility](css-and-accessibility.md) for the id mechanics). The `EditContext`'s
message store carries it too, so Blazor's own `<ValidationSummary />` shows it on a form that
uses the native components.

On a form that renders inline messages only,
[`FormidableModelMessage`](component-kit.md#formidablemodelmessage) is the surface built for the
gate's explanation. It is the same persistent list the field messages render, fixed to the
model-level field, so the gate's explanation has somewhere to appear on exactly the form that has
no summary to carry it. Attach mode's `FormidableValidator` renders no `<form>` of its own, so a
page using it renders the focus id by hand — see [Component kit](component-kit.md)'s
`FormidableValidator` section for the pattern.

A page that renders none of those — no summary, no model message, nothing reading the store —
can still read the issues itself. `Engine.GetIssues(new FieldIdentifier(model, string.Empty))`
returns them, the gate's explanation included, and `GetVisibleIssues()` carries them among
everything else.

A hand-rolled read should keep the persistent-element discipline the shipped components apply:
render the container always and let items come and go. Otherwise the announcement the gate exists
to make is the one most likely to be dropped (see
[CSS and accessibility](css-and-accessibility.md#formidablesummary-as-a-live-region) for why).

That explanation is derived, not filed. There is no gate entry anywhere to keep or lose, only the
conditions that make one true: a submit that blocked with nothing to show, an answer that still
carries errors, and nothing disclosed since. So the debounced refresh that follows every
post-submit edit cannot take that explanation away while nothing on screen explains the block.

The gate gives way the moment there's a real message to give way to: an error the user can see, a
server-declared error arriving, or an answer that finally comes back clean. Every submit decides it
again from scratch, so it can stand at one submit, give way at the next, and come back at the one
after that.

What the gate cannot come back for is a failure the form is still watching. Showing a field's
error starts that watch, and it holds until a successful submit or a `ResetAsync` clears it, so for
as long as it lasts the field's own failure explains itself.

What raises the gate is a failure nothing currently discloses. One route is a field nothing has
ever shown, its section still collapsed at the submit where everything visible finally passes. The
other is a successful submit, which empties the watched set: a field an earlier submit showed can
raise the gate at a later one that finds it off screen again.

That gate only fires when nothing about the failure can be shown; the ordinary case is narrower.
At submit, the engine resolves each FluentValidation failure's property path to a
`FieldIdentifier` through the model introspector, then asks the registry (`FieldRegistry`)
whether that identifier is currently registered. [Collections and row
identity](collections-and-row-identity.md) covers how that resolution walks indexed paths.

A field has no matching registration when the markup that would render it sits behind an `@if`
that isn't satisfied. The rule still ran and the issue still exists in the validator's report, but
it never reaches the `EditContext`'s message store, `FormidableFieldMessage`, or
`FormidableSummary`.

The issue is suppressed instead, and `SuppressedIssueDiagnostic` (see [Options](options.md)) is
invoked once per suppressed *error* so you can still observe it outside the UI. A submit's
suppressed advisories reach no diagnostic at all; applying a server verdict is the one path that
reports one, [below](#disclosureoverride-the-escape-hatch).

That registry answer decides one thing: whether the field joins the watched set. It is asked at
the moment `ValidateForSubmitAsync` runs, not continuously, and the set it feeds only grows. Later
blocked submits add to it, applying a server's verdict adds to it, and nothing removes a field
until a successful submit or a `ResetAsync` empties the set.

So the debounced refresh that follows a submit re-validates the whole model and re-answers the
watched fields, rather than deciding membership again. A field whose error the user fixed loses
its message because the rule stopped producing one, and if the value breaks again the message
returns, with no second submit needed. On the default `LiveProfile` the live pass behind a
post-submit edit rebuilds the submit channel's answer as well. With no `LiveDebounce` set,
neither direction waits out a debounce at all.

The entry follows the answer; the watch follows the submit. A field nothing has ever watched
contributes nothing to the submit channel even while it's failing, and rendering it doesn't
change that. It surfaces here at the *next* submit. Whether anything is already speaking for it
meanwhile is the live channel's business, on its own rule, below.

End to end, that's submit as the disclosure event, an unregistered field's issue suppressed, the
watch a shown field keeps, and the defensive gate catching the case where nothing could be shown
at all:

```mermaid
flowchart TD
    A["Submit runs"] --> B["For each failing field: is it already watched, or is a rendering component registered for it?"]
    B -- "yes" --> C["Field is watched until the form passes or resets; its issues show in FormidableSummary, and inline wherever the field renders"]
    B -- "no" --> D["Issue is suppressed for this submit"]
    D --> E["SuppressedIssueDiagnostic fires once per suppressed error; suppressed advisories are dropped silently"]

    C --> F{"Any error shown?"}
    E --> F
    F -- "yes" --> G["Submit blocks; every message surface shows whichever issues are visible now"]
    F -- "no, every failing field was hidden" --> H["Defensive gate explains the block at form level instead"]
    H --> G

    I["A field shown at an earlier submit is hidden before the next one runs"] --> J["It unregisters, but the watch stays: the check at B answers yes anyway"]
    J --> C
```

That's the submit channel's mechanism in full. Render a failing field and its issue shows, and
goes on showing until the answer comes clean. Leave it unrendered and unwatched and it says
nothing until the next submit.

The live channel that runs between submits works differently, and is covered next. What's left
after that is the shape of the rules that keep a UI condition and a `.When(...)` condition honest
with each other, plus the escape hatches for controls Formidable doesn't wrap.

## The live channel plays by its own rule

Everything above is the submit channel's story. An issue reaches the screen because a rendering
component was registered for its field *at the moment submit ran*, or because an earlier submit
already put that field under watch. The live pass that runs after every field change has no such
gate.

By default registration filters the live channel nowhere: the engine files a live verdict for
every engaged field without consulting the registry. Every surface reads that verdict back the
same way, whether or not anything currently renders the field. That covers the engine's own issue
reads, the kit's own message components, `FormidableSummary`, and the `EditContext`'s message
store a native `ValidationMessage` renders from.

Filtering nothing by default is deliberate: it keeps the store usable as a bridge for native
components. A form built out of plain `InputBase` inputs registers nothing at all, and its live
errors have to reach the store regardless (see
[Server integration](server-integration.md#client-round-trip)).
[`FormidableOptions.LiveDisclosure`](options.md#livedisclosure) is the one way to narrow that, and
it narrows every surface at once rather than one of them.

Two bounds cap how long a live verdict stands either way. The first is departure: a live issue for
a field that has since left the page goes with the next rendered-field-set change, along with
everything else the departure invalidates.

*Left* is the operative word. A field something rendered once and nothing renders now has left. A
field nothing has ever registered never arrived, and no amount of churn elsewhere on the page
takes its live verdict away. That distinction is the whole of what registration decides on this
channel.

The second bound is a submit, which takes the live channel over wholesale: every live verdict is
dropped, and the submit's own report stands as the answer. On a registered field the handover
shows as nothing at all, because the submit reveals the same message its report re-derives.

On a field a submit cannot reveal — the anchor-free native field of
[the anchor section below](#formidablefieldanchor-for-raw-and-foreign-controls) — a blocked submit
takes the on-screen live error with it. The message returns when the next edit's live pass
re-answers the engaged set.

What gates the live channel is a different question: not *is this field rendered*, but *has this
field been engaged*. The engine keeps a first-class engaged-field set, and a field enters it the
moment a field-changed notification names it. The notification comes from a kit input committing a
value (in every `UpdateOn` mode, `OnBlur` included, since a committed change is the only thing
that ever notifies), a native input's own change, or an explicit
`FormidableFieldContext.NotifyChanged()`.

A notification is not the only way in. A page that fills the model itself — from a saved draft,
or a record opened for editing — engages those fields by calling
`IFormidableEngine.DiscloseLoadedValuesAsync()`, since writing model properties notifies
nothing on its own.

A live pass validates the whole model on every change, the same as any other pass. Its verdict
answers every engaged field: the report's issues where it has them, an empty verdict where it says
nothing. An engaged field's message therefore clears — or appears — because of an edit to a
*different* field, which is what keeps a cross-field rule current between submits.

A field never engaged keeps no live verdict at all, however loudly its rule is failing underneath.
That is the gate that does the work of keeping a form from nagging. It is why a submit-ruleset
rule can be evaluated live — `FormidableOptions.LiveProfile` follows the submit profile by
default — without a fresh page complaining about fields nobody has reached.

Engagement is also why a fresh, untouched row in a collection stays silent even when its rule is
already failing against it: nothing has engaged its fields yet. Rule selection is the second and
blunter lever. [Narrowing it](recipes.md#i-want-to-narrow-what-the-live-channel-validates) is for
rules too expensive to run per change rather than for rules merely strict.

Where that line sits is deliberate. Focusing a field and tabbing back out again is not engagement:
the visitor may only have been passing through. A message that appears anyway is how people learn
to stop reading a form's messages at all.

Engagement is a committed value change: typing something, or clearing something that was there. A
page can also say, through `IFormidableEngine.DiscloseLoadedValuesAsync()`, that the values it
loaded stand in for one. Engagement ends the way a live issue does: a field pruned from the
rendered set leaves the engaged set with it, one committed change away from re-engaging.
[The live/refresh asymmetry](recipes.md#i-want-to-validate-while-typing-on-blur-or-only-at-submit)
covers how this interacts with the debounced refresh that follows a submit.

A loaded value standing in for a committed change is what lets a bad saved value speak. Marking
those fields touched and no more would paint the good ones green and leave the wrong one silent
among them. Silence in a row of green reads as "not filled in yet" rather than "this one is
wrong".

One more distinction is worth being precise about, since it looks related and isn't. Whether a
field has been touched or modified gates its CSS class only, never a message. `formidable-invalid`
paints the moment there's an error, ungated. The warning, info and valid tiers wait for the field
to be touched or modified. Valid waits for one thing more: the engine being able to say a submit
would not fail the field.

So an error-free field the visitor hasn't touched earns no state class at all, whatever it carries
(see [CSS and accessibility](css-and-accessibility.md) for the full rule). A message list, a
summary entry, and the message store all read whatever the engine currently holds for the field
regardless, live issues included. So a field can carry a visible message before it ever earns a
class describing it.

## The two patterns

The disclosure sample teaches two different shapes, and they are not interchangeable. The wrong
one either nags the user for fields they haven't reached, or makes a valid answer unsubmittable.

### Pattern 1 — UI-gated sections

A section is collapsed by pure UI state that has nothing to do with the model. The rule behind
it is unconditional, so it always runs; whether it is *visible* depends entirely on whether the
user has opened the section:

```razor
<p>
    <button type="button"
            @onclick="() => _showDetails = !_showDetails">
        @(_showDetails ? "Hide" : "Show") traveler details
    </button>
</p>
@if (_showDetails)
{
    <div class="field">
        <label>Traveler name <FormidableRequiredIndicator For="() => _request.TravelerName" />
            <FormidableInputText @bind-Value="_request.TravelerName" /></label>
        <FormidableFieldMessage For="() => _request.TravelerName" />
    </div>
}
```

<!-- Source: `samples/Formidable.Sample/Pages/Disclosure.razor` -->

The corresponding rule has no `.When(...)` at all:

```csharp
RuleFor(t => t.TravelerName).NotEmpty().WithMessage("Traveler name is required");
```

<!-- Source: `samples/Formidable.Sample.Shared/TravelRequest.cs` -->

Submit while collapsed and the missing-name error is genuinely suppressed — logged to the
diagnostic, not shown inline. Expanding the section registers the field, but the earlier
suppressed error does not retroactively appear (per the refresh rule above). Submitting again is
what lets the now-registered field pick it up.

Use this pattern for disclosure that is a pure UI convenience: an expandable "more details" panel,
a collapsed advanced section. It fits whenever the rule should count toward validity regardless of
whether the user chose to look.

### Pattern 2 — data-gated cascades

A field's relevance is driven by another field's value, and the rule's `.When(...)` condition
mirrors the same condition the `@if` uses to render it. The accommodation type is a native
`<select>` — nothing Formidable would otherwise wrap — so it renders inside `FormidableField`.

The `FormidableField` context supplies the plumbing the control needs: `ElementId`, `AriaInvalid`,
`AriaDescribedBy`, `CssClass`. They are spelled out one attribute at a time here, though
`@attributes="field.InputAttributes"` splats them in one go, adding `aria-required` on a field the
submit profile demands a value for. The context also exposes the `NotifyChanged()` the `@onchange`
handler calls explicitly, so the engine's live pass runs on every change the same way it would for
a Formidable-wrapped input:

```razor
@if (_request.NeedsAccommodation == true)
{
    <fieldset>
        <legend>Accommodation</legend>
        <FormidableField For="() => _request.AccommodationType" Context="field">
            <label>Type
                <select id="@field.ElementId" class="@field.CssClass"
                        aria-invalid="@(field.AriaInvalid ? "true" : null)"
                        aria-describedby="@field.AriaDescribedBy"
                        value="@_request.AccommodationType"
                        @onchange="args => OnTypeChanged(args, field)">
                    <option value="">Choose…</option>
                    <option>Hotel</option>
                    <option>Serviced apartment</option>
                    <option>Accessible</option>
                </select>
            </label>
        </FormidableField>
        <FormidableFieldMessage For="() => _request.AccommodationType" />

        @if (_request.AccommodationType == "Accessible")
        {
            <div class="field">
                <label>Special requirements
                    <FormidableInputText @bind-Value="_request.SpecialRequirements" /></label>
                <FormidableFieldMessage For="() => _request.SpecialRequirements" />
            </div>
        }
    </fieldset>
}
```

<!-- Source: `samples/Formidable.Sample/Pages/Disclosure.razor` -->

```csharp
RuleFor(t => t.AccommodationType).NotEmpty().WithMessage("Choose an accommodation type")
    .When(t => t.NeedsAccommodation == true);
RuleFor(t => t.SpecialRequirements).NotEmpty().WithMessage("Describe the special requirements")
    .When(t => t.NeedsAccommodation == true && t.AccommodationType == "Accessible");
```

<!-- Source: `samples/Formidable.Sample.Shared/TravelRequest.cs` -->

Because the rule simply does not run unless `NeedsAccommodation == true`, there is never a
hidden failing issue to suppress in the first place. The model's own state keeps the rule and
the UI in lockstep, by construction.

That lockstep is more than a nicety. Without the matching `.When`, answering "No" would leave
`AccommodationType`'s unconditional rule permanently failing and permanently unregistered. That is
exactly the all-suppressed case the defensive gate exists for. The "No" data path could never
submit. Mirroring the condition is what lets that alternate path submit at all. Use this pattern
whenever a field's relevance is determined by data, not by a UI-only toggle.

## FormidableFieldAnchor for raw and foreign controls

Anything Formidable doesn't wrap — a plain `<input>`, a native `<select>`, a third-party
component — never registers on its own. `FormidableFieldAnchor` is a registration-only marker
that renders nothing. Placing it next to a raw control keeps automatic disclosure truthful for it.

The anchor suits controls that already notify the `EditContext` of changes through the ordinary
Blazor forms pipeline. A native `InputText` is itself an `InputBase<TValue>` descendant, so it
calls `EditContext.NotifyFieldChanged` on its own without any help. The vanilla-interop sample's
nickname field is one shipped example (`/workout`'s venue region and `/attach`'s submitter name
are the same wiring):

```razor
<div class="field">
    <label>Nickname (native InputText)
        <InputText @bind-Value="_order.Nickname"
                   id="@NicknameId"
                   aria-invalid="@NicknameAriaInvalid"
                   aria-describedby="@NicknameAriaDescribedBy" /></label>
    <ValidationMessage For="() => _order.Nickname" id="@NicknameMessagesId" />
    <FormidableFieldAnchor For="() => _order.Nickname" />
</div>
```

<!-- Source: `samples/Formidable.Sample/Pages/VanillaInterop.razor` -->

The `id`, `aria-describedby` and `aria-invalid` alongside it answer a different question —
click-to-focus and the input's assistive-technology story, both covered in
[CSS and accessibility](css-and-accessibility.md). Registration is the anchor's job alone.

Without the anchor, no submit would ever reveal `Nickname`. The native `InputText` mounts no
Formidable component, so nothing registers the field, and its submit errors are suppressed as
unrevealed while its issues sort last in a resolved issue order.

What the anchor is not needed for is the live channel. Blazor's own binding notifies the engine on
every change, which engages the field, and an engaged field's live verdict discloses on every
surface regardless. The anchor is how the *submit* channel learns the field is on the page.

The disclosure page's accommodation radio group and type `<select>` used to be anchored the same
way. A hand-wired `@onchange` on a raw element doesn't call `EditContext.NotifyFieldChanged` the
way `InputBase` does. Both now render inside `FormidableField` instead, whose context exposes the
`NotifyChanged()` their handlers call explicitly.

Reach for `FormidableFieldAnchor` when a raw or foreign control already drives the engine's live
pass by some other means and only needs registering. Reach for `FormidableField` when a raw or
hand-wired control needs to trigger that pass itself.

## KeepRegistered and virtualization

Every registering component — `FormidableInputBase<TValue>` descendants, `FormidableFieldAnchor`,
`FormidableField`, and `FormidableCollectionMessage` — exposes a `KeepRegistered` parameter. A
container such as `Virtualize` disposes rows that scroll out of view, even though they remain part
of the form.

Without `KeepRegistered`, a scrolled-away row's field unregisters, and its live messages go quiet
with it while the row still sits in the model, still failing. Setting `KeepRegistered` on the
row's fields keeps them registered after disposal, so scrolling takes no message away, and a
submit can still disclose a row the visitor scrolled past. See the Virtualize section of
[Component kit](component-kit.md) for the full pattern.

## DisclosureOverride: the escape hatch

`FormidableOptions.DisclosureOverride` is the escape hatch for an issue whose field nothing
renders. Where a channel consults it at all, it is asked per issue and *before* the registry
check. Return `true` to answer yes, `false` to answer no, or `null` to defer to the registry as
described above.

An answer is an input to the asking channel's own disclosure rule rather than a switch over what
is on screen. The channels differ in what they make of it, and the paragraphs below take them in
turn. The override is the way out for issues that don't fit the render-registration model:
reaching a rule's failure without wrapping its field, or keeping a known-noisy one from being the
reason a field is watched.

What each answer decides at submit is whether that issue puts its field under watch, and the
watch is per field rather than per issue. So a `false` on one of two issues failing on the same
field keeps that issue from being the reason the field is watched, and no more. Once the other
issue starts the watch, the field's answer shows whole, and the suppressed-issue diagnostic
reports neither of them, since neither is in fact hidden.

The live channel is a separate question again. Under its default policy nothing filters it, this
override included, and its [`LiveDisclosure`](options.md#livedisclosure) opt-in is what applies
the override there, issue by issue.

Server-applied *errors* (`Engine.ApplyServerIssues`, see
[Server integration](server-integration.md)) bypass
the registry check entirely rather than defer to it. They're visible unless `DisclosureOverride`
explicitly returns `false`. The server already validated the submitted data, and an error that
blocks the save has to reach the user whether or not the client happened to render its field.

Advisories in a server payload defer to the registry like the client's own, and a suppressed one
fires the diagnostic — which a submit's own suppressed advisories do not. Nothing is stranded by
that: an advisory blocks no submit, so hiding one leaves the user nothing to fix.

A submit reports its own error-severity issues on fields it leaves unwatched, and a server response
reports the advisories its visibility answer hid. Those two sites are the whole of what gets
reported; everything else a visibility answer hides is dropped in silence. That covers a submit's own
advisories, every live issue under `LiveIssueDisclosure.EngagedAndVisible`, and a server-declared
*error*, which bypasses the registry rather than deferring to it.

Both reporting sites write a `Trace`-output warning whether or not
[`SuppressedIssueDiagnostic`](options.md#suppressedissuediagnostic) is set. Where the host resolved
an `ILoggerFactory` they log a warning too — on WebAssembly that is the browser console, needing no
wiring to be seen.

**Samples:** [`/disclosure`](../samples/Formidable.Sample/Pages/Disclosure.razor) — the
suppressed-issue list on the page reflects only the most recent submit. It's cleared at the start
of each submit, and the diagnostic repopulates it as that submit runs. A fully disclosed submit
leaves it empty instead of carrying forward what an earlier submit hid. The same page's
summary-less variant form shows the defensive gate arriving through `FormidableModelMessage`
instead of a summary entry.

[`/workout`](../samples/Formidable.Sample/Pages/Workout.razor) shows the same UI-gating pattern
(catering toggled off over an unconditional `DietaryNotes` rule) and the all-suppressed defensive
gate, both appearing inside a composite form alongside every other feature.
