# Recipes: from behaviour to configuration

Formidable's behaviour comes out of a handful of orthogonal switches: which bucket a rule sits
in, which profile a pass runs, when an input commits its value, whether a field is currently
rendered, what severity a rule carries, and what the server says. This page maps the behaviours
people actually want onto those switches. Each recipe answers with code first, then links to the
doc that explains it in full and the sample page that demonstrates it.

## Part 1 — I want to…

### I want to validate while typing, on blur, or only at submit

**Set:** `UpdateOn` on the input — `InputUpdateMode.OnChange` (the default),
`InputUpdateMode.OnInput`, or `InputUpdateMode.OnBlur`. It decides when the value commits and
when the engine hears about it. A live pass runs `LiveProfile` once per field change, and that
option defaults to `null`, meaning the submit profile itself — so every rule answers live by
default, whichever bucket it was declared in. Submit runs `SubmitProfile`, and a debounced
refresh afterwards keeps what that submit revealed current.

```razor
<FormidableInputText @bind-Value="Model.Nickname"
                      UpdateOn="InputUpdateMode.OnInput" />
```

```csharp
protected override void ConfigureDraftRules() =>
    RuleFor(m => m.Nickname).MaximumLength(20).WithMessage("20 characters max");
```

`OnInput` plus a draft-bucket rule validates on every keystroke; the default `OnChange` waits for
blur. Either way, submit and the refresh are unaffected by `UpdateOn` — they run on their own
triggers, not the input's commit event.

A third mode answers a different question: not *when* but *how many times before it matters*.
`OnBlur` commits the value on `change`, same event as the default, but each commit only arms a
notification, and the next `blur` delivers it — the two halves the other two modes always keep
together, deliberately apart here. In every mode, a committed change is the only thing that ever
notifies the engine; this mode moves the delivery, and a blur nothing was committed before
delivers nothing. That matters for a control whose `change` event fires more than once per
logical edit, a native date input firing once per date segment being the clearest case: without
the split, each segment would start (and cancel) its own live pass on a value that isn't
finished yet. With it, however many commits pile up, one blur delivers one notification.

```razor
<FormidableInputDate @bind-Value="Model.EventDate"
                     UpdateOn="InputUpdateMode.OnBlur" />
```

| | A rule the live channel selects (by default, every one) | A rule it doesn't (`LiveProfile` narrowed past it) |
|---|---|---|
| `UpdateOn="InputUpdateMode.OnChange"` (default) | When the field loses focus after a change: the commit starts a live pass and the message lands on that field. | Not before submit. At submit — and after that, each blur-commit re-answers the submit profile once the refresh debounce (300 ms) falls quiet. |
| `UpdateOn="InputUpdateMode.OnInput"` | On every keystroke: each one starts its own live pass, and the pass that wins writes the verdict. | Not before submit. At submit — and after that, typing re-answers the submit profile after 300 ms of quiet. |
| `UpdateOn="InputUpdateMode.OnBlur"` | When the field loses focus after a change: the blur delivers one notification for however many `change` commits preceded it, so a multi-segment control never starts a live pass mid-edit — and a blur with no commit before it starts nothing. | Not before submit. At submit — and after that, each blur-commit re-answers the submit profile once the refresh debounce (300 ms) falls quiet. |

