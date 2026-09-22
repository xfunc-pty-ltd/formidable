# Recipes: from behaviour to configuration

Formidable's behaviour comes out of a handful of orthogonal switches: which bucket a rule sits
in, which profile a pass runs, when an input commits its value, whether a field is currently
rendered, what severity a rule carries, and what the server says. This page maps the behaviours
people actually want onto those switches. Each recipe answers with code first, then links to the
doc that explains it in full and the sample page that demonstrates it.

## Part 1 — I want to…

### I want to validate while typing, on blur, or only at submit

**Set:** `UpdateOn` on the input — `InputUpdateMode.OnChange` (the default) or
`InputUpdateMode.OnInput` — and choose the bucket the rule lives in. `UpdateOn` decides when the
value commits; the bucket decides which pass judges it. A live pass runs `LiveProfile`
(`ValidationProfile.Draft` by default) once per field change; submit runs `SubmitProfile`, and a
debounced refresh afterwards keeps what that submit revealed current.

```razor
<FormidableInputText For="() => Model.Nickname" @bind-Value="Model.Nickname"
                      UpdateOn="InputUpdateMode.OnInput" />
```

```csharp
protected override void ConfigureDraftRules() =>
    RuleFor(m => m.Nickname).MaximumLength(20).WithMessage("20 characters max");
```

`OnInput` plus a draft-bucket rule validates on every keystroke; the default `OnChange` waits for
blur. Either way, submit and the post-submit refresh are unaffected by `UpdateOn` — they run on
their own triggers, not the input's commit event.

| | Draft-bucket rule (`ConfigureDraftRules`) | Submit-ruleset rule (`ConfigureSubmitRules`) |
|---|---|---|
| `UpdateOn="InputUpdateMode.OnChange"` (default) | When the field loses focus after a change: the commit starts a live pass and the message lands on that field. | Not before submit. At submit — and after that, each blur-commit re-runs the submit profile once the refresh debounce (300 ms) falls quiet. |
| `UpdateOn="InputUpdateMode.OnInput"` | On every keystroke: each one starts its own live pass, and the pass that wins writes the verdict. | Not before submit. At submit — and after that, typing re-runs the submit profile after 300 ms of quiet. |

Both columns assume the default profiles; pointing `FormidableOptions.LiveProfile` at another
profile moves the line. A live pass validates the whole model but writes verdicts only for the
fields that changed, so editing one field never lights up another's message, and the refresh only
ever narrows what submit revealed — a field that starts failing *after* a submit waits for the
next one.

**Read:** [Profiles](profiles.md), [Options](options.md).
**Samples:** [`/field-state`](../samples/Formidable.Sample/Pages/FieldStateVisualizer.razor),
[`/profiles`](../samples/Formidable.Sample/Pages/Profiles.razor).

### I want presence rules to wait for submit while formats answer live

**Set:** derive from `DraftSubmitValidator<T>`; put malformed-value rules (format, length, range)
in `ConfigureDraftRules()` and presence rules (`NotEmpty`, `NotNull`) in `ConfigureSubmitRules()`.

```csharp
public class BriefValidator : DraftSubmitValidator<Brief>
{
    protected override void ConfigureDraftRules() =>
        RuleFor(b => b.Title).MaximumLength(60).WithMessage("Title is 60 characters max");

    protected override void ConfigureSubmitRules() =>
        RuleFor(b => b.Title).NotEmpty().WithMessage("Title is required to submit");
}
```

Draft rules ask "is this value malformed?" and treat an empty value as fine; submit rules ask "is
this value present?" and treat default values as missing — two axes, so one mistake never
produces two messages.

`DraftSubmitValidator<T>` checks that convention once, at construction: a property carrying the
same kind of rule in both buckets invokes `OnOverlappingRuleAxes` (a `Debug` warning by default,
overridable). A "save draft" button asks the validator for `ValidationProfile.Draft` directly
rather than going through the form's submit pipeline, which is what makes it lenient.

