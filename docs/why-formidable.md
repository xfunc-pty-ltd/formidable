# Why Formidable exists

Formidable exists because I got tired of building it badly, one workaround at a time.

The forms belonged to a client project — the kind where the business logic lives in the form:
sections that appear when you tick a box, rows you can add and delete, rules only the server
can settle. Blazor's `EditForm` handled the binding, FluentValidation handled the rules, and
the space between them was mine.

I filled it the same way every time. A manager class, to hold the lifecycle state no single
component owned. A second message store, then a dedup pass so the two stores would stop saying
the same thing twice. A reflection helper nobody loved, resolving FluentValidation's string
paths against the live object graph. Write that three times and you learn it isn't a
workaround: it's a missing layer, and you're paying for its absence in instalments.

So I wrote the layer once, with tests, and Formidable is what came out. It was born from a
deadline rather than a hobby afternoon, and it runs that client's forms today. Battle-tested on
exactly one real project: that's one more than a demo, and I'd rather give you the number than
imply a bigger one. The upside of a real project is that month three already happened, so the
sharp edges you'd normally find then are already filed off.

## The receipts

One row per workaround I stopped writing.

Each row names a pattern that shows up when you wire FluentValidation into Blazor's `EditForm`
by hand — the kind of integration code a form-heavy codebase accumulates one footgun at a time
(a footgun being an API that invites you to hurt yourself). Beside it is the Formidable
mechanism that answers that pattern natively. The patterns themselves are generic: they're what
anyone hand-rolling this integration is likely to reach for, and most of them were in my own
code first. **See it** links the sample page where you can watch the answer work. Every row is
held still by a test in the suite; a pattern with no shipped sample or test is left out rather
than padded in.