Which column a rule falls in is a configuration choice rather than a property of the bucket it
was declared in: `FormidableOptions.LiveProfile` draws the line, and left at its default it
selects everything the submit profile selects, so the right-hand column is empty until a form
asks for it (see
[narrow what the live channel validates](#i-want-to-narrow-what-the-live-channel-validates)).

What keeps the left column from nagging is the engaged set, not the rule selection. A live pass
validates the whole model, and its verdict answers every *engaged* field — a field is engaged
once a committed change has notified the engine about it, and a page can engage the values it
loads itself — so an engaged field's message clears, or appears, the moment an edit anywhere on
the form settles the question, while a field nobody has engaged stays silent however loudly its
rule fails. See
[Disclosure](disclosure.md#the-live-channel-plays-by-its-own-rule) for how that differs from the
submit channel's own registration-gated rule.

The refresh answers the submit channel alone, and for the fields a submit revealed there,
re-answering them rather than widening the set: a field whose submit-only rule starts failing
*after* a submit earns no entry from the refresh, while a field an earlier submit already showed
re-discloses on the refresh alone. On the default profiles the live channel is answering that
same field alongside, so an engaged one speaks anyway, on its own row and in the summary. Narrow
`LiveProfile` past the rule and the next submit is what surfaces it.

**Read:** [Profiles](profiles.md), [Options](options.md).
**Samples:** [`/field-state`](../samples/Formidable.Sample/Pages/FieldStateVisualizer.razor),
[`/profiles`](../samples/Formidable.Sample/Pages/Profiles.razor),
[`/custom-profiles`](../samples/Formidable.Sample/Pages/CustomProfiles.razor) (`OnBlur` on the
typed `FormidableInputDate`),
[`/workout`](../samples/Formidable.Sample/Pages/Workout.razor) (`OnBlur` on the string-modelled
pattern, both date fields).

### I want presence rules to wait for submit while formats answer live

**Set:** two things, because two separate decisions are involved. Derive from
`DraftSubmitValidator<T>` and put malformed-value rules (format, length, range) in
`ConfigureDraftRules()` and presence rules (`NotEmpty`, `NotNull`) in `ConfigureSubmitRules()`.
Then point `FormidableOptions.LiveProfile` at `ValidationProfile.Draft`, which is what holds the
submit bucket back from the live channel.

```csharp
public class BriefValidator : DraftSubmitValidator<Brief>
{
    protected override void ConfigureDraftRules() =>
        RuleFor(b => b.Title).MaximumLength(60).WithMessage("Title is 60 characters max");

    protected override void ConfigureSubmitRules() =>
        RuleFor(b => b.Title).NotEmpty().WithMessage("Title is required to submit");
}
```

```csharp
Options.LiveProfile = ValidationProfile.Draft;
```

The bucket and the profile answer different questions. Buckets are the authoring axis: draft
rules ask "is this value malformed?" and treat an empty value as fine, submit rules ask "is this
value present?" and treat default values as missing — two axes, so one mistake never produces two
messages. They also decide what a lenient draft save enforces, since a "save draft" button asks
the validator for `ValidationProfile.Draft` directly rather than going through the form's submit
pipeline. `LiveProfile` is the runtime axis, and it alone decides which of those rules the live
channel evaluates: unset, it evaluates all of them, and an engaged field's `Title is required to
submit` appears the moment the visitor clears a title they had typed. `ValidationProfile.Draft`
is what makes that message wait for the submit button instead.

`DraftSubmitValidator<T>` checks the bucket convention once, at construction: a property carrying
the same kind of rule in both buckets invokes `OnOverlappingRuleAxes` (a `Debug` warning by
default, overridable).

Worth being deliberate about, since it is a trade rather than a tidier default. Holding the
message back also holds back the one the visitor most wants: the field they just emptied says
nothing until they press a button and are told they cannot. Reach for it where the rule is
expensive rather than merely strict — the next recipe is that case in full.

**Read:** [Profiles](profiles.md), [Options](options.md#liveprofile).
**Samples:** [`/server`](../samples/Formidable.Sample/Pages/ServerRoundTrip.razor) does the
narrowing; [`/profiles`](../samples/Formidable.Sample/Pages/Profiles.razor) and
[`/workout`](../samples/Formidable.Sample/Pages/Workout.razor) are the contrast, left on the
defaults so their presence rules answer live.

### I want to narrow what the live channel validates

**Set:** `FormidableOptions.LiveProfile`. It is nullable and defaults to `null`, meaning the live
channel evaluates whatever `SubmitProfile` selects. Give it a profile and the live channel
evaluates that one instead.

```csharp
Options.LiveProfile = ValidationProfile.Draft;
// SubmitProfile is left at its default, ValidationProfile.Submit - only the live channel narrows.
```

`ValidationProfile.Draft` is the usual choice: the default (unnamed) rules alone, so every
`"Submit"`-ruleset rule waits for the submit button and the refresh behind it. Any profile works,
though — a wizard step's own ruleset, an approval stage, a selection composed for the purpose.

The reason to reach for this is cost, not tidiness. A live pass runs on every committed change,
so a rule that calls a server, hashes something large, or walks a long collection is a rule worth
keeping off that path. Strictness on its own is not a reason: what stops a form nagging is that a
live verdict is filed only for *engaged* fields, so an untouched field says nothing whatever its
rules would report, and narrowing silences the field the visitor is actually working in along
with everything else.

**Keeping one rule live while the rest wait.** Narrowing is per profile, so a rule that has to
answer live needs membership in the narrow profile as well as in `"Submit"` — without existing
twice. A ruleset name may join several names with a comma, which tags every rule inside with all
of them, and that is what one declaration answering to two moments looks like:

```csharp
public class SignupValidator : DraftSubmitValidator<Signup>
{
    protected override void ConfigureDraftRules()
    {
        // format/malformed-value rules, unaffected by any of this
    }

    protected override void ConfigureSubmitRules()
    {
        // the expensive presence and business rules the narrowing keeps off the live path
    }

    protected override void ConfigureAdditionalProfiles() =>
        Profile("Submit,Live", () =>
            RuleForEach(s => s.Guests).ChildRules(guest =>
                guest.RuleFor(g => g.Name).NotEmpty().WithMessage("Guest name is required")));
}
```

```csharp
Options.LiveProfile = ValidationProfile.Named("Live", includeDefaultRules: true, "Live");
// SubmitProfile is left at its default, ValidationProfile.Submit - the rule is already a member.
```

`Profile(name, ...)` splits a joined name the way FluentValidation's own `RuleSet` does — on `,`
or `;`, trimming each part — so the one call above declares `"Submit"` and `"Live"` separately and
tags the guest-name rule with both. Each part registers for ruleset-name verification too, so a
typo'd `LiveProfile` ruleset name throws loudly instead of silently selecting nothing. A
`ValidationProfile` names one ruleset per entry, though: FluentValidation splits a joined name
where a rule is *declared*, never where a selection is made, so
`ValidationProfile.Named("Live", true, "Submit,Live")` would match neither ruleset — and `Named`
rejects it rather than let it select nothing.

What the shape buys, and what it doesn't. The guest-name rule answers live on the field the
visitor engages, exactly as it would on the defaults; every other rule in `ConfigureSubmitRules()`
waits for submit; and a draft save stays clean either way, because it validates against
`ValidationProfile.Draft` directly, which selects neither `"Submit"` nor `"Live"`.

**Nothing on the server needs to change.** A plain `Submit` validation carries the shared rule
along, since it is a member of `"Submit"` too — MVC's `[Validate(Profile = "Submit")]` and a
minimal-API route with no profile argument both resolve to it. Naming the narrow profile
server-side would be a trap rather than a redundancy: `"Live"` alone resolves to the default rules
plus only the `"Live"` ruleset, so every Submit-only rule on the model would silently stop being
enforced. Composite profiles exist for a genuinely separate live/submit split worth naming on
purpose (`ValidationProfile.Named(name, true, "Submit", "SomeOtherRuleset")`, selecting several
rulesets in one profile), but only the minimal-API route can take one: it accepts a
`ValidationProfile` instance directly, while `[Validate(Profile = "...")]` resolves a name through
`ValidationProfile.FromName`, which can only build default rules plus one same-named ruleset.

```csharp
app.MapGroup("/api/signups").Validate<Signup>(
    ValidationProfile.Named("SubmitPlusExtra", true, ValidationProfile.SubmitRuleSetName, "SomeOtherRuleset"));
```

**The cost, honestly.** On a validator with a rule-level seam (the FluentValidation adapter,
unless `ClassLevelCascadeMode.Stop` opts it out), sharing a rule between the two profiles does not
execute it twice. The pair looks disjoint by *name* — `ValidationProfile.Submit`'s own
ruleset list is just `["Submit"]`, with `"Live"` nowhere in it — but the engine reuses verdicts by
*rule*, and a `Profile("Submit,Live", ...)` rule is one declared rule however many names reach it.
A post-submit edit runs each shared rule once across its live pass and the refresh that follows:
whichever lands first executes it, and the other serves the stored verdict.
`FormValidationEngineRuleReuseTests.One_post_submit_edit_runs_each_selected_rule_at_most_once_across_both_passes`
pins exactly this, on this exact shape. Where the seam is absent, every pass validates its whole
profile instead, so the shared rule does run in both — correct, and priced exactly as it reads.

What narrowing does change is who pays for the rules it dropped. Two things behind the live
channel go on wanting a submit-profile answer, and neither of them gets one from a narrowed live
pass. `FormidableOptions.TrackFormValidity`'s probe evaluates `SubmitProfile` whatever
`LiveProfile` says, so under a narrowing the probe becomes the more expensive of the two, with the
async and server-shaped rules among the difference ([Options](options.md#trackformvalidity)). And
the `Valid` state class asks whether a submit would pass, which is a form-wide answer that goes
current only once every submit-selected rule has one. A narrowed live pass never supplies that, so
no field wears a confirmation border on the strength of one. What puts green on the form is a
submit, a refresh, that probe, or a page saying what its freshly loaded values have earned
([CSS and accessibility](css-and-accessibility.md#need-to-know)).

**Read:** [Profiles](profiles.md) (custom profiles), [Options](options.md#liveprofile),
[Disclosure](disclosure.md#the-live-channel-plays-by-its-own-rule),
[The refresh runs only what the live pass did not](async-validation.md#the-refresh-runs-only-what-the-live-pass-did-not)
(the verdict store the reuse rides on).
**Sample:** [`/server`](../samples/Formidable.Sample/Pages/ServerRoundTrip.razor) — `LiveProfile`
pointed at `Draft` over an empty draft bucket, so the server stays the only judge.

### I want an async check with a pending indicator

**Set:** put the `MustAsync` rule in the draft bucket, honour the `CancellationToken` it hands
you, and render the indicator from `field.State.IsValidating` inside a `FormidableField` (kit
inputs also append the `Pending` class on their own). The bucket is about meaning here rather
than timing — a live pass evaluates both buckets by default, and the draft bucket is where a
uniqueness check belongs if a draft save should answer it too.

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
refresh, and form-wide during a submit. A draft load is the one pass it reports for no field at
all: that pass answers for the whole model, so the form-wide flag is true and a page-level spinner
works, but nobody asked for it and no field is waiting on it. One pass runs at a time: a newer live
pass supersedes an older one, and the winner's verdict answers every engaged field — the
superseded pass's fields included, since they were engaged before the winner began — while the
debounced refresh defers to a live pass still in flight and re-arms rather than cancelling it.

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

### I want to open a form on values the visitor did not type

**Set:** nothing. Fill the model the form is already bound to, then call
`DiscloseLoadedValuesAsync()` on the component you captured with `@ref`.

```csharp
        _proposal.Title = "Progressive disclosure in practice";
        _proposal.ContactEmail = "ada.lovelace";
        _proposal.Summary = string.Empty;

        await _form!.DiscloseLoadedValuesAsync();
```

*Excerpt from `samples/Formidable.Sample/Pages/DraftLoad.razor.cs`*

Fill the instance, rather than replacing it. A new instance reaches the form as a `Model`
parameter, and a parameter arrives on the form's next render — which cannot happen between
the fill and the call, so the call would run against the engine still bound to the old object, find
every field of it empty, and do nothing at all. If the values genuinely arrive as a new instance,
bind it with `@bind-Model` and make the call from the render that follows the swap, once the form
has rebuilt its engine around it.

Writing model properties notifies nothing, so without that last line a form loaded from a saved
draft looks pristine however good or bad its contents are: every state class and every live message
waits on a committed change. The call validates the whole model under `SubmitProfile` and then
decides field by field, on whether the field holds a value — a good value is confirmed, a wrong
one discloses its message, and a field nobody has reached stays silent and unstyled. That third
outcome is the point of doing it this way rather than marking everything touched: a bad saved value
sitting silent in a row of green reads as "not filled in yet" rather than "this one is wrong".

`FormidableValidator` carries the same method on the same terms, so an attached form gets this
without a `FormidableForm` in sight. Call it once, after the values land — from the handler that
loaded them, or from `OnAfterRenderAsync(firstRender: true)` — and from the renderer's
synchronization context. A form that never calls it behaves exactly as it always has.

Model an optional value as `T?` rather than `T` if a load has to read it exactly: "holds a value"
means what `NotEmpty()` means against the declared type, so a saved `false` in a `bool?` is an
answer while a `bool` sitting at its default reads as "not filled in". And seed the default rather
than a placeholder your own rules reject — a seeded value is a value, and the form will say so
the moment the draft loads.

**Read:** [Component kit](component-kit.md#saying-what-loaded-values-have-earned).
**Sample:** [`/draft-load`](../samples/Formidable.Sample/Pages/DraftLoad.razor).

### I want to mark fields required when the rules cannot say so

**Set:** `FormidableOptions.RequiredOverride`, returning a `FieldRequirement` for the fields you
are declaring and `null` for everything else.

```csharp
    _options = new FormidableOptions
    {
        RequiredOverride = field => field.FieldName switch
        {
            nameof(Booking.GuestName) => FieldRequirement.Required,
            nameof(Booking.Reference) => FieldRequirement.NotRequired,
            _ => null,
        },
    };
```

[`FormidableRequiredIndicator`](component-kit.md#formidablerequiredindicatortvalue) reads the
validator's own rules, and what it can read is presence written as FluentValidation's `NotEmpty()`
or `NotNull()`. The shapes below draw no mark, and `FieldRequirement.NotRequired` means "not known
to be required" rather than "proven optional":

- presence written as a predicate, `Must(s => !string.IsNullOrWhiteSpace(s))`;
- any field of a validator that cannot be inspected at all;
- a presence rule that carries a condition — a `When`/`Unless`, or a collection rule's per-row
  `Where` filter, judged row by row — which answers `ConditionallyRequired` and draws nothing,
  because whether the demand applies cannot be decided without evaluating the condition against
  the model.

A child validator scoped by the `SetValidator` call itself
(`RuleFor(x => x.Address).SetValidator(new AddressValidator(), "Admin")`) is read under the
selection FluentValidation runs it with, built from those ruleset names and replacing the
profile's own — a rule tagged into those rulesets demands its field whenever the holding rule is
selected, and any other rule inside the child, tagged into a set the call does not name or not
tagged at all, draws no mark anywhere, because FluentValidation runs it under no profile.

The override answers before any of that, and it declares in both directions: `Required` marks a
field the rules cannot be read to demand, `NotRequired` unmarks one they can. It decides the
marker and the input's `aria-required` together, so the two cannot disagree. It is invoked on
every ask, once per bound component per render, so keep it a cheap pure read.

**Read:** [Options](options.md#requiredoverride), [Component
kit](component-kit.md#formidablerequiredindicatortvalue).

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
renderable, return `true`, and the summary retries the focus once. `FormidableForm` takes the
identical parameter for its own blocked-submit auto-focus, so a visitor who never opens the
summary at all still lands on a field the same way — wire the same callback to both.

**Read:** [CSS and accessibility](css-and-accessibility.md),
[Component kit](component-kit.md).
**Samples:** [`/scroll-focus`](../samples/Formidable.Sample/Pages/ScrollFocus.razor),
[`/virtualized`](../samples/Formidable.Sample/Pages/Virtualized.razor),
[`/vanilla`](../samples/Formidable.Sample/Pages/VanillaInterop.razor),
[`/workout`](../samples/Formidable.Sample/Pages/Workout.razor).

### I want a modal dialog to announce a blocked submit

**Set:** open the dialog from `OnInvalidSubmit` and suppress the form's own focus move in the same
handler, give the summary inside the dialog a `PrepareFocus` that dismisses it, and ask for the
move yourself when the dialog closes.

```razor
<FormidableForm @ref="_form" Model="_request" OnValidSubmit="Save" OnInvalidSubmit="AnnounceAsync">

    @* the form's own fields and buttons *@

    <AnnouncementDialog @ref="_announcement" ReturnFocusTo="_submitButton"
                        OnDismissed="ReturnToFirstErrorAsync"
                        Heading="This form is not ready to send">
        <FormidableSummary Show="SummaryFilter.Errors" GroupByField="true" MaxItems="4"
                           PrepareFocus="DismissAnnouncementAsync">
            <ItemTemplate Context="entry">@NameOf(entry.Issue)</ItemTemplate>
            <OverflowTemplate Context="held">@held.Count more to fix</OverflowTemplate>
        </FormidableSummary>
    </AnnouncementDialog>
</FormidableForm>
```

```csharp
    private async Task AnnounceAsync(FormidableInvalidSubmitContext context)
    {
        // Said where it is known: the dialog is about to cover the form.
        context.SuppressFirstErrorFocus();
        await _announcement!.OpenAsync();
    }

    // Whatever your dialog raises once the visitor has closed it with Close or Escape. Those are
    // the ways out that name no field, so this is what leaves them on one.
    private async Task ReturnToFirstErrorAsync() => await _form!.FocusFirstErrorAsync();
```

Three rules decide whether that works: two about *when* focus moves, and one about the ways out
of the dialog that move it at all.

**Suppressing the form's own move is not optional.** With nothing said,
`FocusFirstErrorOnInvalidSubmit` has `FormidableForm` move focus to the first error immediately
after the handler that opened the dialog, inside the same `SubmitAsync` call, and the caret lands
in a field the overlay is covering. Wire a dismissing `PrepareFocus` to the form as well and the
outcome is worse still: that move runs the callback, so the dialog closes the instant it appeared.
Suppress it and the moves left are the ones the visitor asks for: clicking an entry, which is
the move `PrepareFocus` is there to prepare, and the ways out the next rule covers.
`FocusFirstErrorOnInvalidSubmit="false"` says the same thing about every submit the form will
ever run, including the ones where no dialog opens; the handler says it about the submit in front
of it. Attach mode has no such handler and needs none: `FormidableValidator` focuses inside
`ValidateForSubmitAsync()` before that call returns to the page that would have opened the dialog,
so set `FocusFirstErrorOnInvalidSubmit="false"` there and call `FocusFirstErrorAsync()` when you
want the move.

**Something has to focus the ways out that name no field.** A visitor who closes the dialog with
*Close* or Escape has picked nothing, so no `PrepareFocus` runs and the summary moves nothing;
without the `FocusFirstErrorAsync()` above, focus goes wherever the dialog returns it and the
visitor is left to find the first problem by eye. It is the same move the submit would have made,
so a visitor who closes the dialog without choosing lands where the form would have put them had
the dialog never opened.

**The dismissal has to finish before it reports back.** `PrepareFocus` is awaited, so what it
completes on decides what the focus move lands in. A dialog does not disappear on the state change
that starts its close: a transition finishes, an overlay and any scroll lock come off, and the
dialog hands focus back to whatever opened it. That hand-back is what takes a premature move
straight back, leaving the visitor on the button they pressed rather than the field they chose.
Complete the callback on the dialog's own closed event.

The dialog itself is yours: the library ships no dialog and no styling. What the kit brings is the
list the dialog holds, and a dialog has room for a list of names rather than a list of complaints.
[`ItemTemplate`](component-kit.md#formidablesummary) renders the field's name,
[`GroupByField`](component-kit.md#one-entry-per-field) collapses a field failing two rules into one
entry, and [`MaxItems`](component-kit.md#capping-the-list) with `OverflowTemplate` caps the list and
hands the entries it dropped to a line of your own, counted here because a dialog has room for a
number and not for a second list. `SubmitOutcome.VisibleErrorSummary` is already the distinct
names, so a line counting fields needs no counting of its own — as long as the names and the
fields agree. `VisibleErrorSummary` is distinct by name where `GroupByField` is distinct by field:
two fields carrying one `WithName(...)` leave the count one short of the list, and one field whose
two rules carry different names leaves it one long.

`NameOf` above is the page's own one-liner, and it is needed because `Issue.DisplayName` is
nullable: the engine's model-level issues carry none, so an entry rendering the name alone would
render nothing for the defensive gate's explanation or for a validator fault. Fall back to the
issue's `Path`, and for an issue naming no field at all, to
[`ModelLevelDisplayName`](options.md#modelleveldisplayname) read off the engine's options —
`Engine.Options` on the root you hold with `@ref`. That option is the name
`VisibleErrorSummary` already lists such an entry under, so reading it is what keeps a re-voiced
wording from having to be written twice.

**Read:** [Component kit](component-kit.md#preparefocus).
**Sample:** [`/dialog-submit`](../samples/Formidable.Sample/Pages/DialogSubmit.razor).

### I want the summary ordered by where fields appear on screen

**Set:** register your own `IFormidableFieldOrderService` ahead of `AddFormidableBlazor()`, which
keeps a registration already there. Map each field to the id its element carries, ask the browser
where those elements actually are, and map the answer back.

```csharp
using Formidable.Blazor;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;

public sealed class VisualOrderService : IFormidableFieldOrderService
{
    private readonly IJSRuntime _js;

    public VisualOrderService(IJSRuntime js) => _js = js;

    public async ValueTask<IReadOnlyList<FieldIdentifier>?> OrderAsync(
        IReadOnlyList<FieldIdentifier> fields)
    {
        // Last one wins, the way the shipped service resolves it: two fields can only ever agree
        // on an id by colliding, and a throw over that would cost the page its reading order.
        var byId = new Dictionary<string, FieldIdentifier>();
        foreach (var field in fields)
        {
            byId[FormidableFieldId.For(field)] = field;
        }

        var ids = byId.Keys.ToArray();

        // The ids are one argument — the array the script iterates — so they travel wrapped.
        var ordered = await _js.InvokeAsync<string[]>("visualOrder", new object?[] { ids });
        if (ordered is null)
        {
            return null;
        }

        return ordered.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
    }
}
```

```javascript
window.visualOrder = ids => ids
    .map(id => ({ id, element: document.getElementById(id) }))
    .filter(entry => entry.element)
    .sort((a, b) => {
        const first = a.element.getBoundingClientRect();
        const second = b.element.getBoundingClientRect();
        return first.top - second.top || first.left - second.left;
    })
    .map(entry => entry.id);
```

```csharp
builder.Services.AddScoped<IFormidableFieldOrderService, VisualOrderService>();
builder.Services.AddFormidableBlazor();
```

The shipped service sorts by `compareDocumentPosition`, which is the order the *markup* declares.
That is the right answer almost always, and the wrong one exactly when the layout disagrees with
the markup: a two-column form, or a `flex` container whose children carry `order`, puts fields in
front of the visitor in a sequence nothing in the markup states. Measuring is the only way to
learn that sequence, measuring means the browser, and the browser means async — which is why this
seam is public and why it is a service rather than a delegate.

Two parts of the contract are worth honouring in your own implementation. Answer `null`, not an
empty list, when you could not resolve an order at all: the form retries `null` on a later render
and takes an empty list as the settled answer that none of these fields are on the page. And
decide deliberately where the model-level field goes. It arrives in the request like any other,
with an empty `FieldName` and its id on the `<form>` element, and it carries the all-suppressed
gate's explanation and any validator fault. `compareDocumentPosition` puts it first for free,
since the form contains everything in it; a rect comparison can tie with the first field instead,
so put it at the front yourself if you want the shipped behaviour.

If the order you want does not depend on the layout — blocking fields first, one section ahead of
another — [`FormidableOptions.OrderIssues`](options.md#orderissues) re-sorts what the shipped
service already resolved, and costs no JavaScript of your own.

**Read:** [Component kit](component-kit.md#the-order-entries-appear-in),
[Options](options.md#orderissues).
**Sample:** no page. Every sample form lays its fields out top to bottom, where document order and
visual order are the same answer.

### I want my own summary markup

**Set:** nothing, in most cases — check first that
[`FormidableSummary`](component-kit.md#formidablesummary) cannot be shaped into what you want,
because it carries wiring a hand-rolled list has to rebuild. `ItemTemplate` decides what an entry
says, `GroupByField` gives you one entry per field rather than per issue, and `MaxItems` with
`OverflowTemplate` caps the list and says what stands in for the rest. Between them that is the
field-name list, deduplicated and capped. The click is none of their business: the component wires
every entry to the focus service itself, and that wiring is part of what a hand-rolled list
rebuilds.

A surface those cannot reach — a dialog laid out as a grid, a status bar, a wizard's step
indicator — reads the same two seams the component reads:

```csharp
using Formidable;
using Formidable.Blazor;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;

public partial class MissingFieldList : ComponentBase, IDisposable
{
    [CascadingParameter]
    private FormidableFormContext Context { get; set; } = default!;

    [Inject]
    private IFormidableFocusService Focus { get; set; } = default!;

    private IFormValidationEngine? _subscribed;

    private List<(string Name, FieldIdentifier Field)> Entries =>
        Context.Engine.GetVisibleIssues()
            .Where(visible => visible.Issue.Severity == ValidationSeverity.Error)
            .GroupBy(visible => visible.Field)
            .Select(group => (Name: NameOf(group.First().Issue), Field: group.Key))
            .ToList();

    private string NameOf(ValidationIssue issue) =>
        issue.DisplayName ?? (issue.Path.Length == 0
            ? Context.Engine.Options.ModelLevelDisplayName
            : issue.Path);

    private async Task GoToAsync(FieldIdentifier field) => await Focus.FocusAsync(field);

    protected override void OnParametersSet()
    {
        if (ReferenceEquals(Context.Engine, _subscribed))
        {
            return;
        }

        if (_subscribed is not null)
        {
            _subscribed.StateChanged -= Redraw;
        }

        _subscribed = Context.Engine;
        _subscribed.StateChanged += Redraw;
    }

    private void Redraw(object? sender, FormidableStateChangedEventArgs e) => StateHasChanged();

    public void Dispose()
    {
        if (_subscribed is not null)
        {
            _subscribed.StateChanged -= Redraw;
            _subscribed = null;
        }
    }
}
```

```razor
<ul class="missing-fields" role="alert">
    @foreach (var entry in Entries)
    {
        <li>
            <button type="button" @onclick="() => GoToAsync(entry.Field)">@entry.Name</button>
        </li>
    }
</ul>
```

Five things the shipped component knows, which your own has to know too:

- **`GetVisibleIssues()` is the whole answer.** It is the same submit-then-live-deduped view the
  kit's message components read per field, computed from engine state on every ask, and it pairs
  each issue with the `FieldIdentifier` it resolved to, including the model-level identifier for
  a verdict about the form as a whole.
- **`Issue.DisplayName` is the user-facing name.** Where a rule set `WithName(...)`, that is the
  value; where none did, it is FluentValidation's own display name for the property. It is
  nullable, and two issues on one field can carry different names, since `WithName` applies to the
  rule that declared it. The engine's own two model-level issues (the validator fault and the
  all-suppressed gate's explanation) carry none, and neither does an error mapped from a
  `ProblemDetails` body, whose `errors` half is paths and messages only. Hence the two-step
  fallback above: the issue's `Path`, and for an issue naming no field at all
  [`ModelLevelDisplayName`](options.md#modelleveldisplayname), read from
  `Context.Engine.Options` rather than written out here. That option is the name
  `SubmitOutcome.VisibleErrorSummary` already lists such an entry under, so re-voicing that option
  changes your list and the engine's own summary together.
- **The order is the page's, under `FormidableForm`.** That root resolves where the fields sit and
  hands the answer to its engine, so entries arrive in the order a visitor reads them, with
  anything the page could not place last. `FormidableValidator` resolves no order, so a list
  attached to someone else's `EditForm` arrives in validator order instead (see
  [Component kit](component-kit.md#the-order-entries-appear-in)).
- **Focus goes through `IFormidableFocusService`.** Its currency is the `FieldIdentifier`, so the
  entry you rendered is already holding what the click needs. Injecting it also gets you whatever
  the app registered ahead of `AddFormidableBlazor()`. What you do not get for free is
  `FormidableSummary`'s miss recovery: a field that is not currently in the DOM has nothing to
  focus, and retrying after making it renderable is yours to write.
- **Subscribe to `StateChanged`, and keep the announcing element persistent.** The engine raises
  it on every pass, refresh and server apply; without the subscription the list is only as fresh
  as whatever else happened to re-render. And a live region announces reliably only when the
  element carrying the role was already in the DOM, so render the `<ul>` and its role from the
  first paint and let the items come and go inside it. That is the discipline behind the summary's
  own [two fixed-role regions](css-and-accessibility.md#formidablesummary-as-a-live-region).

**Read:** [Component kit](component-kit.md#formidablesummary),
[CSS and accessibility](css-and-accessibility.md#formidablesummary-as-a-live-region).
**Sample:** [`/summary-shape`](../samples/Formidable.Sample/Pages/SummaryShape.razor) works
`ItemTemplate`, `GroupByField`, `MaxItems` and `OverflowTemplate` over one fixed set of issues,
which is the check this recipe opens with. No page rolls its own list: every sample form uses
`FormidableSummary` itself.

### I want to use a native or third-party control

**Set:** wrap it in `FormidableField` and call `field.NotifyChanged()` from its change handler —
the context supplies `ElementId`, `CssClass`, `AriaInvalid`, `AriaDescribedBy`, `Requirement` and
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
`FormidableOptions.LiveProfile` or `SubmitProfile` at it. Setting `SubmitProfile` alone moves both
channels: `LiveProfile` unset means the live channel follows whichever instance `SubmitProfile`
holds, so an engaged field answers the custom profile between submits with no second setting to
keep in step. That is what the `/custom-profiles` sample's runtime toggle
demonstrates. `FormidableForm` picks up the `Options`
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

**Set:** three `FormidableOptions` strings, below — every rule's own message reaches you through
FluentValidation's localization untouched. Keep a rule's default message and FluentValidation
translates it by `CultureInfo.CurrentUICulture`; for your own text, pass a message *factory* so
the resource lookup happens each time the rule runs and follows the culture in force then.

```csharp
RuleFor(p => p.Age).Must(BeAWholeAgeInRange)
    .WithMessage(_ => ValidationMessages.AgeRange);

RuleFor(o => o.Description).NotEmpty().WithName("Order description");
```

`WithName(...)` display names land on `ValidationIssue.DisplayName`, and from there in
`SubmitOutcome.VisibleErrorSummary`, ready for a dialog without extra mapping. WebAssembly fixes
its culture at startup, so a language switch means storing the choice and applying it before the
host runs.

Three strings a form puts on screen come from the engine rather than from a rule, and all three
ship in English: [`DefensiveGateMessage`](options.md#defensivegatemessage), the explanation a
blocked submit shows when every field that failed is hidden;
[`ModelLevelDisplayName`](options.md#modelleveldisplayname), the name a model-level entry is
listed under in `SubmitOutcome.VisibleErrorSummary`; and
[`ValidationFaultMessage`](options.md#validationfaultmessage), what a pass that threw before it
could finish leaves on the form. Nothing else the kit renders is the library's own words: every
other message on a form was written by a rule, or came back from your server. Give these three a
resource lookup and the form is speaking your language:

```csharp
_options = new FormidableOptions
{
    DefensiveGateMessage = ValidationMessages.HiddenFieldsInvalid,
    ModelLevelDisplayName = ValidationMessages.ThisForm,
    ValidationFaultMessage = ValidationMessages.ValidationIncomplete
};
```

Each is read where the engine uses it, so a later culture change is a matter of assigning these
properties on the instance the form already holds, never of handing the form a new options
instance — which it accepts only alongside a new model
([`FormidableOptions` is read once](options.md#formidableoptions-is-read-once)). The one to know
about is `ValidationFaultMessage`: its issue is filed when the fault happens rather than rebuilt
at each read, so a change reaches the next fault and leaves one already on screen as it was.

Server-side, one string has no lever yet. Where a request binds its body to null, the endpoint
filter answers with `"A request body is required."` in the 400 body. That is a response rather
than something a form renders, so it takes a different kind of seam and does not have one today.

**Read:** [Profiles](profiles.md) (localization and display names),
[Engine options](options.md#defensivegatemessage).
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

### I want to wrap the validator without losing what it can do

**Set:** derive from `DelegatingModelValidator<TModel>`, override every member whose behaviour
changes, and pass the wrapper as the form's `Validator`.

```csharp
public sealed class StagedUploadsValidator(
    IModelValidator<Brief> inner,
    IReadOnlyList<Upload> staged)
    : DelegatingModelValidator<Brief>(inner)
{
    // What the rules should judge: the model the form holds, plus the uploads the visitor has
    // picked and the page has not committed yet.
    private Brief Staged(Brief model) => model.WithAttachments([.. model.Attachments, .. staged]);

    public override Task<ValidationReport> ValidateAsync(
        Brief model, ValidationProfile profile, CancellationToken cancellationToken = default) =>
        base.ValidateAsync(Staged(model), profile, cancellationToken);

    public override ValidationReport Validate(Brief model, ValidationProfile profile) =>
        base.Validate(Staged(model), profile);

    public override Task<RuleLevelResult> ValidateRulesAsync(
        Brief model, ValidationProfile profile, IReadOnlyList<RuleIdentity> rules,
        CancellationToken cancellationToken = default) =>
        base.ValidateRulesAsync(Staged(model), profile, rules, cancellationToken);
}
```

```razor
<FormidableForm Model="Model" Validator="_validator">
    @* the form's fields, unchanged *@
</FormidableForm>
```

`IModelValidator<TModel>` is the seam a form validates through, and two capabilities sit beside it
rather than inside it: `IRuleInspectingValidator<TModel>` answers what the rules demand of a field,
and `IRuleLevelValidator<TModel>` runs a chosen set of them in one pass. The shipped
FluentValidation adapter implements all three. A wrapper written against the seam alone compiles
and validates correctly and presents neither of the other two, and nothing reports the difference,
because the capability test a form makes reads the same for that wrapper as for a validator whose
rules genuinely cannot be read. Three things go quiet. Required markers and `aria-required` stop
appearing, since the requirement answer has no other source. `DiscloseLoadedValuesAsync` goes on
disclosing a wrong saved value and stops confirming a good one, since confirming needs the
validator's own list of the fields it has rules for. And every pass evaluates its whole profile for
itself, since there are no per-rule verdicts left to share. Mostly that is a cost. It is also a
difference in one place: confirming a field — the `formidable-valid` class, and the
`FieldState.WouldPassSubmit` behind it — needs a completed submit-profile answer that still
describes the model, and per-rule verdicts are what let a live pass leave one. Without them a live
pass leaves nothing to confirm from, so on a form that has run nothing else, a field whose value a
submit would accept wears no confirmation. Switching `TrackFormValidity` on closes that gap: its
probe answers the whole profile on every edit, and the same fields are confirmed either way.

`DelegatingModelValidator<TModel>` forwards all three interfaces to the validator it wraps, and
each capability tester answers what that validator answers rather than reporting a capability
because an interface is present. Where the wrapped validator implements neither optional
interface, each degrades the way its own interface documents: inspection reports the empty answer,
and rule-level selection and execution throw `NotSupportedException`.

Override every entry point that validates, not one of them. `Validate`, `ValidateAsync` and
`ValidateRulesAsync` all take a model, and which of them runs is the caller's choice: a form that
finds the rule-level capability present validates through `ValidateRulesAsync`, a set of rules at a
time, and reaches neither of the other two. A wrapper staging its model at `ValidateAsync` alone
therefore changes nothing that form sees.

State the wrapper reads from outside the model needs telling. Staging a new upload writes nothing
the form is watching: it raises no `EditContext` field-changed notification, so no live pass starts
and the edit counter every verdict is stamped with does not move. The store goes on serving what it
computed against the staged set before it, and the screen waits for an unrelated edit or a submit.
Call `NotifyChanged()` on the field the staged state belongs to whenever that state changes, the
same call a hand-wired control makes from its change handler:
[use a native or third-party control](#i-want-to-use-a-native-or-third-party-control) has the
shape.

**Read:** [Component kit](component-kit.md#formidableformtmodel) (what a form resolves, and the
rule that decides it),
[Open a form on values the visitor did not type](#i-want-to-open-a-form-on-values-the-visitor-did-not-type),
[Mark fields required when the rules cannot say so](#i-want-to-mark-fields-required-when-the-rules-cannot-say-so).
**Sample:** no page. The worked examples are Formidable's own tests, in
[`DelegatingModelValidatorTests.cs`](../tests/Formidable.Tests/DelegatingModelValidatorTests.cs).

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
| A new project won't build: `RZ9991` on every `@bind-Value` — `The attribute names could not be inferred from bind attribute 'bind-Value'` — plus a run of `RZ10012` warnings about unresolved component names. | The kit isn't in scope, so the Razor compiler reads `<FormidableInputText>` as plain markup and the `@bind-Value` on it as an attribute nothing can bind. The error describes binding syntax and never mentions the namespace; the diagnostic that does name it is `RZ10012`, which is a warning, and warnings are what a reader scrolls past to reach the errors. | Add `@using Formidable.Blazor` to `_Imports.razor`, and `using FluentValidation;` plus `using Formidable.Blazor;` to `Program.cs`. [Quickstart](quickstart.md). |
| A page won't build: `CS0103` — `The name 'context' does not exist in the current context`, or that sentence naming whatever a `Context="…"` called it — on a line inside a `<FormidableForm>`, with `RZ10012` warnings elsewhere in the same build. Or: the project builds clean and the page's own labels and headings appear while every Formidable component renders nothing. | The same missing namespace as the row above, on a page with no `@bind-Value` to turn it into a Razor error. An unresolved `<FormidableForm>` is plain markup, so it declares no typed fragment and no `context` variable for its body to read, and the C# error lands on the line doing the reading rather than on the element. Where nothing reads one either, `RZ10012` is the only diagnostic there is — and unless the project promotes warnings to errors, a warning does not fail the build, so the elements ship as markup the browser treats as unknown: their children still render, and the components themselves produce no output. | Add `@using Formidable.Blazor` to `_Imports.razor`, as above. And read the build's warnings, not only its errors: `RZ10012` says `Found markup element with unexpected name 'FormidableForm'. If this is intended to be a component, add a @using directive for its namespace.` — it names the fix that the C# error cannot. [Quickstart](quickstart.md). |
| A page won't build: `RZ9999` — `The child content element 'ChildContent' of component 'FormidableField' uses the same parameter name ('context') as enclosing child content element 'ChildContent' of component 'FormidableForm'`, with the same wording for a `Virtualize` inside a form, or a `FormidableValidator` inside an `EditForm`. | Both components take a typed `ChildContent`, and an unnamed typed fragment claims the implicit `context`. Nesting one inside another puts two claims on that one name. What collides is the declaration, not any use of it, so the diagnostic arrives whether or not either body ever reads `context` — `EditForm` has always charged the same rename. | Name either fragment: `Context="field"` on a `FormidableField`, `Context="gadget"` on a `Virtualize`, `Context="formidable"` on a `FormidableValidator` inside a consumer's own `EditForm` — or on the `FormidableForm` itself, which names the outer body and leaves the inner one on `context`. Every shipped sample names the inner one. [Component kit](component-kit.md#formidableformtmodel). |
| Submit answers with an HTTP 400: `The POST request does not specify which form is being submitted. To fix this, ensure <form> elements have a @formname attribute with any unique value, or pass a FormName parameter if using <EditForm>.` | The page is statically server-rendered, so the browser posts the form and no Blazor component ever sees the submit. The advice can't be followed either — `FormidableForm` has no `FormName` parameter to pass. | Add `@rendermode InteractiveServer` (or `@rendermode InteractiveWebAssembly`) to the page. `FormidableForm` refuses to render where the renderer reports itself static, naming that same fix in its own words; this 400 is what a host whose renderer says nothing answers instead. [Quickstart](quickstart.md). |
| A field says nothing until Submit is pressed. | Either nothing has engaged that field — a live verdict is filed only for a field something has engaged, so tabbing through one is not enough — or the form narrows `LiveProfile` past the rule, which is what holds a submit-ruleset rule back from the live channel. | Type into the field and commit the change (blur, under the default `UpdateOn`) and the message answers from there on, until the field leaves the rendered page. If the form sets `LiveProfile`, the narrowing is the cause: unset it to have the live channel follow the submit profile, or give the one rule membership in the narrow profile as well. [Validate while typing, on blur, or only at submit](#i-want-to-validate-while-typing-on-blur-or-only-at-submit), [presence rules wait for submit](#i-want-presence-rules-to-wait-for-submit-while-formats-answer-live), [narrow what the live channel validates](#i-want-to-narrow-what-the-live-channel-validates). |
| Clicking a summary entry does nothing. | Click-to-focus looks the field up by its deterministic id, and no rendered element carries it — a control the page renders itself, or a field with no input of its own. | Render the id: `id="@field.ElementId"`, or `FormidableFieldId.For(field)` plus `tabindex="-1"` on a container. [Every summary entry lands somewhere](#i-want-every-summary-entry-to-land-somewhere). |
| A hand-wired control never validates live, even though it picks up the state classes. | Its handler calls `MarkTouched()`, which marks the field touched without telling the `EditContext` a value changed — the field never engages, so no live pass ever answers for it, and a touched field with no errors is styled valid as soon as the engine can vouch that a submit would not fail it. | Call `field.NotifyChanged()` from the change handler. `MarkTouched()` belongs on blur, where there is no new value to judge. [Use a native or third-party control](#i-want-to-use-a-native-or-third-party-control). |
| Required markers and `aria-required` vanished, and a loaded draft stopped confirming the values its rules pass, after the form was given its own `Validator`. | The validator passed in wins whole. Rule inspection and rule-level execution are optional interfaces beside `IModelValidator<TModel>`, so a wrapper implementing only that seam presents neither, and the capability test a form makes reads the same for it as for a validator whose rules cannot be read at all. | Derive the wrapper from `DelegatingModelValidator<TModel>`, which forwards all three interfaces and answers each tester with the wrapped validator's own answer. [Wrap the validator without losing what it can do](#i-want-to-wrap-the-validator-without-losing-what-it-can-do). |
| A date input reports impossible years while it is being typed. | A native date input fires `change` once per segment, so validating on every change judges half-typed values. | Set `UpdateOn="InputUpdateMode.OnBlur"` so the pass waits for the value to settle. [Validate while typing, on blur, or only at submit](#i-want-to-validate-while-typing-on-blur-or-only-at-submit). |
| The server's error never appears, though client errors do. | The page posts from `OnValidSubmit`, so a rule only the server owns cannot speak until every client rule passes. | Expected ordering — clear the client errors and the server's verdict arrives last. Post without that gate if the server should answer first. [Validate on the server and show its verdict](#i-want-to-validate-on-the-server-and-show-its-verdict). |
| The server's errors land inline but its advisories show nowhere. | Server-declared errors bypass the disclosure registry; advisories defer to it, so an advisory whose field nothing rendered has nowhere to go. | Render the field (or a `FormidableFieldAnchor` for one the page draws itself); the browser console names each dropped advisory with `Formidable: issue at '...' is suppressed`. [Validate on the server and show its verdict](#i-want-to-validate-on-the-server-and-show-its-verdict). |
| A warning is on screen but the submit succeeded. | Warnings and infos never affect validity: `CanProceed` counts error-severity issues only. | That is the severity doing its job — give the rule error severity if it must block. [Advise without blocking](#i-want-to-advise-without-blocking). |
| Submit is blocked but no field shows a message. | Every failing field is unwatched and unrendered, so the defensive gate blocks with one model-level explanation instead of a silent no-op — and no background refresh takes that explanation away while nothing on screen explains the block. | Give that explanation a surface: a `FormidableSummary` lists it among everything else, and a form built on inline messages alone renders a [`FormidableModelMessage`](component-kit.md#formidablemodelmessage), which is fixed to the model-level field the explanation belongs to. Then check the browser console for `Formidable: issue at '...' is suppressed` (or watch `SuppressedIssueDiagnostic` for the same events in code), and check whether the rule needed a mirrored `.When(...)`. [Reveal fields conditionally without losing their rules](#i-want-to-reveal-fields-conditionally-without-losing-their-rules). |
| The summary names a field that is not on screen. | A field a submit has shown stays watched until the form passes or is reset, so hiding it afterwards takes the message off its own row without taking the entry out of the summary. A `DisclosureOverride` returning `true` also discloses fields that were never rendered. | Fix the value: the entry goes when the rule stops producing the issue, at the next pass to answer the submit profile. Keep the override where it is deliberate, as virtualized rows are. [Reveal fields conditionally without losing their rules](#i-want-to-reveal-fields-conditionally-without-losing-their-rules). |
| A message lingers after the value was fixed. | On the default `LiveProfile`, the live pass a commit starts re-answers the submit profile and takes the message with it — so a lingering message is one no such pass has answered for yet. A change that never committed starts none (under the default `UpdateOn` the commit is the blur); a `LiveDebounce` window holds one back until the typing quiets; and a `LiveProfile` narrowed past the rule leaves the message to the debounced refresh instead, 300 ms of quiet after the value commits, with that refresh waiting on any live pass still in flight. | Commit the change: leave the field, set `UpdateOn="InputUpdateMode.OnInput"` to commit on every keystroke, or call `field.NotifyChanged()` from a control the page wires itself. Where the debounce or the narrowing is deliberate, wait it out or tune `FormidableOptions.RefreshDebounce`. [Validate while typing, on blur, or only at submit](#i-want-to-validate-while-typing-on-blur-or-only-at-submit), [async check with a pending indicator](#i-want-an-async-check-with-a-pending-indicator). |
