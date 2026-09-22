# Engine options

**You should already know:** the live/submit split and why one validator serves both moments
([Profiles](profiles.md)), and the whole-form re-check a submitted form runs after every further
edit ([Async validation](async-validation.md)).

Every setting the engine reads lives on one class, `FormidableOptions`, handed to a form through
`FormidableForm<TModel>`'s `Options` parameter. This page catalogs each property, then `UpdateOn`,
the read-once rule, and app-wide registration.

## Need to know

Every property has a default, so a form that passes no `Options` at all is fully configured. It
falls back to the app-wide default, or to `new FormidableOptions()` where none is registered (see
[App-wide defaults](#app-wide-defaults)).

An option is set in one of two places: once for the whole app, in `Program.cs`, with
`builder.Services.AddFormidableBlazor(options => …)`, or per form, on the `FormidableOptions`
instance the `Options` parameter takes (`FormidableForm` and `FormidableValidator` alike). The
two do not merge: a form's own instance wins outright.

```razor
<FormidableForm Model="_request" Options="_options" OnValidSubmit="HandleValid"
                @ref="_form">
```

<!-- Excerpt from `samples/Formidable.Sample/Pages/Disclosure.razor` -->

Any attribute `FormidableForm<TModel>` does not recognise is splatted onto the `<form>` it renders.
[Component kit](component-kit.md#formidableformtmodel) has the four positions the form's own
attributes take against the splat.

One rule governs the rest: `FormidableForm<TModel>` builds its engine once per `Model` instance and
passes `Options` into the constructor there. A whole new `FormidableOptions` instance without a new
`Model` throws (see [`FormidableOptions` is read once](#formidableoptions-is-read-once)).

The engine does keep re-reading that instance's *properties*. Mutating one takes effect at that
property's next read (a check choosing its profile, a debounce window opening, a render asking
for a class name). A change notifies nothing by itself; it shows when something next validates or
renders.

Where an entry below states a coarser read it governs: [`ClickRecovery`](#clickrecovery) is read
once per root, at its first interactive render, and [`VerifyRowKeys`](#verifyrowkeys) and
[`ReportStaleRegistrations`](#reportstaleregistrations) once per bound component as it binds.

| Option | Type | Default | What it decides |
|---|---|---|---|
| [`LiveProfile`](#liveprofile) | `ValidationProfile?` | `null` (tracks `SubmitProfile`) | Which profile live checking runs. |
| [`SubmitProfile`](#submitprofile) | `ValidationProfile` | `ValidationProfile.Submit` | Which profile a submit, the whole-form re-check, a load of values and `TrackFormValidity` run. |
| [`RefreshDebounce`](#refreshdebounce) | `TimeSpan` | 300 ms | After a submit, how long after an edit before the whole form is re-checked. |
| [`LiveDebounce`](#livedebounce) | `TimeSpan?` | `null` (immediate) | How long after a field change before rules run, instead of at once. |
| [`TrackFormValidity`](#trackformvalidity) | `bool` | `false` | Keeps `IsFormValid` current with a whole-form validity check. |
| [`NormalizeOnSubmit`](#normalizeonsubmit) | `bool` | `false` | Whether a submit calls `Normalize()` first. |
| [`ClickRecovery`](#clickrecovery) | `DisplacedClickRecovery` | `Buttons` | Whether a click the page displaced is re-delivered. |
| [`DisclosureOverride`](#disclosureoverride) | `Func<ValidationIssue, bool?>?` | `null` | A per-issue answer to whether an issue may be shown. |
| [`RequiredOverride`](#requiredoverride) | `Func<FieldIdentifier, FieldRequirement?>?` | `null` | Declares a field required, or not, where the rules cannot say. |
| [`ShowRequiredIndicators`](#showrequiredindicators) | `bool` | `true` | Whether the required marker renders at all. |
| [`RequiredIndicatorContent`](#requiredindicatorcontent) | `string` | `"*"` | What a drawn marker holds. |
| [`LiveDisclosure`](#livedisclosure) | `LiveIssueDisclosure` | `Engaged` | Whether a live issue also waits for its field to render. |
| [`SuppressedIssueDiagnostic`](#suppressedissuediagnostic) | `Action<ValidationIssue>?` | `null` | Callback for an issue a reporting site did not show. |
| [`NeverRegisteredFieldDiagnostic`](#neverregisteredfielddiagnostic) | `Action<ValidationIssue>?` | `null` | Callback for the narrower half: a field never rendered. |
| [`StaleRegistrationDiagnostic`](#staleregistrationdiagnostic) | `Action<StaleRegistrationReport>?` | `null` | Callback for a stale registration, once detection is on. |
| [`VerifyRowKeys`](#verifyrowkeys) | `bool` | `false` | Throws when a component stops speaking for its field. |
| [`ReportStaleRegistrations`](#reportstaleregistrations) | `bool` | `false` | Reports that divergence instead of throwing. |
| [`InlineMessageLive`](#inlinemessagelive) | `string?` | `null` (no attribute) | The `aria-live` politeness every message list carries. |
| [`DefensiveGateMessage`](#defensivegatemessage) | `string` | `"The form cannot be submitted because information that is not currently displayed is invalid."` | The sentence the all-suppressed defensive gate carries. |
| [`ModelLevelDisplayName`](#modelleveldisplayname) | `string` | `"This form"` | The name a fieldless error is listed under. |
| [`ValidationFaultMessage`](#validationfaultmessage) | `string` | `"Validation could not run to completion; recent changes may not be fully validated."` | The form-level message shown when a live check or the whole-form re-check throws. |
| [`OrderIssues`](#orderissues) | `Func<IReadOnlyList<FieldIdentifier>, IReadOnlyList<FieldIdentifier>>?` | `null` (document order) | Re-sorts the order visible issues are reported in. |
| [`CssClasses`](#cssclasses) | `FormidableCssClasses` | a new instance | The five field-state class names. |

## Properties

### `LiveProfile`

`ValidationProfile?`, defaults to `null`. Which rules run as you edit: the profile every live
check validates against. `null` means `SubmitProfile`, so a live message says what a submit would
actually complain about, presence rules included. It is read as each live check begins and follows
the instance the options hold, so a runtime swap takes effect at the next check.

Set it for the whole app with
`builder.Services.AddFormidableBlazor(options => options.LiveProfile = ValidationProfile.Draft);`.

Reach for it where a submit rule is genuinely too expensive to run per change;
`ValidationProfile.Draft` is the usual answer. A validator that declares no ruleset (a plain
`AbstractValidator<T>`) has only default rules, which `Draft` selects, so
`LiveProfile = ValidationProfile.Draft` holds nothing back until the rules that should wait sit in
a ruleset
([validate while typing, on blur, or only at submit](recipes.md#i-want-to-validate-while-typing-on-blur-or-only-at-submit)).

[Profiles](profiles.md) has why narrowing is the blunter of the two levers that keep a live channel
from nagging, and why a save-progress flow is unaffected either way. Why:
[how the engine works: what starts each check](how-the-engine-works.md#the-five-pass-kinds).

**Recipe:**
[narrow what the live channel validates](recipes.md#i-want-to-narrow-what-the-live-channel-validates).

### `SubmitProfile`

`ValidationProfile`, defaults to `ValidationProfile.Submit`. The profile a submit validates
against, the profile the whole-form re-check runs, the profile
`IFormidableEngine.DiscloseLoadedValuesAsync` runs, and the profile `TrackFormValidity`'s validity
check answers for. Unless `LiveProfile` narrows it, every live check validates against it too. See
[Profiles](profiles.md).

Set it for the whole app with
`builder.Services.AddFormidableBlazor(options => options.SubmitProfile = ValidationProfile.Named("Approve", includeDefaultRules: true, ValidationProfile.SubmitRuleSetName, "Approve"));`.

### `RefreshDebounce`

`TimeSpan`, defaults to 300 ms. After a submit, how long after an edit before the whole form is
re-checked, so what the submit showed stays truthful: the messages on screen follow your fixes, and
the `Valid` class stays honest, with no second submit.

Set it for the whole app with
`builder.Services.AddFormidableBlazor(options => options.RefreshDebounce = TimeSpan.FromMilliseconds(500));`.

Before the first submit or server reply, an edit gets one live check, which answers every field
you have engaged, and no whole-form re-check follows it. A change to which fields are on screen (a
row leaving, a section collapsing) re-checks the whole form after the same wait at any point in
the form's life.

It is a second debounce because the re-check is whole-form work, too much to repeat on every
keystroke. The wait is the same whether or not `LiveDebounce` is set, and a burst of edits or
field-set changes inside it is one re-check, since they share one timer. This timer and
`LiveDebounce`'s both come from the DI container's `TimeProvider` where one is registered, so a
test can land either window with `Advance` (see [Testing](testing.md#faking-the-clock)).

[Async validation](async-validation.md#why-did-the-summary-change-a-moment-after-i-fixed-a-field)
has what the re-check waits for. Why:
[how the engine works: what starts each check](how-the-engine-works.md#the-five-pass-kinds).

### `LiveDebounce`

`TimeSpan?`, defaults to `null`: a field change starts its live check at once. Set it and a change
opens a wait instead. Another change inside the wait restarts it, and when it passes quietly one
check runs for every field changed since it opened (one shared wait for the form, not one per
field), with the "checking" cue on those fields alone.

Reach for it when live rules are expensive enough that one per keystroke is the wrong trade. If
one field's rule is the expensive one, leave `LiveDebounce` unset and let that input commit on
`change` (the `UpdateOn` default) instead. A change to any other field then starts its check at
once ([stop the check firing on every keystroke](async-validation.md#how-do-i-stop-the-check-firing-on-every-keystroke)),
and a memo answers the expensive rule's repeat asks
([remember its answer](recipes.md#i-want-a-slow-async-check-to-remember-its-answer)).

With `TrackFormValidity` on, its validity check waits for the same window, and after a submit the
same edit starts the whole-form re-check's own wait as well.

Set it once for the whole app in `Program.cs`,
`builder.Services.AddFormidableBlazor(options => options.LiveDebounce = TimeSpan.FromMilliseconds(400));`,
or per form, on the `FormidableOptions` the form's `Options` parameter takes
([App-wide defaults](#app-wide-defaults)).

The two waits run independently;
[Async validation](async-validation.md#why-did-the-summary-change-a-moment-after-i-fixed-a-field) has what setting one wider
costs. Why: [how the engine works: the two timers](how-the-engine-works.md#the-five-pass-kinds).

**Sample:** [`/async`](../samples/Formidable.Sample/Pages/AsyncRules.razor) — a checkbox swaps the
immediate default for a 400 ms window.

### `TrackFormValidity`

`bool`, defaults to `false`. Keeps `IFormidableEngine.IsFormValid` current (the answer a disabled
Submit button needs) with a whole-form validity check by the submit profile: once when the form is
built, then on every field change, or once per window when `LiveDebounce` is set. It shows no
message and no "checking".

Off by default because a form with nothing reading `IsFormValid` gets nothing for the work. On a
validator the engine can take rule by rule, where every rule answers without waiting, tracking adds
no rule executions per edit on the default profiles: whichever of the validity check and the check
your edit started runs first has answered by the time the other looks, and the other reuses those
answers.

It costs extra where `LiveProfile` narrows (the rules the live check skipped still run on every
edit) and on any other validator, where each validity check is one whole `SubmitProfile`
validation on top of the live check.
[Async validation](async-validation.md#what-does-trackformvalidity-cost-with-async-rules) has the
async-rule cost and what happens when several validity checks overlap.

No option narrows the validity check the way `LiveProfile` narrows the live check. An async rule
it reaches (a lookup against your API, say) wants a memo (`MustAsyncMemoized`,
[remember its answer](recipes.md#i-want-a-slow-async-check-to-remember-its-answer)). The
alternative is a button bound to nothing, with tracking off: it stays enabled and Submit blocks on
an error.

```razor
<button type="submit" disabled="@(_form?.Engine?.IsFormValid != true)">Submit</button>
```

With tracking off `IsFormValid` keeps its last answer, `false` on a form that has never tracked; with
it on, `false` until a whole-form check has answered once (a validity check, a submit, a load, or the
whole-form re-check). Issues a server applied through `ApplyServerIssues` are not part of the answer.

Tracking also feeds the `Valid` class, so it can put green on a field a narrowed live check could
not ([CSS and accessibility](css-and-accessibility.md#what-puts-green-on-a-field)). Why:
[how the engine works: what `TrackFormValidity` runs](how-the-engine-works.md#the-trackformvalidity-probe).

**Sample:** [`/field-state`](../samples/Formidable.Sample/Pages/FieldStateVisualizer.razor) — a
Submit button disabled until the validity check says yes.

### `NormalizeOnSubmit`

`bool`, defaults to `false`. When `true` and the model implements `INormalizableModel`, a submit
calls `model.Normalize()` in place before running `SubmitProfile`, so the profile judges the
cleaned values rather than whatever was typed. It mirrors the ASP.NET Core validation filters, and
it is the only automatic client-side invocation there is.

The mutation happens before the submit's rules run, so the submit's own re-render repaints every
bound input straight from the normalized model, with no extra wiring. Calling `model.Normalize()`
yourself takes one more step this option does for free: the engine starts a live check only when it
hears `EditContext.NotifyFieldChanged`, so name each field the mutation changed.

**Sample:** [`/normalize`](../samples/Formidable.Sample/Pages/Normalize.razor) — a checkbox, and a
Submit button that never calls `Normalize()` itself.

### `ClickRecovery`

`DisplacedClickRecovery`, defaults to `DisplacedClickRecovery.Buttons`. Whether the root re-delivers
a click the page moved out from under a still pointer between the press and the release (the submit
a message appearing above the button swallows).

Set it for the whole app with
`builder.Services.AddFormidableBlazor(options => options.ClickRecovery = DisplacedClickRecovery.None);`.

Under the default the root recovers clicks on buttons inside it.
[Component kit](component-kit.md#the-click-a-disclosure-displaces) has the shift itself, the three
conditions recovery holds out for, what the recovered click is, and what a root that cannot scope a
guard does instead.

`DisplacedClickRecovery.None` installs no guard at all, for a page that deliberately moves its own
controls during a press.

This option is read once per root, at its first interactive render. Recovery needs the library's own
script, so a host that cannot load it recovers nothing.

### `DisclosureOverride`

`Func<ValidationIssue, bool?>?`, defaults to `null`. Consulted wherever the engine decides whether
an issue may be shown: return `true` to answer yes for an issue whose field nothing renders, `false`
to answer no, or `null` to defer to the field registry. Model-level issues (an empty `Path`)
resolve to the form's own element, which counts as rendered for as long as the form is on the page,
so deferring leaves them visible.

Set it for the whole app with
`builder.Services.AddFormidableBlazor(options => options.DisclosureOverride = issue => issue.Path.StartsWith("Shipping.") ? true : null);`.

An answer is an input to the asking channel's own disclosure rule rather than a per-issue switch
over what is on screen, and the live channel consults it only under
`LiveIssueDisclosure.EngagedAndVisible` (see [`LiveDisclosure`](#livedisclosure)). See
[Disclosure](disclosure.md#disclosureoverride-the-escape-hatch) for what each channel makes of one.

### `RequiredOverride`

`Func<FieldIdentifier, FieldRequirement?>?`, defaults to `null`. Consulted before the validator's
own rules are read: return a `FieldRequirement` to declare a field's requiredness, or `null` to
defer to the rules.

Set it for the whole app with
`builder.Services.AddFormidableBlazor(options => options.RequiredOverride = field => field.FieldName == "Reason" ? FieldRequirement.Required : null);`.

Reading rules sees presence only as FluentValidation's own `NotEmpty()` or `NotNull()`, so presence
written as a predicate answers `FieldRequirement.NotRequired`, as does every field of an
uninspectable validator. `NotRequired` means "not known to be required", never "proven optional".
A presence rule under a `When` or `Unless` answers `ConditionallyRequired`, which, like
`NotRequired`, draws no mark; return `Required` here to mark the field anyway.

It declares in both directions: `Required` marks a field the rules cannot be read to demand,
`NotRequired` unmarks one they can. The marker and `aria-required` are read from that one answer
rather than decided apart. Invoked on every ask (once per bound component per render) so keep it
cheap and pure.

The delegate receives the field's identifier alone, so a lambda that reads the condition off your
model, `field => field.FieldName == nameof(Booking.Company) && _booking.WantsInvoice ?
FieldRequirement.Required : null`, makes the mark follow a checkbox bound to that flag at the
page's next render.
Or render your own marker from `FormidableFieldContext.Requirement` inside a `FormidableField`,
which can say something for the conditional case as well.

**Recipe:**
[mark fields required when the rules cannot say so](recipes.md#i-want-to-mark-fields-required-when-the-rules-cannot-say-so).

### `ShowRequiredIndicators`

`bool`, defaults to `true`. Whether
[`FormidableRequiredIndicator`](component-kit.md#formidablerequiredindicatortvalue) (the asterisk
beside a required field's label or legend in the samples) renders at all. Set it to `false` to
render no marker anywhere on the form: no element, not an empty one. That is the form-wide off
switch for a design that marks the optional fields instead.

Off means off for everything the indicator might ever render. What it never suppresses is
`aria-required`: whether a value is demanded is a fact about the input rather than a decoration. For
a marker drawn entirely in CSS, leave the switch on and empty the content instead — next.

### `RequiredIndicatorContent`

`string`, defaults to `"*"`. The content
[`FormidableRequiredIndicator`](component-kit.md#formidablerequiredindicatortvalue) renders inside
its marker for a required field. It decides what a drawn marker holds, never whether one is drawn.
That is `ShowRequiredIndicators`, above.

Set it to `""` for a marker drawn entirely in CSS: the marker element still renders, empty, which is
what a stylesheet's `::before`/`::after` needs to land on. The library ships no styling, so this is
the text inside the marker's `formidable-required` element and nothing else.

### `LiveDisclosure`

`LiveIssueDisclosure`, defaults to `LiveIssueDisclosure.Engaged`. Which of an engaged field's live
issues the live channel discloses (the one lever over a channel registration never touches).

Set it for the whole app with
`builder.Services.AddFormidableBlazor(options => options.LiveDisclosure = LiveIssueDisclosure.EngagedAndVisible);`.

Under the default, engagement alone discloses: a field a committed change has named, or one a draft
load adopted, shows its live issues on every surface, rendered or not.
[Disclosure](disclosure.md#why-isnt-my-message-showing-yet) has why that default exists and what
departure means. Departure is the one thing registration decides here.

`LiveIssueDisclosure.EngagedAndVisible` gates each live issue on the same override-aware visibility
the submit channel consults, message store included. Reach for it when a page deliberately notifies
unrendered fields and would rather they stayed quiet until they appear. To list less without
changing disclosure, use [`FormidableSummary`'s `Show`](component-kit.md#showing-one-severity-band)
instead.

Why: [how the engine works: what the live channel discloses](how-the-engine-works.md#the-live-view).

### `SuppressedIssueDiagnostic`

`Action<ValidationIssue>?`, defaults to `null`. Invoked once per issue one of two reporting sites
decided not to show: no rendered field registration matched it, or a `DisclosureOverride` answered
`false`. Those sites are a submit, for its own error-severity issues on unwatched fields, and
`ApplyServerIssues`, for the advisories a server response's visibility answer hides.

Set it for the whole app with
`builder.Services.AddFormidableBlazor(options => options.SuppressedIssueDiagnostic = issue => Console.WriteLine($"Suppressed: {issue.Path}"));`.

Those two are the whole of what reaches this callback.
[Disclosure](disclosure.md#disclosureoverride-the-escape-hatch) has what a visibility answer hides
in silence, and the channels that record a suppression whether or not this callback is set.

The issue carries the response body's own strings on the `ApplyServerIssues` route. The library
neutralizes control characters in the path and bounds its length; telemetry that writes `issue.Path`
owes it the same.

### `NeverRegisteredFieldDiagnostic`

`Action<ValidationIssue>?`, defaults to `null`. Invoked alongside `SuppressedIssueDiagnostic`, for
the narrower half of what it reports: a suppressed issue whose field has no registration history at
all. It receives the same issue, response strings and all, so what that entry says about writing
`Path` into a log applies here unchanged.

Set it for the whole app with
`builder.Services.AddFormidableBlazor(options => options.NeverRegisteredFieldDiagnostic = issue => Console.WriteLine($"Never rendered: {issue.Path}"));`.

That is the signature of a rule whose `.When(...)` fails to mirror the `@if` gating its field, so
the rule can fail in a state the field never renders in. It is *also* the signature of a correct
section the visitor has not opened yet, and this signal cannot tell the two apart, so treat it as a
place to look rather than a verdict. A visited-then-collapsed section stays silent here.

### `StaleRegistrationDiagnostic`

`Action<StaleRegistrationReport>?`, defaults to `null`. Invoked once per stale registration
[`ReportStaleRegistrations`](#reportstaleregistrations) detects. The report carries the component's
type and both ends of the divergence: the field it registered, and the one its accessor names now.

Set it for the whole app with
`builder.Services.AddFormidableBlazor(options => options.StaleRegistrationDiagnostic = report => Console.WriteLine($"Stale: {report.RegisteredField.FieldName} is now {report.CurrentField.FieldName}"));`.

Detection is `ReportStaleRegistrations`' to switch on; while that is off nothing reaches this
callback, and with [`VerifyRowKeys`](#verifyrowkeys) on the exception replaces the report. A
Trace-output warning is written whether or not this callback is set, and a logged one where the host
resolved an `ILoggerFactory`.

Read at each report. Your callback is invoked unguarded, as
[`SuppressedIssueDiagnostic`](#suppressedissuediagnostic)'s is, so a throw surfaces from the
component's own lifecycle. Unlike that callback's issue, nothing here is payload-supplied: these
names and types come from the component's own accessor.

### `VerifyRowKeys`

`bool`, defaults to `false`. A development-time check that each bound component still speaks for the
field it registered: it re-reads its accessor on every parameter set, and a divergence throws an
`InvalidOperationException` naming the field and the fix.

Correctly keyed rows never trip it for anything done to the list itself;
[Collections and row identity](collections-and-row-identity.md#how-do-i-catch-a-message-on-the-wrong-row)
has the ways a page produces the divergence.

[Need to know](#need-to-know) names this a coarser read: the answer is captured once, per component,
the moment it binds, so flipping it mid-life reaches only components that bind afterward.

Treat it as a startup switch for Development builds;
[`/collections`](../samples/Formidable.Sample/Pages/Collections.razor) is the exception, and
[`ReportStaleRegistrations`](#reportstaleregistrations) answers where a throw is the wrong severity.

### `ReportStaleRegistrations`

`bool`, defaults to `false`. The report-never-throw sibling of [`VerifyRowKeys`](#verifyrowkeys),
for environments where a throw is the wrong severity: a divergence is reported rather than thrown,
through the channels [`StaleRegistrationDiagnostic`](#staleregistrationdiagnostic) documents. The
form renders on, misfiled messages and all; with `VerifyRowKeys` on, the exception replaces it.

One divergence is one report, repeating only after the accessor names the registered field again or
the component rebinds. Off by default because detection is not free (the same accessor resolution
`VerifyRowKeys` pays, at a cost [Collections and row identity](collections-and-row-identity.md)
sizes by shape).

```csharp
builder.Services.AddFormidableBlazor(options =>
{
    options.VerifyRowKeys = builder.HostEnvironment.IsDevelopment();
    options.ReportStaleRegistrations = !builder.HostEnvironment.IsDevelopment();
});
```

That pairing covers both environments. This option latches as `VerifyRowKeys` does: the answer is
captured once, per component, the moment it binds, so flipping it mid-session reaches only
components binding afterward.

### `InlineMessageLive`

`string?`, defaults to `null`, which renders no `aria-live` attribute at all. Set it to `"polite"`
and every message list (field-, collection- and model-level alike) is announced as its content
changes; `"assertive"` interrupts whatever is being read instead. Recommended on forms that render
no `FormidableSummary`, which already announces on its own.

It is `aria-live` rather than a `role`:
[Component kit](component-kit.md#formidablefieldmessagetvalue) has what a `role` on the list would
cost, and why the list element renders even when empty.

**Sample:** [`/disclosure`](../samples/Formidable.Sample/Pages/Disclosure.razor) — the summary-less
variant form under *Without a summary* sets it to `"polite"` on options of its own.

### `DefensiveGateMessage`

`string`, defaults to `"The form cannot be submitted because information that is not currently
displayed is invalid."` — the sentence the all-suppressed defensive gate carries (see
[Disclosure](disclosure.md)).

A replacement reaches the kit's components, and a direct `GetIssues` or `GetVisibleIssues` call,
at their next read, and a native `ValidationSummary` at the next rebuild of the `EditContext`'s
message store. Nothing keeps the old sentence beyond those points. This option decides the gate's
explanation and nothing else; `ModelLevelDisplayName`, next, names what it is listed under.

Why: [how the engine works: the gate](how-the-engine-works.md#the-gate-latch).

### `ModelLevelDisplayName`

`string`, defaults to `"This form"`. The name `SubmitOutcome.VisibleErrorSummary` lists an error
under when that error's issue names no field of its own: the defensive gate's explanation, and any
model-level rule a blocked submit disclosed.

That list holds names rather than messages. An entry is the issue's `DisplayName` where the issue
carries one (what `WithName(...)` sets) and its `Path` otherwise, and this option stands in
wherever that pair leaves an empty string. It is read as each submit builds its outcome, so a
`SubmitOutcome` already handed back holds the names it was built with.

It reaches that list and nothing else; a `FormidableSummary` entry renders its issue's `Message`
instead.

### `ValidationFaultMessage`

`string`, defaults to `"Validation could not run to completion; recent changes may not be fully
validated."` — the form-level message for a check that threw before finishing. It appears when a
live check or the whole-form re-check throws, since what the form shows is then incomplete rather
than wrong; a submit or a load of values throws to the code that awaited it instead.

Its read timing is not `DefensiveGateMessage`'s. The issue is filed when the fault is reported and
then stored, so
a change reaches the *next* fault while one already on screen goes on saying what it said when it
was written.

It says nothing about what threw: the exception goes to the engine's `ValidationFaulted` event,
where a host logs it. The stored issue clears at the next check that completes without throwing,
and at `ApplyServerIssues`, with no check involved.

### `OrderIssues`

`Func<IReadOnlyList<FieldIdentifier>, IReadOnlyList<FieldIdentifier>>?`, defaults to `null`, which
reports visible issues in the document order of the fields rendering them. Set it and the delegate
becomes a stage after that: the order service answers where the fields are; this answers what order
to report them in.

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

It receives the fields in document order, and `OrderBy` is stable, so everything the key does not
separate keeps the order it arrived in. The model-level field is in that list too, with an empty
`FieldName`, so a delegate keying on names should place it (the empty-name arm above) rather than
leave it to a default bucket.

[Component kit](component-kit.md#the-order-entries-appear-in) has the rest: what re-sorting cannot
do, when the delegate runs, and where exceptions go.

### `CssClasses`

`FormidableCssClasses`, defaults to a new instance. Class names field components and native
`InputBase` descendants apply based on field state:

| Property | Applied when | Default |
|---|---|---|
| `Invalid` | the field has error-severity issues | `formidable-invalid` |
| `Warning` | touched or modified, no errors, and has a warning-severity issue | `formidable-warning` |
| `Info` | touched or modified, no errors or warnings, and has an info-severity issue | `formidable-info` |
| `Valid` | the field is touched or modified, has no issues at all, and the engine can say a submit would not fail it | `formidable-valid` |
| `Pending` | a check involving the field is still running | `formidable-pending` |

Set it for the whole app with
`builder.Services.AddFormidableBlazor(options => options.CssClasses = new FormidableCssClasses { Invalid = "is-invalid", Valid = "is-valid" });`.

This is read at each class computation, on every surface, so mutating this instance's properties and
assigning a whole new `FormidableCssClasses` are the same lever, and nothing latches a class map at
engine construction. The `FormidableOptions` object around it is still the one that cannot be
swapped (see [`FormidableOptions` is read once](#formidableoptions-is-read-once)).
[CSS and accessibility](css-and-accessibility.md#what-puts-green-on-a-field) has why `Valid` asks
for that third condition and [how the five compose](css-and-accessibility.md#need-to-know).

## `UpdateOn` (per input, not a `FormidableOptions` property)

`UpdateOn` tunes a single input rather than the engine: it is a parameter on
`FormidableInputBase<TValue>` (see
[Component kit](component-kit.md#formidableinputtext-and-formidableinputbasetvalue)), not a member
of `FormidableOptions`, so it is not set through `Options`. It answers the other half of "when does
a rule get to answer": `RefreshDebounce` governs the whole-form re-check's timing, and `UpdateOn`
governs a live check's.

`InputUpdateMode.OnChange` (default) commits the value and notifies the engine together, on the
element's `change` event. `InputUpdateMode.OnInput` commits the same pair on every keystroke
instead. `InputUpdateMode.OnBlur` splits the pair across two events: the value commits on `change`,
arming a notification the next `blur` delivers (one delivery however many commits accumulate before
it, and none at all on a blur nothing was committed before).

```razor
<FormidableInputDate @bind-Value="Model.EventDate"
                     UpdateOn="InputUpdateMode.OnBlur" />
```

That split exists for a native control whose `change` event fires more than once per logical edit, a
date input segment by segment being the clearest case.

`OnBlur` is also the one mode that binds an event a page may already want for itself, so it chains
rather than claims. In that mode an input carrying its own splatted `@onblur` runs that handler
first, awaits it, then delivers what a commit left pending. A field that marks itself touched on
blur keeps doing so after the mode is switched on.

**Read:** [Recipes](recipes.md#i-want-to-validate-while-typing-on-blur-or-only-at-submit) for the
full behaviour table (all three modes, against a rule the live channel selects and one it does not)
and [Component kit](component-kit.md#formidableinputdatetvalue) for why `FormidableInputDate` in
particular prefers this mode.
**Sample:** [`/custom-profiles`](../samples/Formidable.Sample/Pages/CustomProfiles.razor) —
`Publish date` is the typed date input;
[`/workout`](../samples/Formidable.Sample/Pages/Workout.razor) shows the same mode on the
string-modelled pattern instead.

## `FormidableOptions` is read once

A new `Options` instance takes effect only together with a new `Model` instance, and the form
enforces that. Hand the `Options` parameter a different `FormidableOptions` reference on a render
where `Model` did not also change, and `FormidableForm<TModel>` throws an
`InvalidOperationException` naming the three ways out: build the options once, mutate the instance
you have, or swap `Model` alongside them.

`FormidableValidator<TModel>` enforces the same rule against its own rebuild trigger, a new
`EditContext` from the enclosing `EditForm`. Two shapes therefore never work: passing
`Options="new FormidableOptions { … }"` inline hands the form a fresh instance on every render, and
reassigning an options field hands it a different one on the next. Both are errors on the render
that introduces them.

Build the `FormidableOptions` once, up front, and hold it in a field for the life of the form
(exactly what the sample below does):

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

<!-- Excerpt from `samples/Formidable.Sample/Pages/Disclosure.razor.cs` -->

`_options` is a `FormidableOptions?` field on the page, built once here and never reassigned.

## App-wide defaults

Most settings on this page are a decision an app makes once, not per form: a design system's class
names, a team's debounce, a profile pair. `AddFormidableBlazor` takes an `Action<FormidableOptions>`
overload for exactly that, and registers the configured instance as the default every form falls
back to:

```csharp
builder.Services.AddFormidableBlazor(options =>
{
    options.CssClasses = new FormidableCssClasses { Invalid = "is-invalid", Valid = "is-valid" };
    options.RefreshDebounce = TimeSpan.FromMilliseconds(500);
});
```

A form with no `Options` parameter uses those. A form that passes one wins outright. Resolution is
parameter first, then the configured default, then `new FormidableOptions()`, with no merging
between the steps. See [Component kit](component-kit.md#addformidableblazor) for the registration
itself.

The copy constructor is how a form differs in one setting without restating the rest. It holds every
property the instance it copies holds, leaving an object initializer to say what changes:

```razor
@inject FormidableOptions AppWide

@code {
    private FormidableOptions? _options;

    protected override void OnInitialized() =>
        _options = new FormidableOptions(AppWide) { LiveProfile = ValidationProfile.Draft };
}
```

That form narrows its live channel and keeps everything else the app-wide instance holds. The
injection resolves the singleton registered above, so an app with no app-wide defaults has nothing
to copy. `OnInitialized` is where the copy belongs, because a copy is an options instance like any
other and [is read once](#formidableoptions-is-read-once).

That singleton is shared by the whole app, which makes property mutation a wider lever than it
looks: changing `RefreshDebounce` on it at runtime changes every live form that resolved it, not the
one on screen. A copy takes each property's value as it stands when the copy is built, so a later
change on the shared instance stops at the forms holding copies.

`CssClasses` is the exception, deliberately. The copy holds the same `FormidableCssClasses` instance
rather than a clone, so mutating that map's properties goes on reaching every form, copies included.
A form that wants different class names assigns a new map to its own copy.

## Where each option is demonstrated

- `LiveProfile` / `SubmitProfile` — [Profiles](profiles.md), plus
  [`/profiles`](../samples/Formidable.Sample/Pages/Profiles.razor) on the defaults,
  [`/custom-profiles`](../samples/Formidable.Sample/Pages/CustomProfiles.razor) pointing
  `SubmitProfile` at a profile of its own, and
  [`/server`](../samples/Formidable.Sample/Pages/ServerRoundTrip.razor) narrowing `LiveProfile` to
  `Draft` so the server is the only judge.
- `LiveDebounce` — [`/async`](../samples/Formidable.Sample/Pages/AsyncRules.razor), toggled against
  the immediate default.
- `TrackFormValidity`, and `IsFormValid` with it —
  [`/field-state`](../samples/Formidable.Sample/Pages/FieldStateVisualizer.razor), driving a
  disabled Submit button.
- `NormalizeOnSubmit` — [`/normalize`](../samples/Formidable.Sample/Pages/Normalize.razor), beside
  the two buttons that call `Normalize()` by hand.
- `SuppressedIssueDiagnostic` and `DisclosureOverride` —
  [`/disclosure`](../samples/Formidable.Sample/Pages/Disclosure.razor) and
  [Disclosure](disclosure.md).
- `CssClasses` — [CSS and accessibility](css-and-accessibility.md); remapped onto a UI library's own
  classes ([`/bootstrap`](../samples/Formidable.Sample/Pages/BootstrapFitting.razor)) and recoloured
  live via CSS custom properties
  ([`/css-colours`](../samples/Formidable.Sample/Pages/CssColours.razor)).
- `VerifyRowKeys` — [`/collections`](../samples/Formidable.Sample/Pages/Collections.razor), on
  unconditionally rather than gated to Development, since the page's whole point is the row-key
  discipline the guard enforces.
- `ShowRequiredIndicators` and `RequiredIndicatorContent`, and the marker they feed —
  [`/draft-load`](../samples/Formidable.Sample/Pages/DraftLoad.razor), where a required field
  carries its mark and stays silent at the same time.
- `InlineMessageLive` — [`/disclosure`](../samples/Formidable.Sample/Pages/Disclosure.razor)'s
  summary-less variant, which has no summary announcing for it, so the attribute is what the browser
  suite asserts on the model-level list there.

`ClickRecovery` has no sample page either, for a better reason: it is on by default on every page
here, and what it prevents is a click going missing. The gated browser suite pins it, on the
[`/`](../samples/Formidable.Sample/Pages/Quickstart.razor) quickstart form, the smallest page that
reproduces the shift. `RequiredOverride` has none because every validator the sample ships can be
inspected; the recipe linked from its entry is its worked example.

The rest have no sample page, deliberately. `NeverRegisteredFieldDiagnostic`,
`ReportStaleRegistrations` and `StaleRegistrationDiagnostic` report into your telemetry or the
console rather than onto the screen, and the last two report a mistake every sample page is written
not to make, since each keys its rows the way
[Collections and row identity](collections-and-row-identity.md) teaches.

`OrderIssues` re-sorts a reading order every sample page is already content with. `RefreshDebounce`
would demonstrate nothing but a longer wait. `LiveDisclosure` changes what happens for a field that
is engaged but not rendered, and no page here notifies a change for a field it never renders. And
`DefensiveGateMessage`, `ModelLevelDisplayName` and `ValidationFaultMessage` replace sentences the
samples are content to show as they ship. Each one's entry above is its worked example.

Where the layout itself is what makes document order wrong, the delegate cannot help, because it
cannot measure. [Recipes](recipes.md#i-want-the-summary-ordered-by-where-fields-appear-on-screen)
replaces the order service with one that measures. And two components carry behaviour these options
don't reach: [`/scroll-focus`](../samples/Formidable.Sample/Pages/ScrollFocus.razor) toggles
`FormidableForm.FocusFirstErrorOnInvalidSubmit`, and
[`/profiles`](../samples/Formidable.Sample/Pages/Profiles.razor)'s Reset button calls
`ResetAsync()`. Both are component surface rather than engine settings, so they live in
[Component kit](component-kit.md#formidableformtmodel).

**Sample:** [`/disclosure`](../samples/Formidable.Sample/Pages/Disclosure.razor) — the page this one
quotes for the shape of a well-behaved `Options` field.
