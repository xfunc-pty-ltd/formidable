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
| A hand-written per-form class deciding which fields' errors are currently allowed to show | Render-registration disclosure — a field's visibility is a side effect of whether something registered it while mounted, not code you write per form. See [Disclosure](disclosure.md) |
| A second, hand-maintained message store layered on top of the library's own, plus the bookkeeping to keep the two in sync | One single-writer `ValidationMessageStore`, owned by the engine — there's no second store to keep synchronized. See [Component kit](component-kit.md)'s `FormidableForm` section |
| Hand-written plumbing to get a server's rejection back onto the fields it names, or to ask whether a pass is currently running | `<FormidableValidator>` exposes the engine as `Engine` and forwards both `ApplyServerIssues` overloads itself, so an `EditForm`-hosted form reaches the same pipeline `FormidableForm` does, in the same one line. See [Component kit](component-kit.md#formidablevalidatortmodel-attaching-to-an-existing-form) and [Server integration](server-integration.md) |

### Two ways to attach

If the page already has a working `EditForm`/`EditContext` and you'd rather not restructure it
yet, `<FormidableValidator>` attaches to the cascaded `EditContext` the same way
`<FluentValidationValidator />` does: drop it inside the existing `EditForm` and it starts
running validation against it. `<FormidableForm>` is the alternative for new forms or forms
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
  Formidable's three-pass lifecycle (live, submit, debounced post-submit refresh; see
  [Profiles](profiles.md#the-client-lifecycle)). A rule that runs correctly on every
  pass is unaffected; a rule or handler that only makes sense once per form lifetime may need to
  be scoped to a specific pass.
- **Submit-only rules move to the Submit profile.** Completeness rules that only make sense at
  submit — "this is required to submit, but a blank draft is fine" — belong in the `"Submit"`
  ruleset (or a `ConfigureSubmitRules()` override on `DraftSubmitValidator<T>`), not in the
  default/common rules, or they'll fire during live typing. See [Profiles](profiles.md).
- **Disclosure changes what "always visible" used to mean.** If your prior integration always
  ran a rule and always attempted to show its message once the field failed, regardless of
  whether the field's containing UI was rendered, some of those errors are now suppressed by
  render-registration until the field is actually on screen (see
  [Disclosure](disclosure.md)). Fields wrapped in a Formidable component get this
  automatically; a raw/foreign control needs a `<FormidableFieldAnchor>` alongside it to opt back
  in to being counted as revealed. Sections that were always fully rendered are unaffected either
  way.

## Sample

There's no single "migration" sample page — every page under `samples/Formidable.Sample/Pages/`
is a small, complete form built the Formidable way, so any one of them is a working reference
for what a form on the other side of this mapping looks like end to end. `/` (Quickstart) is the
shortest.
