# Recipes: from behaviour to configuration

Formidable's behaviour comes out of a handful of orthogonal switches: which bucket a rule sits
in, which profile a pass runs, when an input commits its value, whether a field is currently
rendered, what severity a rule carries, and what the server says. This page maps the behaviours
people actually want onto those switches. Each recipe answers with code first, then links to the
doc that explains it in full and the sample page that demonstrates it.

## Part 1 — I want to…

### I want to validate while typing, on blur, or only at submit

**Set:** `UpdateOn` on the input — `InputUpdateMode.OnChange` (the default),
`InputUpdateMode.OnInput`, or `InputUpdateMode.OnBlur` — and choose the bucket the rule lives in.
`UpdateOn` decides when the value commits and when the engine hears about it; the bucket decides
which pass judges it once it does. A live pass runs `LiveProfile` (`ValidationProfile.Draft` by
default) once per field change; submit runs `SubmitProfile`, and a debounced refresh afterwards
keeps what that submit revealed current.

```razor
<FormidableInputText @bind-Value="Model.Nickname"
                      UpdateOn="InputUpdateMode.OnInput" />
```

```csharp
protected override void ConfigureDraftRules() =>
    RuleFor(m => m.Nickname).MaximumLength(20).WithMessage("20 characters max");
```

`OnInput` plus a draft-bucket rule validates on every keystroke; the default `OnChange` waits for
blur. Either way, submit and the post-submit refresh are unaffected by `UpdateOn` — they run on
their own triggers, not the input's commit event.

A third mode answers a different question: not *when* but *how many times before it matters*.
`OnBlur` commits the value on `change`, same event as the default, but waits for `blur` to notify
the engine — the two halves the other two modes always keep together, deliberately apart here.
That matters for a control whose `change` event fires more than once per logical edit, a native
date input firing once per date segment being the clearest case: without the split, each segment
would start (and cancel) its own live pass on a value that isn't finished yet.

```razor
<FormidableInputDate @bind-Value="Model.EventDate"
                     UpdateOn="InputUpdateMode.OnBlur" />
```

| | Draft-bucket rule (`ConfigureDraftRules`) | Submit-ruleset rule (`ConfigureSubmitRules`) |
|---|---|---|
| `UpdateOn="InputUpdateMode.OnChange"` (default) | When the field loses focus after a change: the commit starts a live pass and the message lands on that field. | Not before submit. At submit — and after that, each blur-commit re-runs the submit profile once the refresh debounce (300 ms) falls quiet. |
| `UpdateOn="InputUpdateMode.OnInput"` | On every keystroke: each one starts its own live pass, and the pass that wins writes the verdict. | Not before submit. At submit — and after that, typing re-runs the submit profile after 300 ms of quiet. |
| `UpdateOn="InputUpdateMode.OnBlur"` | Same trigger as the default — losing focus — but the value already committed on whichever `change` event came last, so a multi-segment control never starts a live pass mid-edit. | Not before submit. At submit — and after that, each blur-commit re-runs the submit profile once the refresh debounce (300 ms) falls quiet. |

Both columns assume the default profiles; pointing `FormidableOptions.LiveProfile` at another
profile moves the line. A live pass validates the whole model but writes verdicts only for the
fields that changed, so editing one field never lights up another's message, and the refresh only
ever narrows what submit revealed — a field that starts failing *after* a submit waits for the
next one.

**Read:** [Profiles](profiles.md), [Options](options.md).
**Samples:** [`/field-state`](../samples/Formidable.Sample/Pages/FieldStateVisualizer.razor),
[`/profiles`](../samples/Formidable.Sample/Pages/Profiles.razor),
[`/custom-profiles`](../samples/Formidable.Sample/Pages/CustomProfiles.razor) (`OnBlur` on the
typed `FormidableInputDate`),
[`/workout`](../samples/Formidable.Sample/Pages/Workout.razor) (`OnBlur` on the string-modelled
pattern, both date fields).

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
    <FormidableInputText @bind-Value="Model.Username"
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
`FormidableValidationProblem` and hand it to `_form!.ApplyServerIssues(...)` — every issue lands
on the field it names.

```csharp
var response = await Http.PostAsJsonAsync("/api/orders", Model);
if (!response.IsSuccessStatusCode)
{
    var problem = await response.Content.ReadFromJsonAsync<FormidableValidationProblem>();
    _form!.ApplyServerIssues(problem!);
}
```

