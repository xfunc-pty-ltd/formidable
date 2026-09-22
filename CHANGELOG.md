# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog 1.1.0](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/) (pre-1.0: the
`0.MINOR.PATCH` surface can still move).

## [Unreleased]

Formidable is a form-validation library for Blazor, built on FluentValidation, shipped as
three packages: `Formidable` (validation profiles, `ProfiledValidator<T>` /
`DraftSubmitValidator<T>`, the `IModelValidator` seam, `ValidationIssue` /
`ValidationReport`, `INormalizableModel`, model introspection — no Blazor dependency),
`Formidable.Blazor` (the validation engine and a headless component kit —
`FormidableForm`, `FormidableField`, `FormidableInputText`/`Select`/`TextArea`/`Number`/
`Date`, `FormidableFieldMessage`, `FormidableCollectionMessage`, `FormidableSummary`), and
`Formidable.AspNetCore` (a Minimal API endpoint filter and an MVC `[Validate]` action
filter that return `ValidationProblemDetails` in the same shape the Blazor client
consumes). Draft and Submit profiles, progressive disclosure, row-stable collection
errors, and a client/server round trip driven by one validator are the core promise; the
sections below list what shipped since work on the library began, ending with this
pre-publish polish wave.

### Added

- `FormidableOptions.LiveDebounce` (`TimeSpan?`, default `null`) — debounces the live pass
  a field change triggers instead of running it on every keystroke, sharing
  `RefreshDebounce`'s timer mechanics; a debounced live pass defers to an in-flight refresh
  or submit rather than clobbering its field snapshot, re-arming instead of dropping the
  fields it had accumulated.
- `FormidableOptions.TrackFormValidity` (`bool`, default `false`) plus
  `IFormValidationEngine.IsFormValid` — an opt-in, invisible whole-form Submit-profile
  probe for disable-submit-button consumers; never writes to the message store or the
  pending indicator, raises `StateChanged` on flip, and never freezes on a faulting
  validator or loses a race against a submit's own, more authoritative verdict.
- `FormidableOptions.NormalizeOnSubmit` (`bool`, default `false`) — calls
  `INormalizableModel.Normalize()` before the submit profile, mirroring the AspNetCore
  filters' server-side behavior on the client.
- `FormidableOptions.NeverRegisteredFieldDiagnostic` (`Action<ValidationIssue>?`, default
  `null`) — fires only for a failing field whose registration has never existed since the
  engine was built, the signature of a disclosure gate whose `.When()` rule fails to
  mirror its field's render condition, distinct from the existing
  `SuppressedIssueDiagnostic` (registered-then-unregistered).
- `FormidableOptions.VerifyRowKeys` (`bool`, default `false`, Development builds) —
  throws when a field-bound component's resolved row identity no longer matches what it
  registered, catching a collection rendered without `@key`.
- `FormidableOptions.InlineMessageRole` (`string?`, default `null`) — when set, applies
  that ARIA role to every field- and collection-level message list, closing the
  summary-less announcement gap.
- `FormidableForm<TModel>.FocusFirstErrorOnInvalidSubmit` (`bool`, default `true`) — on a
  blocked submit, focuses the first error's element automatically, falling back to the
  first visible issue only when a blocked submit shows no error at all, so a warning
  above the failing field never collects the focus a refused submit owes the visitor. It
  also gates `FormidableForm.ApplyServerIssues` (both overloads), which focuses that same
  first error when the payload it applies carries one, since a rejected round trip is a
  blocked submit arriving late; a clean or advisory-only payload moves nothing, and
  `IFormValidationEngine.ApplyServerIssues` stays quiet, which is the path for a
  background apply. Set `false` to choose focus yourself from `OnInvalidSubmit`.
- `FormidableForm<TModel>.ResetAsync(TModel? newModel = null)` — returns a form to
  pristine. Omitted, rebuilds over the same model instance (touched/modified state,
  message store, advisory buckets, `HasSubmitted`, and any pending refresh all cleared).
  Supplied, carries out a model swap, now invoking a new `ModelChanged` callback so
  `@bind-Model` keeps a parent's own state in sync instead of silently reverting the swap
  on the parent's next render.