**Read:** [Profiles](profiles.md).
**Samples:** [`/profiles`](../samples/Formidable.Sample/Pages/Profiles.razor),
[`/workout`](../samples/Formidable.Sample/Pages/Workout.razor).

### I want an async check with a pending indicator

**Set:** put the `MustAsync` rule in the draft bucket so it runs live, honour the
`CancellationToken` it hands you, and render the indicator from `field.State.IsValidating` inside
a `FormidableField` (kit inputs also append the `Pending` class on their own).

```csharp
protected override void ConfigureDraftRules() =>
    RuleFor(h => h.Username)
        .MustAsync(async (username, ct) => await IsAvailableAsync(username, ct))
        .WithMessage("That username is taken");
```

```razor
<FormidableField For="() => Model.Username" Context="field">
    <FormidableInputText For="() => Model.Username" @bind-Value="Model.Username"
                          UpdateOn="InputUpdateMode.OnInput" />
    @if (field.State.IsValidating) { <em role="status">checking…</em> }
</FormidableField>
```

Add `UpdateOn="InputUpdateMode.OnInput"` for a check that answers as the user types.
`IFormValidationEngine.IsValidating` is the form-wide flag; the per-field one is scoped — to the
field that changed during a live pass, to the fields edited in the debounce window during a
refresh, and form-wide during submit. One pass runs at a time: a newer live pass supersedes an
older one and writes the superseded fields' verdicts along with its own, while the debounced
refresh defers to a live pass still in flight and re-arms rather than cancelling it.

**Read:** [Async validation](async-validation.md), [Options](options.md)
(`RefreshDebounce`), [CSS and accessibility](css-and-accessibility.md) (`Pending`).
**Samples:** [`/async`](../samples/Formidable.Sample/Pages/AsyncRules.razor),
[`/field-state`](../samples/Formidable.Sample/Pages/FieldStateVisualizer.razor).

### I want to validate on the server and show its verdict

**Set:** on the server, `Validate<TModel>(profile?)` on a minimal-API handler or route group, or
`[Validate]` on an MVC action or controller. On the client, deserialize the 400 body into
`FormidableValidationProblem`, call `ToIssues()`, and pass the result to
`_form!.Engine!.ApplyServerIssues(...)` — the errors land on the same fields.

```csharp
var response = await Http.PostAsJsonAsync("/api/orders", Model);
if (!response.IsSuccessStatusCode)
{
    var problem = await response.Content.ReadFromJsonAsync<FormidableValidationProblem>();
    _form!.Engine!.ApplyServerIssues(problem!.ToIssues());
}
```

Call `model.Normalize()` before posting when the model implements `INormalizableModel`: the
filters normalize too, so cleaning first keeps the paths in the response lined up with the rows on
screen (there is no automatic client-side hook). Each apply replaces the previous server verdict
instead of accumulating, and only error-severity issues are applied — a rejection's `warnings`
extension is the page's to present. Server-declared issues bypass the disclosure registry, since
the server judged what was actually submitted.

**Read:** [Server integration](server-integration.md), [Severity](severity.md).
**Samples:** [`/server`](../samples/Formidable.Sample/Pages/ServerRoundTrip.razor),
[`/normalize`](../samples/Formidable.Sample/Pages/Normalize.razor),
[`/workout`](../samples/Formidable.Sample/Pages/Workout.razor).

### I want to reveal fields conditionally without losing their rules

**Set:** if the field's relevance is decided by *data*, mirror the `@if` condition in the rule's
`.When(...)` — the rule then never runs for the path that hides it, so the alternate answer stays
submittable.

```csharp
RuleFor(t => t.AccommodationType).NotEmpty().WithMessage("Choose an accommodation type")
    .When(t => t.NeedsAccommodation == true);
```