Call `problem!.ToIssues()` first and pass the flattened list instead when the page wants the issues
for something of its own; the two overloads are otherwise identical.

Clean the model before posting when it implements `INormalizableModel`: the filters normalize too,
so cleaning first keeps the paths in the response lined up with the rows on screen. Either call
`model.Normalize()` yourself, or set
[`FormidableOptions.NormalizeOnSubmit`](options.md#normalizeonsubmit) and the submit pass does it
before the profile runs — which, since the POST goes out from `OnValidSubmit`, is before the model
reaches the wire. Each apply replaces the previous server verdict
instead of accumulating, and the verdict applies at the severity it carries: a rejection's
`advisories` extension lands on its fields as warnings and infos, blocking nothing. Server-declared
errors bypass the disclosure registry, since the server judged what was actually submitted;
advisories defer to it like the client's own, since a hidden advisory blocks nothing.

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
        <FormidableFieldMessage For="() => Model.AccommodationType" />
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

`FormidableFieldMessage` and `FormidableSummary` render every severity, classed `formidable-message--{severity}`
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
private string TeamsId => FormidableFieldId.For(Model, m => m.Teams);
```

```razor
<div id="@TeamsId" tabindex="-1">
    <FormidableCollectionMessage For="() => Model.Teams" />
</div>
```

A collection is the case that needs this by hand: its rule fails against the list, not against
any one input, so nothing renders its id automatically. The model-level field behind the
all-suppressed gate is reachable the same way — an id and `tabindex="-1"` on the element that
should take focus — but `FormidableForm` already does it, on the `<form>` element it renders;
only attach mode's `FormidableValidator`, which renders no `<form>` of its own, still needs the
page to render that one by hand (see [Component kit](component-kit.md)). For a field that is not
currently in the DOM at all, give `FormidableSummary` a `FocusFallback`: make the element
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
<select @attributes="field.InputAttributes"
        value="@Model.Colour" @onchange="args => OnColourChanged(args, field)" />
```

```csharp
private void OnColourChanged(ChangeEventArgs args, FormidableFieldContext field)
{
    Model.Colour = args.Value?.ToString() ?? string.Empty;
    field.NotifyChanged();
}
```

Reach for `FormidableFieldAnchor` instead when the control already notifies the `EditContext` itself, as
every native `InputBase` descendant does, and only needs registering.

A native date or number input firing `change` mid-edit — once per typed segment, for a date —
doesn't need the seam at all: reach for `FormidableInputDate`/`FormidableInputNumber` with
`UpdateOn="InputUpdateMode.OnBlur"` instead, when the model is genuinely a `DateOnly`/`decimal`/
etc. rather than a string holding one. Both convert through `CultureInfo.InvariantCulture`, so the
model gets the typed value without the culture hazard a generic typed binder would otherwise
have — see [Component kit](component-kit.md#formidableinputnumbertvalue) and
[Options](options.md#updateon-per-input-not-a-formidableoptions-property). Splatting the type
onto `FormidableInputText`/`FormidableInputTextArea` instead is still right for a field whose
model type genuinely is a string: that binds a plain `string`, with no conversion and so no
culture involved either way, the same pattern Workout's own date fields use, parsing the string
by hand once it's in the model. Write the model in `@onchange` and call `NotifyChanged()` from
`@onblur` by hand only for a control this seam exists for in the first place — one that isn't a
plain `<input>`/`<textarea>` at all, or whose value doesn't bind through `value` (a plain
checkbox binds through `checked`, so it needs the seam too).

Native `InputBase` components inside a `FormidableForm` pick up the configured state classes
automatically, pending included — the engine installs a `FieldCssClassProvider` on the shared
`EditContext` — but not the id or the aria attributes, so a page renders those itself. Matching a
UI library's own class names is `FormidableOptions.CssClasses`.

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
instance is a different move — that one does need a fresh model alongside it, and the form throws
if it doesn't get one, since only a `Model` reference change makes `FormidableForm` look at the
`Options` parameter again — and is
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

### I want to validate a nested object

**Set:** `SetValidator` on the parent property's rule (or `ChildRules` to write them inline), and
name the nested field with its full path in `For` or `@bind-Value`.

```csharp
public class OrderValidator : AbstractValidator<Order>
{
    public OrderValidator() =>
        RuleFor(o => o.ShippingAddress).SetValidator(new AddressValidator());
}

public class AddressValidator : AbstractValidator<Address>
{
    public AddressValidator() =>
        RuleFor(a => a.Street).NotEmpty().WithMessage("Street is required");
}
```

```razor
<FormidableInputText @bind-Value="Model.ShippingAddress.Street" />
<FormidableFieldMessage For="() => Model.ShippingAddress.Street" />
```

Depth needs no special handling. FluentValidation reports the issue at `ShippingAddress.Street`,
and Formidable walks that path down to the `Address` instance and keys the issue to *that object* —
the same resolution a collection row gets, for the same reason. Another level down changes nothing:
`() => Model.ShippingAddress.Region.Code` is still one field, owned by the `Region` instance.
Nested and indexed segments mix freely too, so `RuleForEach(o => o.Lines).SetValidator(...)`
produces `Lines[0].Sku` and lands on the row object.

Give the nested object a value the markup can reach — `public Address ShippingAddress { get; set; }
= new();` — since `() => Model.ShippingAddress.Street` dereferences it while rendering. The engine
itself is defensive about a null on the way down (the issue falls back to the deepest object that
does exist, carrying the rest of the path), but the page's own lambda runs first.

**Read:** [Collections and row identity](collections-and-row-identity.md) (path resolution in
full), [Fields and collections](fields-and-collections.md).
**Sample:** [`/collections`](../samples/Formidable.Sample/Pages/Collections.razor) — teams holding
members, nested one level inside a collection.

### I want to unit-test my form

**Set:** for the rules, drive `IModelValidator<T>` directly — no renderer involved. For the form,
render it under bUnit with test doubles registered *before* `AddFormidableBlazor()`.

```csharp
var validator = new FluentValidationModelValidator<Brief>(new BriefValidator());

var report = await validator.ValidateAsync(new Brief(), ValidationProfile.Submit);

Assert.Contains(report.Errors, i => i.Path == "Title");
```

```csharp
Services.AddSingleton<IFormidableFocusService>(_focus);      // your recording double
Services.AddSingleton<IFormidableDomValueSync>(_domSync);    // ditto
Services.AddSingleton<IFormidableFieldOrderService>(_order); // ditto
Services.AddFormidableBlazor();                              // respects all three
Services.AddSingleton<IValidator<Signup>>(new SignupValidator());
```

Those three are the only kit services a rendered form resolves that talk to JavaScript, and
doubling them makes focus and issue order assertable as a side benefit. `FormidableSummary`
injects the focus service outright, so a form rendering one needs it present either way.

Assert through bUnit's `WaitForAssertion`, since a verdict lands a render later than the event that
asked for it, and call the form's own methods — `SubmitAsync()`, `ResetAsync()`,
`ApplyServerIssues(...)` — through `InvokeAsync`, since all three trigger renders.

**Read:** [Testing](testing.md#testing-your-forms) (the same ground in full, including how to pin a
pending state), [Component kit](component-kit.md).
**Sample:** no page. The worked examples are Formidable's own component tests, such as
[`FormidableFormComponentTests.cs`](../tests/Formidable.Blazor.Tests/FormidableFormComponentTests.cs),
which render the kit exactly this way.

## Part 2 — Troubleshooting

| Symptom | Why | Fix |
|---|---|---|
| A new project won't build: `RZ9991` on every `@bind-Value` — `The attribute names could not be inferred from bind attribute 'bind-Value'` — plus a run of `RZ10012` warnings about unresolved component names. | The kit isn't in scope, so the Razor compiler reads `<FormidableInputText>` as plain markup and the `@bind-Value` on it as an attribute nothing can bind. Both diagnostics describe binding syntax; neither mentions the namespace that is actually missing. | Add `@using Formidable.Blazor` to `_Imports.razor`, and `using FluentValidation;` plus `using Formidable.Blazor;` to `Program.cs`. [Quickstart](quickstart.md). |
| Submit answers with an HTTP 400: `The POST request does not specify which form is being submitted. To fix this, ensure <form> elements have a @formname attribute with any unique value, or pass a FormName parameter if using <EditForm>.` | The page is statically server-rendered, so the browser posts the form and no Blazor component ever sees the submit. The advice can't be followed either — `FormidableForm` has no `FormName` parameter to pass. | Add `@rendermode InteractiveServer` (or `@rendermode InteractiveWebAssembly`) to the page. `FormidableForm` refuses to render where the renderer reports itself static, naming that same fix in its own words; this 400 is what a host whose renderer says nothing answers instead. [Quickstart](quickstart.md). |
| A field says nothing until Submit is pressed. | Presence rules live in the `"Submit"` ruleset by convention, and live passes run the Draft profile. | Working as designed — move the rule into the draft bucket if it should answer live. [Validate while typing, on blur, or only at submit](#i-want-to-validate-while-typing-on-blur-or-only-at-submit), [presence rules wait for submit](#i-want-presence-rules-to-wait-for-submit-while-formats-answer-live). |
| Clicking a summary entry does nothing. | Click-to-focus looks the field up by its deterministic id, and no rendered element carries it — a control the page renders itself, or a field with no input of its own. | Render the id: `id="@field.ElementId"`, or `FormidableFieldId.For(field)` plus `tabindex="-1"` on a container. [Every summary entry lands somewhere](#i-want-every-summary-entry-to-land-somewhere). |
| A hand-wired control never validates live, even though it picks up the state classes. | Its handler calls `MarkTouched()`, which marks the field touched without telling the `EditContext` a value changed — so no live pass ever runs for it, and a touched field with no errors is styled valid. | Call `field.NotifyChanged()` from the change handler. `MarkTouched()` belongs on blur, where there is no new value to judge. [Use a native or third-party control](#i-want-to-use-a-native-or-third-party-control). |
| A date input reports impossible years while it is being typed. | A native date input fires `change` once per segment, so validating on every change judges half-typed values. | Set `UpdateOn="InputUpdateMode.OnBlur"` so the pass waits for the value to settle. [Validate while typing, on blur, or only at submit](#i-want-to-validate-while-typing-on-blur-or-only-at-submit). |
| The server's error never appears, though client errors do. | The page posts from `OnValidSubmit`, so a rule only the server owns cannot speak until every client rule passes. | Expected ordering — clear the client errors and the server's verdict arrives last. Post without that gate if the server should answer first. [Validate on the server and show its verdict](#i-want-to-validate-on-the-server-and-show-its-verdict). |
| The server's errors land inline but its advisories show nowhere. | Server-declared errors bypass the disclosure registry; advisories defer to it, so an advisory whose field nothing rendered has nowhere to go. | Render the field (or a `FormidableFieldAnchor` for one the page draws itself); the browser console names each dropped advisory with `Formidable: issue at '...' is suppressed`. [Validate on the server and show its verdict](#i-want-to-validate-on-the-server-and-show-its-verdict). |
| A warning is on screen but the submit succeeded. | Warnings and infos never affect validity: `CanProceed` counts error-severity issues only. | That is the severity doing its job — give the rule error severity if it must block. [Advise without blocking](#i-want-to-advise-without-blocking). |
| Submit is blocked but no field shows a message. | Every failing field is unrevealed, so the defensive gate blocks with one model-level explanation instead of a silent no-op. | Render a `FormidableSummary` (the gate's message shows there), check the browser console for `Formidable: issue at '...' is suppressed` (or watch `SuppressedIssueDiagnostic` for the same events in code), and check whether the rule needed a mirrored `.When(...)`. [Reveal fields conditionally without losing their rules](#i-want-to-reveal-fields-conditionally-without-losing-their-rules). |
| The summary names a field that is not on screen. | Visibility is decided at submit and the refresh only narrows that set, so hiding a field afterwards leaves its entry until the next submit. A `DisclosureOverride` returning `true` also discloses fields that were never rendered. | Submit again to re-derive the visible set; keep the override where it is deliberate, as virtualized rows are. [Reveal fields conditionally without losing their rules](#i-want-to-reveal-fields-conditionally-without-losing-their-rules). |
| A message lingers after the value was fixed. | After a submit, submit-profile messages are updated by the debounced refresh — 300 ms of quiet after the value commits, which under the default `UpdateOn` means after blur — and that refresh waits for any live pass still in flight. | Wait out the debounce, commit on input instead, or tune `FormidableOptions.RefreshDebounce`. [Validate while typing, on blur, or only at submit](#i-want-to-validate-while-typing-on-blur-or-only-at-submit), [async check with a pending indicator](#i-want-an-async-check-with-a-pending-indicator). |