| Workaround pattern | Formidable mechanism | See it |
|---|---|---|
| A hand-written orchestration/manager class sitting between the form and the validator, owning lifecycle state no single component owns | The engine lives inside the root component (`FormidableForm`/`FormidableValidator`): there's no separate manager to construct, inject, or keep in sync | [quickstart](../samples/Formidable.Sample/Pages/Quickstart.razor) |
| A supplementary message store layered on top of the library's own, plus a dedup pass to stop the same issue appearing twice across the two stores | One single-writer `ValidationMessageStore`. Nothing to deduplicate, because there's only one store | — |
| Submit-only completeness rules that never render inline because the integration only ever runs one, default rule set | Validation profiles are first-class; the submit pipeline always runs the submit profile, so submit-only rules are just rules in that profile | [/profiles](../samples/Formidable.Sample/Pages/Profiles.razor) |
| A hand-rolled reflection helper resolving FluentValidation's string error paths against the live object graph | `IModelIntrospector`: one cached, tested path resolver, shipped in the core package | — |
| A per-form strategy class hard-coding which fields' errors are currently allowed to be visible, mirroring the markup's conditional rendering by hand | Render-registration disclosure. Whether a submit shows a field's error is a side effect of something having registered it while mounted, so there's no separate strategy to write or keep in sync with the markup | [/disclosure](../samples/Formidable.Sample/Pages/Disclosure.razor) |
| Submit-time error tracking keyed by positional path string, so deleting or reordering collection rows can misattribute an error to the wrong row | Every field a submit discloses is keyed by object instance, not index or path string, so reordering or deleting rows can't relocate an error | [/collections](../samples/Formidable.Sample/Pages/Collections.razor) |
| Custom render-dispatch / `SynchronizationContext` plumbing to make sure a debounced or async validation result actually triggers a render | The root component's own `InvokeAsync` marshals every validation-triggered update: no dispatcher delegate to write or probe for | [/async](../samples/Formidable.Sample/Pages/AsyncRules.razor) |
| A scoped/injected manager whose lifetime outlives one form, needing an explicit "begin a new session" reset call between forms | Engine lifetime equals component lifetime. Construction and `Dispose()` are the whole story; nothing to reset by hand | — |
| A `RuleSets` (or similar) parameter on the validator-attachment component that has to be kept consistent with whatever the live/typing-time validation pass runs, or the two start fighting each other | Nothing to keep consistent: the live channel follows the submit profile unless a form deliberately narrows it, so there is one setting rather than two that can drift apart, and it lives on the engine's options rather than on a component's markup | — |
| A parameter threaded through wrapper component after wrapper component so a nested selection control's validation message can find the right field identifier | `FormidableField`/`FormidableInputBase` resolve and register their own field: there's no identifier to thread through anything wrapping them | [/foreign](../samples/Formidable.Sample/Pages/ForeignControl.razor) |
| Manually creating, and re-creating, the `EditContext` whenever the bound model instance is swapped (draft load, reset) | `FormidableForm` owns the `EditContext`; swapping the `Model` parameter rebuilds it and resets validation state automatically | — |
| Bespoke wiring to make sure a model's "clear the fields that don't apply anymore" cleanup step actually runs before every save and is re-enforced server-side | `INormalizableModel.Normalize()`: one hook, enforced at the server boundary before validation by both adapters, and on the client by `FormidableOptions.NormalizeOnSubmit` when you want the submit pass to run it too. Call it yourself before a lenient draft save, which doesn't go through that pass | [/server](../samples/Formidable.Sample/Pages/ServerRoundTrip.razor) |
| Hand-assembling a distinct, human-readable field list for an error dialog or summary, separately from whatever the inline messages already computed | `SubmitOutcome.VisibleErrorSummary`: distinct display names for the currently-visible errors, ready for a dialog or summary | — |
| A CSS-class extension method reimplemented per project to decide a field's invalid/valid styling from its validation state | `FormidableCss.Compute` plus the `FieldCssClassProvider` bridge: one rule, shared by every Formidable input and by plain `InputBase` descendants alike | — |
| A bespoke component for rendering a collection-level rule's message (e.g. "at least one row is required"), separate from the per-field message component | `<FormidableCollectionMessage For="...">` registers the collection's own path for disclosure and renders its issues, with the same rendering as a field message | [/collections](../samples/Formidable.Sample/Pages/Collections.razor) |
| A server-rejected save landing in a generic error dialog because there's no path from the response body back to the specific fields that failed | The server-error round trip: deserialize the response — [inside a guard](server-integration.md#reading-the-rejection-body), since a 400 can come from a proxy or a gateway rather than from the endpoint — and hand it to the form's `ApplyServerIssues(...)`. The exact fields the server rejected light up inline | [/server](../samples/Formidable.Sample/Pages/ServerRoundTrip.razor) |
| A submit handler that can be bypassed: a toolbar button, keyboard shortcut, or second call site that runs the validator directly instead of routing through the same gate the form's own submit uses, so two code paths can disagree about whether submission may proceed | `FormidableForm.SubmitAsync()` is the one submit pipeline: it's both the handler wired to the rendered `EditForm`'s `OnSubmit` and a public method any other trigger can call directly, so there's only ever one gate to go through | [quickstart](../samples/Formidable.Sample/Pages/Quickstart.razor) |
| A hand-rolled base validator class with an enum or string flag selecting which rule subset to run, reinvented per project because FluentValidation has no first-class notion of "which lifecycle stage is this" | `DraftSubmitValidator<T>` ships that base class once, over `ProfiledValidator<T>`, with `ValidationProfile` as the selector: nothing to reinvent per project | [/profiles](../samples/Formidable.Sample/Pages/Profiles.razor) |

Every mechanism above has a deep dive of its own when you want the full picture:
[progressive disclosure](disclosure.md), [collections and row
identity](collections-and-row-identity.md), [server integration](server-integration.md), and the
rest of the list in the [README](../README.md#documentation).

Those land better once you've built something with it, though. Start with a working form.

**Next:** [Quickstart](quickstart.md)