```razor
@if (Model.NeedsAccommodation == true)
{
    <FormidableField For="() => Model.AccommodationType" Context="field">
        <FieldMessage For="() => Model.AccommodationType" />
    </FormidableField>
}
```

If relevance is decided by *UI state* alone, leave the rule unconditional: visibility is
render-registration, so the rule keeps running and counting toward validity while the field is off
screen, and its message is suppressed until a submit finds the field rendered.

Observe suppressions through `FormidableOptions.SuppressedIssueDiagnostic`, and force an issue
visible (`true`) or hidden (`false`) with `DisclosureOverride`. When every failing field is hidden,
the engine blocks anyway and reports one model-level explanation rather than a silent no-op submit.
For rows a `Virtualize` container disposes, `KeepRegistered` holds an already-showing error open,
and a `DisclosureOverride` on the collection covers rows it has never rendered.

**Read:** [Disclosure](disclosure.md), [Options](options.md).
**Samples:** [`/disclosure`](../samples/Formidable.Sample/Pages/Disclosure.razor),
[`/virtualized`](../samples/Formidable.Sample/Pages/Virtualized.razor),
[`/workout`](../samples/Formidable.Sample/Pages/Workout.razor).

### I want to advise without blocking

**Set:** `.WithSeverity(Severity.Warning)` or `.WithSeverity(Severity.Info)` on the rule.

```csharp
RuleFor(l => l.Tags).Must(tags => tags.Count <= 5)
    .WithSeverity(Severity.Warning)
    .WithMessage("More than five tags rarely helps discovery");
```

`SubmitOutcome.CanProceed` counts error-severity issues only, so a report of warnings and infos
submits successfully, and the same rule holds on the server: a report without errors passes the
filters untouched.

`FieldMessage` and `FormSummary` render every severity, classed `formidable-message--{severity}`
and `formidable-summary__group--{severity}`; the `EditContext`'s message store receives errors
only, so a native `ValidationMessage` shows nothing for an advisory. Submit is the disclosure
event for advisories exactly as for errors — a warning that was showing keeps refreshing as the
user edits, a field that was an error site at submit picks up a newly-appearing warning too, and
only a field with neither an error nor a warning at submit waits for the next submit.

**Read:** [Severity](severity.md).
**Samples:** [`/severity`](../samples/Formidable.Sample/Pages/SeverityLevels.razor),
[`/workout`](../samples/Formidable.Sample/Pages/Workout.razor).

### I want every summary entry to land somewhere

**Set:** make sure a rendered element carries the field's deterministic id. The kit's inputs
render `FormidableFieldId.For(field)` themselves; anything the page renders needs it explicitly.

```csharp
private string TeamsId => FormidableFieldId.For(new FieldIdentifier(Model, nameof(Model.Teams)));
```

```razor
<div id="@TeamsId" tabindex="-1">
    <CollectionMessage For="() => Model.Teams" />
</div>
```

A field that owns no input — a collection whose rule fails against the list, or the model-level
field behind the all-suppressed gate — is reachable the same way: put its id and `tabindex="-1"`
on the container (or on the `<form>` element), and the summary entry lands there. For a field
that is not currently in the DOM at all, give `FormSummary` a `FocusFallback`: make the element
renderable, return `true`, and the summary retries the focus once.

**Read:** [CSS and accessibility](css-and-accessibility.md),
[Component kit](component-kit.md).
**Samples:** [`/scroll-focus`](../samples/Formidable.Sample/Pages/ScrollFocus.razor),
[`/virtualized`](../samples/Formidable.Sample/Pages/Virtualized.razor),
[`/vanilla`](../samples/Formidable.Sample/Pages/VanillaInterop.razor),
[`/workout`](../samples/Formidable.Sample/Pages/Workout.razor).

### I want to use a native or third-party control

