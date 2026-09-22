# Migrating from another integration layer

Blazored.FluentValidation and Blazilla both solved the same real problem: wiring FluentValidation
into Blazor's `EditForm`. Plenty of production forms run on one of them today, validators and all,
and nothing here argues they shouldn't.

This guide is for anyone moving a form from one of those onto Formidable. It is a mechanical map
from one component and API surface to the other, not a case for why you should move. If what you
have works, there's no obligation to change it.

## What doesn't change

Your FluentValidation validators port unchanged. `RuleFor`, `.NotEmpty()`, `.WithMessage(...)`,
`.WithSeverity(...)`, custom validators, async rules with `MustAsync` — none of that is
Formidable-specific, and none of it needs to be rewritten.

What moves is the *integration* layer around the validator: how the validator attaches to the form,
how rule subsets are selected, and how results reach the UI.

## Mechanical mapping

Three pieces of the old integration layer have a direct Formidable equivalent.

| Coming from | Formidable equivalent |
|---|---|
| `<FluentValidationValidator />` inside an `EditForm` | `<FormidableValidator>` attaches to an `EditForm` you already own. `<FormidableForm>` renders and owns one itself. "Two ways to attach" below has the choice. |
| RuleSet parameters selecting which rules run | A `ValidationProfile` (`ValidationProfile.Draft` / `ValidationProfile.Submit` / `ValidationProfile.Named(...)`). See [Profiles](profiles.md) |
| `<ValidationMessage For="...">` | `<FormidableFieldMessage For="...">`. It is severity-aware, rendering warnings and infos, not just errors. The engine resolves each reported path, indexed collection items and nested-nullable properties included, to the owning object instance, so the message lands on the exact row and stays there when rows move. See [Collections and row identity](collections-and-row-identity.md) |

The rest of that layer is plumbing the library owns instead, so migrating it is mostly deletion.

