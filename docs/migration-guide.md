# Migrating from another integration layer

Blazored.FluentValidation and Blazilla both solved the same real problem: wiring FluentValidation
into Blazor's `EditForm`. Plenty of production forms run on one of them today, validators and
all, and nothing here argues they shouldn't. This guide is for anyone moving a form from one of
those onto Formidable — a mechanical map from one component/API surface to the other, not a case
for why you should move. If what you have works, there's no obligation to change it.

## What doesn't change

Your FluentValidation validators port unchanged. `RuleFor`, `.NotEmpty()`, `.WithMessage(...)`,
`.WithSeverity(...)`, custom validators, async rules with `MustAsync` — none of that is
Formidable-specific, and none of it needs to be rewritten. What moves is the *integration* layer
around the validator: how the validator attaches to the form, how rule subsets are selected, and
how results reach the UI.

## Mechanical mapping

| Coming from | Formidable equivalent |
|---|---|
| `<FluentValidationValidator />` inside an `EditForm` | `<FormidableValidator>` (attaches to an `EditForm` you already own — see "Two ways to attach" below), or `<FormidableForm>` if you're open to it owning the `EditForm` itself |
| RuleSet parameters selecting which rules run | A `ValidationProfile` (`ValidationProfile.Draft` / `ValidationProfile.Submit` / `ValidationProfile.Named(...)`) — see [Profiles](profiles.md) |
| `<ValidationMessage For="...">` | `<FormidableFieldMessage For="...">` — severity-aware (renders warnings and infos, not just errors) and resolves paths `ValidationMessage` can't, including indexed collection items and nested-nullable properties. See [Collections and row identity](collections-and-row-identity.md) |
| Manually creating and rebuilding the `EditContext` when the model changes (draft load, reset) | `FormidableForm` owns that lifecycle — swapping its `Model` parameter rebuilds the `EditContext` and re-initializes validation state for you; `FormidableValidator` has no `Model` parameter and instead follows whatever `EditContext` is cascaded to it. See [Component kit](component-kit.md) |
| A hand-written pass over a loaded record, marking its fields touched and running validation so the form does not open looking pristine | `DiscloseLoadedValuesAsync()` on either root — one call that validates the whole model under the submit profile and then confirms the fields holding good values, discloses the ones holding wrong values, and leaves the empty ones silent. See [Component kit](component-kit.md#saying-what-loaded-values-have-earned) |
| A hand-written per-form class deciding which fields' errors are currently allowed to show | Render-registration disclosure — whether a submit shows a field's error is a side effect of something having registered it while mounted, not code you write per form; the live pass between submits answers to engagement instead. See [Disclosure](disclosure.md) |
| A hand-written call to re-validate, or to manually clear stale messages, after removing a row from a collection | Neither root needs it. The engine notices the rendered field set changing on its own — `FormidableForm` on every render, `FormidableValidator` through a registry signal it reconciles just after the render that caused it — and prunes the departed row's live issues, then schedules a reconciling refresh. `NotifyFieldSetChanged()` exists as an override for a call site that can't wait for that automatic pass; ordinary use needs nothing beyond removing the row. See [Fields and collections](fields-and-collections.md) and [`/attach`](../samples/Formidable.Sample/Pages/AttachMode.razor) |
| A second, hand-maintained message store layered on top of the library's own, plus the bookkeeping to keep the two in sync | One single-writer `ValidationMessageStore`, owned by the engine — there's no second store to keep synchronized. See [Component kit](component-kit.md)'s `FormidableForm` section |
| Hand-written plumbing to get a server's rejection back onto the fields it names, or to ask whether a pass is currently running | `<FormidableValidator>` exposes the engine as `Engine` and forwards both `ApplyServerIssues` overloads itself, so an `EditForm`-hosted form reaches the same pipeline `FormidableForm` does, in the same one line. See [Component kit](component-kit.md#formidablevalidatortmodel-attaching-to-an-existing-form) and [Server integration](server-integration.md) |

### Two ways to attach

If the page already has a working `EditForm`/`EditContext` and you'd rather not restructure it
yet, `<FormidableValidator>` attaches to the cascaded `EditContext` the same way
`<FluentValidationValidator />` does: drop it inside the existing `EditForm` and it starts
running validation against it. One difference from the validator you're replacing: its child
content is a typed fragment (it hands the markup the cascaded `FormidableFormContext`), and so
is `EditForm`'s, so the Razor compiler asks you to name one of the two implicit `context`
parameters — add `Context="formidable"` to the `<FormidableValidator>` element and move on.
`<FormidableForm>` is the alternative for new forms or forms
you're willing to restructure: it renders its own `EditForm` and owns the `EditContext`
outright, which is what unlocks automatic rebuild-on-model-swap. Both attach modes share the
same engine underneath, so the mapping table above applies to either — and both expose it the same
way, so a server round trip is written identically whichever root the page ended up with.

## What to check after migrating

A few behaviors are different enough to be worth a deliberate look, not because the new
behavior is wrong, but because a rule or a page written against the old lifecycle might be
relying on the old one implicitly:

- **Rules that assumed a single validation pass.** If a rule (or a handler reacting to
  validation results) assumed there was exactly one pass over the whole form — e.g. counting on
  a submit-time pass to also be the pass that populates some other UI state — check it against
  Formidable's multi-pass lifecycle (live, submit and the debounced refresh among them; see
  [Profiles](profiles.md#the-client-lifecycle)). A rule that runs correctly on every
  pass is unaffected; a rule or handler that only makes sense once per form lifetime may need to
  be scoped to a specific pass.
- **Submit-only rules move to the Submit profile.** Completeness rules that only make sense at
  submit — "this is required to submit, but a blank draft is fine" — belong in the `"Submit"`
  ruleset (or a `ConfigureSubmitRules()` override on `DraftSubmitValidator<T>`) rather than in
  the default/common rules, so a lenient draft save can leave them alone. The bucket is not what
  decides when the rule speaks on screen: a live pass evaluates both buckets unless
  `FormidableOptions.LiveProfile` narrows it, and a message waits for the visitor to engage the
  field rather than for the rule to be held back. See [Profiles](profiles.md).
- **Disclosure changes what "always visible" used to mean.** If your prior integration always
  ran a rule and always attempted to show its message once the field failed, regardless of
  whether the field's containing UI was rendered, some of those submit errors are suppressed by
  render-registration until the field is actually on screen (see
  [Disclosure](disclosure.md)). Fields wrapped in a Formidable component get this
  automatically; a raw/foreign control needs a `<FormidableFieldAnchor>` alongside it to opt back
  in to being counted as revealed. Sections that were always fully rendered are unaffected either
  way. The gate is the submit channel's alone: the live pass that runs between submits answers to
  engagement, so a field the user has committed a change to goes on speaking whether or not
  anything registered it.
- **Attach mode leaves `novalidate` to you.** `<FormidableForm>` renders it on the `<form>` it
  owns, by deliberate default, so the browser's interactive constraint validation never answers a
  submit ahead of FluentValidation. `<FormidableValidator>` renders no `<form>` and does not reach
  the one your `EditForm` owns, so nothing writes the attribute for you: a native constraint
  attribute anywhere inside that form — a `required` or `pattern`, a `type="email"`, a `min` or
  `max` — blocks the submit at the element and fronts the browser's own bubble, and the message
  the visitor reads stops being FluentValidation's. Write `novalidate` on the `EditForm` yourself
  for the same guarantee (an attribute `EditForm` does not recognise lands on the `<form>` it
  renders, which is how the gate id gets there too), or leave it off where the browser's
  constraint UI is what the page wants. See
  [Component kit](component-kit.md#formidablevalidatortmodel-attaching-to-an-existing-form).
- **Attach mode lists issues in the engine's order, not the page's.** Under `<FormidableForm>`,
  a summary reports issues in the document order of the fields that render them, because the form
  resolves where those fields sit and hands its engine the answer. `<FormidableValidator>` renders
  no `<form>` of its own and resolves nothing, so a summary inside your own `EditForm` lists the
  fault issue first, then submit errors, then advisories, then live issues — close to the order
  the validator declares its rules in. Nothing misbehaves; the reading order is simply the
  validator's. Move the page to `<FormidableForm>` if the reading order matters to it (see
  [Component kit](component-kit.md#the-order-entries-appear-in)).
- **A blocked submit still moves focus, as long as you submit through the component.** Capture
  the `<FormidableValidator>` with `@ref` and call `ValidateForSubmitAsync()` on it, rather than
  reaching past it to `Engine` for the engine's own method, and a blocked submit lands the visitor
  on the first error exactly as it does under `<FormidableForm>`: the same
  `FocusFirstErrorOnInvalidSubmit` switch, the same
  [`PrepareFocus`](component-kit.md#preparefocus) hook awaited ahead of that move, and the same
  `FocusFallback` seam for an error whose element is not currently rendered. "First" here means
  first in the reading order above, not first down the page. Focus parity is not order parity, and
  the two are separate boundaries with separate causes. `FocusFirstErrorAsync()` is on the
  validator too, for a page that would rather choose the moment than have the submit choose it.
  The one move that stays quiet is the server
  round trip: `ApplyServerIssues` applies the verdict and focuses nothing, since the page owns both
  the `<form>` and whatever it does after a rejection. Clicking a summary entry moves focus in
  either root.

## Sample

There's no single "migration" sample page — every page under `samples/Formidable.Sample/Pages/`
is a small, complete form built the Formidable way, so any one of them is a working reference
for what a form on the other side of this mapping looks like end to end. `/` (Quickstart) is the
shortest. [`/attach`](../samples/Formidable.Sample/Pages/AttachMode.razor) is the one built for
this page specifically: a consumer's own `EditForm` wearing `FormidableValidator`, a plain
`InputText` left unmigrated beside a Formidable-managed expense list, and a row whose removal
needs nothing extra to leave the page along with it.