**Set:** wrap it in `FormidableField` and call `field.NotifyChanged()` from its change handler —
the context supplies `ElementId`, `CssClass`, `AriaInvalid`, `AriaDescribedBy` and
`MarkTouched()`.

```razor
<select value="@Model.Colour" @onchange="OnColourChanged" id="@field.ElementId"
        class="@field.CssClass" aria-invalid="@field.AriaInvalid" />
```

```csharp
private void OnColourChanged(ChangeEventArgs args, FormidableFieldContext field)
{
    Model.Colour = args.Value?.ToString() ?? string.Empty;
    field.NotifyChanged();
}
```

Reach for `FieldAnchor` instead when the control already notifies the `EditContext` itself, as
every native `InputBase` descendant does, and only needs registering.

Commit on blur when a control fires `change` mid-edit: a native date input fires one per segment,
so write the model in `@onchange` and call `NotifyChanged()` from `@onblur`, once the value has
settled. Native `InputBase` components inside a `FormidableForm` pick up the configured state
classes automatically — the engine installs a `FieldCssClassProvider` on the shared `EditContext`
— but not the pending class, the id or the aria attributes, so a page renders those itself.
Matching a UI library's own class names is `FormidableOptions.CssClasses`.

**Read:** [Component kit](component-kit.md), [Disclosure](disclosure.md),
[CSS and accessibility](css-and-accessibility.md).
**Samples:** [`/foreign`](../samples/Formidable.Sample/Pages/ForeignControl.razor),
[`/vanilla`](../samples/Formidable.Sample/Pages/VanillaInterop.razor),
[`/bootstrap`](../samples/Formidable.Sample/Pages/BootstrapFitting.razor),
[`/workout`](../samples/Formidable.Sample/Pages/Workout.razor).

### I want profiles of my own

**Set:** derive from `ProfiledValidator<T>`, put shared rules in `ConfigureCommonRules()` — they
run under every profile that includes the default rules — and each named ruleset in
`ConfigureProfiles()` via `Profile(name, ...)`.

```csharp
public class ReviewedPostValidator : ProfiledValidator<ReviewedPost>
{
    protected override void ConfigureCommonRules() =>
        RuleFor(p => p.Title).NotEmpty();

    protected override void ConfigureProfiles() =>
        Profile("AdminReview", () =>
            RuleFor(p => p.ReviewNote).NotEmpty());
}
```

```csharp
Options.SubmitProfile = ValidationProfile.Named("AdminReview", includeDefaultRules: true, "AdminReview");
```

Compose a selection with `ValidationProfile.Named(name, includeDefaultRules, ruleSets)` and point
`FormidableOptions.LiveProfile` or `SubmitProfile` at it. `FormidableForm` picks up the `Options`
parameter once, when it binds a `Model` instance, but the engine holds that instance for its
whole lifetime and re-reads its properties fresh on every pass — mutating
`LiveProfile`/`SubmitProfile` on the same `FormidableOptions` object takes effect starting with
the very next pass, no fresh model required. Handing the form a wholly new `FormidableOptions`
instance is a different move — that one does need a fresh model alongside it, since only a
`Model` reference change makes `FormidableForm` look at the `Options` parameter again — and is
worth reaching for when a batch of option changes should become visible together in one step
rather than as several independently-observable mutations. Server-side, minimal APIs take a
`ValidationProfile` value directly while `[Validate(Profile = "…")]` takes a name — any name
other than Draft or Submit becomes the default rules plus the same-named ruleset.

**Read:** [Profiles](profiles.md), [Options](options.md),
[Server integration](server-integration.md).
**Sample:** [`/custom-profiles`](../samples/Formidable.Sample/Pages/CustomProfiles.razor).

### I want localized messages

**Set:** nothing in Formidable — FluentValidation's own localization passes straight through. Keep
a rule's default message and FluentValidation translates it by `CultureInfo.CurrentUICulture`; for
your own text, pass a message *factory* so the resource lookup happens each time the rule runs and
follows the culture in force then.