| Coming from | Formidable equivalent |
|---|---|
| Manually creating and rebuilding the `EditContext` when the model changes (draft load, reset) | `FormidableForm` owns that lifecycle: swapping its `Model` parameter rebuilds the `EditContext` and re-initializes validation state for you. `FormidableValidator` has no `Model` parameter and instead follows whatever `EditContext` is cascaded to it. See [Component kit](component-kit.md) |
| A hand-written pass over a loaded record, marking its fields touched and running validation so the form does not open looking pristine | `DiscloseLoadedValuesAsync()` on either root. One call validates the whole model under the submit profile, then confirms the fields holding good values, discloses the ones holding wrong values, and leaves the empty ones silent. See [Component kit](component-kit.md#saying-what-loaded-values-have-earned) |
| A hand-written per-form class deciding which fields' errors are currently allowed to show | Render-registration disclosure. Whether a submit shows a field's error is a side effect of something having registered it while mounted, not code you write per form. The live pass between submits answers to engagement instead. See [Disclosure](disclosure.md) |
| A hand-written call to re-validate, or to manually clear stale messages, after removing a row from a collection | Neither root needs it. Both notice when the rendered field set changes, prune the departed row's live issues, and schedule a reconciling refresh, so removing the row is the whole edit. `NotifyFieldSetChanged()` is there for a call site that cannot wait for that refresh. See [Collections and row identity](collections-and-row-identity.md) and [`/attach`](../samples/Formidable.Sample/Pages/AttachMode.razor) |
| A second, hand-maintained message store layered on top of the library's own, plus the bookkeeping to keep the two in sync | One single-writer `ValidationMessageStore`, owned by the engine. There's no second store to keep synchronized. See [Server integration](server-integration.md#the-store-as-a-compatibility-bridge) |
| Hand-written plumbing to get a server's rejection back onto the fields it names, or to ask whether a pass is currently running | `<FormidableValidator>` exposes the engine as `Engine` and forwards both `ApplyServerIssues` overloads itself, so an `EditForm`-hosted form reaches the same engine pipeline in one line. The validator's apply is quiet, where `FormidableForm`'s own overloads treat an error-carrying apply like a blocked submit and move focus. See [Component kit](component-kit.md#formidablevalidatortmodel-attaching-to-an-existing-form) and [Server integration](server-integration.md) |

### Two ways to attach

If the page already has a working `EditForm`/`EditContext` and you'd rather not restructure it yet,
`<FormidableValidator>` attaches to the cascaded `EditContext` just as
`<FluentValidationValidator />` did. It is not the same markup shape, though.

The old validator sat beside the form's content as an empty, self-closing element.
`<FormidableValidator>` wraps content instead: drop it inside the existing `EditForm` and nest the
form's Formidable components inside it, because its cascade reaches only its own child content.

With no child content, `<FormidableValidator>` renders nothing at all. The engine still attaches and
validates, but a `FormidableFieldMessage` or `FormidableSummary` left beside it throws for want of a
cascaded context.

A second difference, after the markup shape: the validator's child content is a typed fragment,
handing the markup the cascaded `FormidableFormContext`, and so is `EditForm`'s. The Razor compiler
asks you to name one of the two implicit `context` parameters. Add `Context="formidable"` to the
`<FormidableValidator>` element and move on.

`<FormidableForm>` is the alternative for new forms, or for forms you're willing to restructure. It
renders its own `EditForm` and owns the `EditContext` outright, which is what unlocks automatic
rebuild-on-model-swap.

Both attach modes share the same engine underneath, so the mapping above applies to either. Both
expose it the same way too, so a server round trip is written identically whichever root the page
ended up with.

## What to check after migrating

A few behaviors differ enough from the old integration layers to be worth a deliberate look. The new
behavior isn't wrong; it's that a rule or a page written against the old lifecycle might be relying
on the old one implicitly.

- **Rules that assumed a single validation pass.** Check any rule, or any handler reacting to
  validation results, that counted on there being exactly one pass over the whole form (a
  submit-time pass that also populates some other UI state, say).

  Formidable's lifecycle runs several passes: live, submit and the debounced refresh among them (see
  [Profiles](profiles.md#the-client-lifecycle)). A rule that runs correctly on every pass is
  unaffected. One that only makes sense once per form lifetime may need scoping to a specific pass.

- **Submit-only rules move to the Submit profile.** A completeness rule that only makes sense at
  submit is the "this is required to submit, but a blank draft is fine" kind. It belongs in the
  `"Submit"` ruleset, or in a `ConfigureSubmitRules()` override on `DraftSubmitValidator<T>`, rather
  than in the default or common rules. A lenient draft save then leaves it alone.

  The bucket is not what decides when the rule speaks on screen. A live pass evaluates both buckets
  unless `FormidableOptions.LiveProfile` narrows it, and a message waits for the visitor to engage
  the field rather than for the rule to be held back. See [Profiles](profiles.md).

- **Disclosure changes what "always visible" used to mean.** A prior integration may have run every
  rule and tried to show every failed field's message, rendered or not. Some of those submit errors
  are suppressed here: a submit that finds the field unrendered leaves its error out, unless a
  `FormidableOptions.DisclosureOverride` says to show it.

  Disclosure is decided at the submit rather than at the mount, so a later-mounted field's error
  waits for the next submit. Between submits the live pass answers to engagement instead (see
  [Disclosure](disclosure.md)).

  Fields wrapped in a Formidable component get this automatically. A raw or foreign control needs a
  `<FormidableFieldAnchor>` alongside it to opt back in to being counted as revealed. Sections that
  were always fully rendered are unaffected either way.

  The gate belongs to the submit channel's client half, and server-declared errors bypass it. The
  live channel has no such gate by default. A field the user has committed a change to goes on
  speaking whether or not anything registered it, unless `FormidableOptions.LiveDisclosure` opts it
  into the same visibility test.

### Attach mode leaves `novalidate` to you

`<FormidableForm>` renders `novalidate` on the `<form>` it owns, by deliberate default, so the
browser's interactive constraint validation never answers a submit ahead of FluentValidation.

`<FormidableValidator>` renders no `<form>` and does not reach the one your `EditForm` owns, so
nothing writes the attribute for you. A native constraint attribute anywhere inside that form (a
`required` or `pattern`, a `type="email"`, a `min` or `max`) blocks the submit at the element and
fronts the browser's own bubble. The message the visitor reads then stops being FluentValidation's.

Write `novalidate` on the `EditForm` yourself for the same guarantee: an attribute `EditForm` does
not recognise lands on the `<form>` it renders, which is how the gate id gets there too. Or leave it
off where the browser's constraint UI is what the page wants. See
[Component kit](component-kit.md#formidablevalidatortmodel-attaching-to-an-existing-form).

### Attach mode leaves the form element's `aria-describedby` to you

`FormidableForm` points the `<form>` it owns at the model-level message list's id, so a
`FormidableModelMessage` describes the form without anything being wired.

`<FormidableValidator>` renders the same component but reaches no `<form>`, so a page rendering that
component writes the attribute on its own `EditForm` (`FormidableFieldId.MessagesFor` of the
model-level field, beside the gate id it already writes there).

It is the same split the sections around it describe, and it has its own cause: what a Formidable
component renders travels into attach mode, and what `FormidableForm`'s own `<form>` element carries
does not. See
[Component kit](component-kit.md#formidablevalidatortmodel-attaching-to-an-existing-form).

### Attach mode lists issues in the engine's order, not the page's

Under `<FormidableForm>`, a summary reports issues in the document order of the fields that render
them, because the form resolves where those fields sit and hands its engine the answer.

`<FormidableValidator>` renders no `<form>` of its own and resolves nothing. A summary inside your
own `EditForm` lists the fault issue first, then submit errors, then advisories, then live issues —
close to the order the validator declares its rules in.

Nothing misbehaves; the reading order is simply the validator's. Move the page to `<FormidableForm>`
if the reading order matters to it (see
[Component kit](component-kit.md#the-order-entries-appear-in)).

### Attach mode moves focus on a blocked submit, as long as you submit through the component

Capture the `<FormidableValidator>` with `@ref` and call `ValidateForSubmitAsync()` on it, rather
than reaching past it to `Engine` for the engine's own method.

A blocked submit then lands the visitor on the first error exactly as it does under
`<FormidableForm>`: the same `FocusFirstErrorOnInvalidSubmit` switch, the same
[`PrepareFocus`](component-kit.md#preparefocus) hook awaited ahead of that move, and the same
`FocusFallback` seam for an error whose element does not take focus.

"First" here means first in the reading order above, not first down the page. Focus parity is not
order parity, and the two are separate boundaries with separate causes. `FocusFirstErrorAsync()` is
on the validator too, for a page that would rather choose the moment than have the submit choose it.

The one move that stays quiet is the server round trip. The validator's `ApplyServerIssues` applies
the verdict and focuses nothing, since the page owns both the `<form>` and whatever it does after a
rejection.

That quiet is a difference, not parity: `FormidableForm`'s own `ApplyServerIssues` overloads treat
an error-carrying apply as a blocked submit that arrived late and move focus to the first error.
Clicking a summary entry moves focus in either root.

## Sample

There's no single "migration" sample page. Every page under `samples/Formidable.Sample/Pages/` is a
complete form built the Formidable way. Any one of them shows what a form looks like on the other
side of this mapping. `/` (Quickstart) is the shortest.

[`/attach`](../samples/Formidable.Sample/Pages/AttachMode.razor) is the one built for this page
specifically: a consumer's own `EditForm` wearing `FormidableValidator`, a plain `InputText` left
unmigrated beside a Formidable-managed expense list, and a row whose removal needs nothing extra to
leave the page along with it.
