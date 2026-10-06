# The engine and its results

**You should already know:** the two roots and the context they cascade to the components inside
them ([Component kit](component-kit.md)), and what a submit shows and enforces
([Disclosure](disclosure.md)).

This page lists the members of three types: `IFormidableEngine`, the form's engine;
`ValidationReport`, what a validator answers; and `SubmitOutcome`, what a submit answers. Each row
says what the member answers and, where another page teaches it, links that page.

## Need to know

| Type | Package | Where a page gets one |
|---|---|---|
| [`IFormidableEngine`](#iformidableengine) | `Formidable.Blazor` | `Engine` on `FormidableForm` or `FormidableValidator` (`null` until the engine is built; once the root is disposed, the form's still returns its last engine, disposed, and the validator's returns `null`), or `Engine` on the cascaded `FormidableFormContext` (`context.Engine` inside either root's `ChildContent`). |
| [`ValidationReport`](#validationreport) | `Formidable` | `SubmitOutcome.Report`, `IModelValidator<TModel>.ValidateAsync`, and on the server `GetFormidableValidationReport()`. |
| [`SubmitOutcome`](#submitoutcome) | `Formidable.Blazor` | `FormidableForm.SubmitAsync()`, `OnValidSubmit`, `FormidableInvalidSubmitContext.Outcome`, `FormidableValidator.ValidateForSubmitAsync()`, and the engine's own `ValidateForSubmitAsync`. |

## `IFormidableEngine`

The engine as a page and the kit's components see it: field state, the issues showing, submit,
loaded values, server issues and two events. Both roots build the shipped engine. Call
`ValidateForSubmitAsync`, `DiscloseLoadedValuesAsync` and `ApplyServerIssues` from the renderer's
synchronization context.

The engine's own `ValidateForSubmitAsync` and `ApplyServerIssues` move no focus. For a blocked
submit that focuses its first error, submit through the root: `FormidableForm.SubmitAsync()` or
`FormidableValidator.ValidateForSubmitAsync()`
([Where does focus go on a blocked submit?](css-and-accessibility.md#where-does-focus-go-on-a-blocked-submit)).
To make that move yourself, call `FocusFirstErrorAsync()` on either root
([Asking for the first-error move](component-kit.md#asking-for-the-first-error-move)).

Implementing the interface is supported, and a test double cascaded in the engine's place is the
expected shape for a component test. A member added later carries a default implementation, so a
double written today keeps compiling ([Testing](testing.md#the-form-under-bunit)).

| Member | Type | What it answers |
|---|---|---|
| `EditContext` | `EditContext` | The Blazor `EditContext` the engine writes its messages to. |
| `Registry` | `FieldRegistry` | The registry each rendered field joins, which decides which fields a submit may disclose ([Disclosure](disclosure.md#why-did-it-appear-when-i-pressed-submit)). |
| `Options` | `FormidableOptions` | The options instance the engine was built with ([`FormidableOptions` is read once](options.md#formidableoptions-is-read-once)). |
| `IsValidating` | `bool` | Whether the form is mid-check (a live check, a submit, a load of values or the whole-form re-check), whichever field started it: the flag for a form-wide spinner. The validity check `TrackFormValidity` runs does not set it. One field's flag is `GetFieldState(field).IsValidating` ([Where does "checking" show?](async-validation.md#where-does-checking-show-and-where-doesnt-it)). |
| `HasSubmitted` | `bool` | Whether a submit has answered or a server reply has been applied. After either, every later edit is followed by a whole-form re-check once [`RefreshDebounce`](options.md#refreshdebounce) has passed ([Does applying a reply count as a submit?](server-integration.md#does-applying-a-reply-count-as-a-submit)). |
| `IsFormValid` | `bool` | Whether the form would pass the submit profile, kept current only while [`TrackFormValidity`](options.md#trackformvalidity) is on. With tracking off it keeps its last answer, `false` on a form that has never tracked; with it on, `false` until a whole-form check has answered once. An edit made while a submit or a load of values ran is answered by the edit's own check, not by theirs. Issues a server applied are not part of the answer. The first read with tracking off, once per form build, writes a logged warning and a `Trace` line naming the option. |
| `StateChanged` | `EventHandler<FormidableStateChangedEventArgs>` | Raised whenever validation or field state changes. The sender is the engine and the arguments carry no detail. A page that renders engine state itself subscribes to re-render ([CSS and accessibility](css-and-accessibility.md#why-did-my-own-aria-invalid-vanish-from-a-native-inputtext)). |
| `ValidationFaulted` | `EventHandler<FormidableValidationFaultedEventArgs>` | Raised when a check nothing awaits throws: a live check, the whole-form re-check or the validity check. A submit or a load of values throws to its caller instead, and a cancellation is not a fault. A host logs the exception here ([`ValidationFaultMessage`](options.md#validationfaultmessage) has what the form shows). |
| `GetFieldState(FieldIdentifier)` | `FieldState` | One field's current state: touched, modified, checking, the severities it carries, and whether it would pass submit ([What puts green on a field?](css-and-accessibility.md#what-puts-green-on-a-field)). |
| `GetFieldRequirement(FieldIdentifier)` | `FieldRequirement` | How firmly the submit profile's rules demand a value, as the required indicator and `aria-required` render it; [`RequiredOverride`](options.md#requiredoverride) answers first. `NotRequired` means no presence rule that fails as an error was found, not that the field is proven optional ([Which ARIA attributes does an input get?](css-and-accessibility.md#which-aria-attributes-does-an-input-get-and-when)). |
| `GetIssues(FieldIdentifier)` | `IReadOnlyList<ValidationIssue>` | The issues showing for one field at every severity, computed on each call: its submit errors, then its advisories and its live messages less any message already listed. Two submit errors that share a message are both listed. A standing [fault message](options.md#validationfaultmessage) comes last, on the model-level field. |
| `GetVisibleIssues()` | `IReadOnlyList<VisibleIssue>` | Every issue showing across the form, computed on each call. Each `VisibleIssue` carries its `Field`, its `Issue` and the `DisplayName` it is listed under ([Deciding what an entry says](component-kit.md#deciding-what-an-entry-says)). The list follows the page's reading order once the root has resolved one ([The order entries appear in](component-kit.md#the-order-entries-appear-in)). |
| `MarkTouched(FieldIdentifier)` | `void` | Marks the field touched, which lets its state class paint, without reporting a value change, so no check starts. A committed change marks it touched too ([`FormidableField<TValue>` and `FormidableFieldContext`](component-kit.md#formidablefieldtvalue-and-formidablefieldcontext)). |
| `ValidateForSubmitAsync(CancellationToken)` | `Task<SubmitOutcome>` | Validates the whole model under the submit profile, discloses what is on screen, and answers whether the submit may proceed ([`SubmitOutcome`](#submitoutcome)). Under [`NormalizeOnSubmit`](options.md#normalizeonsubmit) the model is normalized first. When a newer submit or load answers in its place, the call reports blocked, with nothing shown. A token cancelled before the check answered throws `OperationCanceledException`. |
| `DiscloseLoadedValuesAsync(CancellationToken)` | `Task` | Says what the values already in the model have earned: a field holding a value shows what the submit profile says of it (valid, or its message), and a field holding nothing stays silent. A field rendered with [`WaitForSubmit`](options.md#waitforsubmit-per-component-not-a-formidableoptions-property) keeps its message back until a submit or server reply answers, and a passing value still shows valid. A cancelled call discloses nothing ([Saying what loaded values have earned](component-kit.md#saying-what-loaded-values-have-earned)). |
| `ApplyServerIssues(IEnumerable<ValidationIssue>)` | `void` | Applies a server reply as the server's current answer, replacing the previous reply: an error lands whether or not its field is rendered, an advisory only where a submit could show it. It sets `HasSubmitted` and moves no focus, where `FormidableForm.ApplyServerIssues` treats a reply carrying an error as a blocked submit and moves focus while `FocusFirstErrorOnInvalidSubmit` is on, as it is by default ([Why did focus move when I applied the reply?](server-integration.md#why-did-focus-move-when-i-applied-the-reply)). The reply stands until the next submit or load of values, or until the form is re-checked after a later edit or a change in which fields render ([What happens to a server error when I edit the field?](server-integration.md#what-happens-to-a-server-error-when-i-edit-the-field)). |

## `ValidationReport`

What a validator answers for one profile. It lives in the core `Formidable` package, so a contracts
assembly or a server reads it without Blazor. Every list keeps issue order, and the severity lists
are fixed when the report is built. [The rules, without Blazor](testing.md#the-rules-without-blazor)
reads one in a test, and [Severity](severity.md#does-a-warning-or-an-info-block-the-submit) has
what counts as valid.

| Member | Type | What it answers |
|---|---|---|
| `ValidationReport(IReadOnlyList<ValidationIssue>)` | constructor | Builds a report over the issues, splitting them by severity once. The report keeps the list handed in, so a later change to that list shows in `Issues` alone, never in the severity lists or `IsValid`. A `null` list throws `ArgumentNullException`. |
| `Empty` | `ValidationReport` (static) | A shared report with no issues. |
| `Issues` | `IReadOnlyList<ValidationIssue>` | Every issue, in validator order. |
| `IsValid` | `bool` | `true` when the report holds no error-severity issue. Warnings and infos do not affect it. |
| `Errors` | `IReadOnlyList<ValidationIssue>` | The error-severity issues, in issue order. |
| `Warnings` | `IReadOnlyList<ValidationIssue>` | The warning-severity issues, in issue order. |
| `Infos` | `IReadOnlyList<ValidationIssue>` | The info-severity issues, in issue order. |
| `Advisories` | `IReadOnlyList<ValidationIssue>` | Every issue that is not an error, in issue order: warnings, infos, and any severity outside the named members. It is the set the server writes to a problem response's `advisories` extension, where an issue whose severity no member defines is refused ([The server-side mapper](server-integration.md#the-server-side-mapper)). |

## `SubmitOutcome`

What a submit answers: whether it may go ahead, the report behind that, and the names of the errors
a blocked submit left showing. `FormidableForm.SubmitAsync()` routes on `CanProceed`, to
`OnValidSubmit` or `OnInvalidSubmit`
([Severity](severity.md#does-a-warning-or-an-info-block-the-submit)). It is a record that grows by
init-only properties, never by constructor parameters, so a test that builds one keeps compiling.

| Member | Type | What it answers |
|---|---|---|
| `CanProceed` | `bool` | `true` when the submit finished with a report holding no error; warnings and infos do not block. `false` for a blocked submit, and for one displaced by a newer submit, a load of values, or the form being rebuilt or torn down, whatever its report holds. |
| `Report` | [`ValidationReport`](#validationreport) | The submit profile's full report, advisories included, so a submit that proceeds still carries its warnings here. A displaced submit's report is empty when the check honoured the cancellation, and its own otherwise. |
| `VisibleErrorSummary` | `IReadOnlyList<string>` | For a blocked submit, the names its showing errors are listed under, each name once and never the messages, for a dialog or a summary line. An error on a field is listed under the name its rule gives the field (`WithName(...)`, or FluentValidation's own) when that name has text, else under its path; one that names no field, under [`ModelLevelDisplayName`](options.md#modelleveldisplayname). The submit's own errors come first, then a server reply's still standing on fields its own errors do not name. Empty for a submit that proceeds or was displaced. The rule is the one `VisibleIssue.DisplayName` follows, and a later change to `ModelLevelDisplayName` does not rename an outcome already handed back. |