```csharp
RuleFor(p => p.Age).Must(BeAWholeAgeInRange)
    .WithMessage(_ => ValidationMessages.AgeRange);

RuleFor(o => o.Description).NotEmpty().WithName("Order description");
```

`WithName(...)` display names land on `ValidationIssue.DisplayName`, and from there in
`SubmitOutcome.VisibleErrorSummary`, ready for a dialog without extra mapping. WebAssembly fixes
its culture at startup, so a language switch means storing the choice and applying it before the
host runs.

**Read:** [Profiles](profiles.md) (localization and display names).
**Sample:** [`/localization`](../samples/Formidable.Sample/Pages/Localization.razor).

## Part 2 — Troubleshooting

| Symptom | Why | Fix |
|---|---|---|
| A field says nothing until Submit is pressed. | Presence rules live in the `"Submit"` ruleset by convention, and live passes run the Draft profile. | Working as designed — move the rule into the draft bucket if it should answer live. [Validate while typing, on blur, or only at submit](#i-want-to-validate-while-typing-on-blur-or-only-at-submit), [presence rules wait for submit](#i-want-presence-rules-to-wait-for-submit-while-formats-answer-live). |
| Clicking a summary entry does nothing. | Click-to-focus looks the field up by its deterministic id, and no rendered element carries it — a control the page renders itself, or a field with no input of its own. | Render the id: `id="@field.ElementId"`, or `FormidableFieldId.For(field)` plus `tabindex="-1"` on a container. [Every summary entry lands somewhere](#i-want-every-summary-entry-to-land-somewhere). |
| A date input reports impossible years while it is being typed. | A native date input fires `change` once per segment, so validating on every change judges half-typed values. | Write the model on `@onchange`, and call `NotifyChanged()` from `@onblur` so the pass runs on the settled value. [Use a native or third-party control](#i-want-to-use-a-native-or-third-party-control). |
| The server's error never appears, though client errors do. | The page posts from `OnValidSubmit`, so a rule only the server owns cannot speak until every client rule passes. | Expected ordering — clear the client errors and the server's verdict arrives last. Post without that gate if the server should answer first. [Validate on the server and show its verdict](#i-want-to-validate-on-the-server-and-show-its-verdict). |
| A warning is on screen but the submit succeeded. | Warnings and infos never affect validity: `CanProceed` counts error-severity issues only. | That is the severity doing its job — give the rule error severity if it must block. [Advise without blocking](#i-want-to-advise-without-blocking). |
| Submit is blocked but no field shows a message. | Every failing field is unrevealed, so the defensive gate blocks with one model-level explanation instead of a silent no-op. | Render a `FormSummary` (the gate's message shows there), watch `SuppressedIssueDiagnostic`, and check whether the rule needed a mirrored `.When(...)`. [Reveal fields conditionally without losing their rules](#i-want-to-reveal-fields-conditionally-without-losing-their-rules). |
| The summary names a field that is not on screen. | Visibility is decided at submit and the refresh only narrows that set, so hiding a field afterwards leaves its entry until the next submit. A `DisclosureOverride` returning `true` also discloses fields that were never rendered. | Submit again to re-derive the visible set; keep the override where it is deliberate, as virtualized rows are. [Reveal fields conditionally without losing their rules](#i-want-to-reveal-fields-conditionally-without-losing-their-rules). |
| A message lingers after the value was fixed. | After a submit, submit-profile messages are updated by the debounced refresh — 300 ms of quiet after the value commits, which under the default `UpdateOn` means after blur — and that refresh waits for any live pass still in flight. | Wait out the debounce, commit on input instead, or tune `FormidableOptions.RefreshDebounce`. [Validate while typing, on blur, or only at submit](#i-want-to-validate-while-typing-on-blur-or-only-at-submit), [async check with a pending indicator](#i-want-an-async-check-with-a-pending-indicator). |