- `FormidableFieldContext.InputAttributes` — one dictionary bundling `id`, `class`,
  `aria-invalid`, and `aria-describedby` for a foreign control, replacing four separate
  attribute bindings with `@attributes="field.InputAttributes"`.
- `FormidableCultureBootstrap.ApplyStoredCultureAsync` (two overloads: a storage-read
  primary through `IJSRuntime`, and a delegate overload any host can drive without JS) —
  reads a stored culture, parses it with a fallback, and sets both default-thread cultures
  before a WASM host starts.
- `FieldState.HasInfos` — rounds out the severity trio (`HasWarnings`/`HasInfos`) a field
  state read already reported two-thirds of.
- `FormidableValidator<TModel>.Engine` and its two `ApplyServerIssues` forwarders — gives
  attach mode the same server round-trip surface `FormidableForm` already had.
- Typed `FormidableForm<TModel>.OnValidSubmit` (`EventCallback<SubmitOutcome>`) — the
  passing branch now carries the same outcome the invalid branch always did, so a valid
  submit that still carries advisories can read them; parameterless lambdas still bind.
- `FormidableInputSelect<TValue>` honors `UpdateOn.OnBlur` — commits on `change`, defers
  the validation notification to `blur`; `OnInput` coerces to `OnChange` since a
  `<select>` has no distinct input event.
- `FormidableSummary.Show` (`SummaryFilter`, default `All`) — renders one severity band
  instead of the combined list, so a page can put its errors and its advisories in different
  places; `Advisories` covers warnings and infos together, and a filter matching nothing
  renders nothing. Each summary computes its own announcement role from what it shows.
- `IFormValidationEngine.GetVisibleIssues()` reports issues in the document order of the
  fields that render them — so `FormidableSummary` lists them in reading order within each
  severity group, and a blocked submit's focus lands on the topmost problem.
  `FormidableForm` resolves that order after any render that changed its registered field
  set, by asking the new `IFormidableFieldOrderService`; a field with no element on the
  page sorts last, and until a resolve lands (or in `FormidableValidator`'s attach mode,
  which resolves none) the channel order stands: fault, submit errors, advisories, then
  live. The service is registered by `AddFormidableBlazor()` alongside the focus and
  DOM-sync services — a public interface over an internal JS-backed implementation for the
  same reason its two siblings are: a bUnit test substitutes a fake instead of standing up
  module interop.
- The scroll behind a focus move — a blocked submit's own, or a click on a
  `FormidableSummary` entry — prefers the field's own message list over the focus element,
  and aligns a target taller than 60% of the viewport to its top instead of centring it, so
  a collection-level issue shows the message that named the problem rather than the middle
  of the rows below it.
- A "Testing your forms" section in `docs/testing.md` (drive `IModelValidator<T>` with no
  Blazor; bUnit-render `FormidableForm` with recording doubles for the JS-backed
  services; async-flush guidance for pending-state assertions) and a "validate a nested
  object" recipe in `docs/recipes.md`.
- An explanation in `docs/async-validation.md` of why one edit after a submit runs a draft
  rule twice, with the consumer-side remedies: `LiveDebounce` for the live pass, and a
  worked value-keyed memoization inside the rule for a genuinely expensive check.

### Changed

- `FormidableSummary` renders `role="alert"` only when a visible issue is error-severity,
  and the politer `role="status"` otherwise (previously unconditional `role="alert"`),
  so an advisory-only, errors-free submit no longer interrupts as if blocking.
- `docs/quickstart.md` and the README's five-minute quickstart teach a tight three-file
  first form (`Program.cs`, `_Imports.razor`, one page with model, validator, and form
  together in its own `@code` block), with a bridging note on splitting into code-behind
  as a form grows; the samples themselves are unchanged.
- `FormidableComponentBase.Dispose()` now wraps `DisposeCore()` in `try`/`finally`, so a
  derived control's cleanup throwing still releases its field registration.

<!-- publish-day: verify -->
[Unreleased]: https://github.com/xfunc/formidable/commits/main
