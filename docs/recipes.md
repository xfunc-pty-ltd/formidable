# Recipes: from behaviour to configuration

Some of Formidable's behaviour comes from a handful of switches: bucket, profile, commit timing,
render state, severity, and the server's verdict. Each recipe opens with **Set:** — what to reach
for, or a note that nothing needs setting. Every one links to what explains it in full; a sample
page follows where one demonstrates it. Chasing a symptom instead of a goal?
[Troubleshooting](troubleshooting.md) answers by symptom.

### I want to validate while typing, on blur, or only at submit

**Set:** `UpdateOn` on the input — `InputUpdateMode.OnChange` (the default),
`InputUpdateMode.OnInput`, or `InputUpdateMode.OnBlur`.

This recipe sets which event delivers a change to the form (`UpdateOn`). Holding a rule back
until Submit takes `LiveProfile`: see **Only at submit** below, and
[presence rules wait for submit](#i-want-presence-rules-to-wait-for-submit-while-formats-answer-live).

```razor
<FormidableInputText @bind-Value="Model.Nickname"
                      UpdateOn="InputUpdateMode.OnInput" />
```

```csharp
protected override void ConfigureDraftRules() =>
    RuleFor(m => m.Nickname).MaximumLength(20).WithMessage("20 characters max");
```

**`OnChange`**, the default, commits the value on the element's `change` event, and the engine hears
about it then.

**`OnInput`** commits the value on the element's `input` event instead, so the draft-bucket rule
above answers as the visitor types. A `<select>` has no meaningful `input` event distinct from
`change`, so `OnInput` there behaves like the default.

**`OnBlur`** commits the value on `change` like the default, but the next `blur` delivers the
notification, one blur for however many commits piled up. That suits a control whose `change` event
fires more than once per logical edit (a native date input, once per segment).

```razor
<FormidableInputDate @bind-Value="Model.EventDate"
                     UpdateOn="InputUpdateMode.OnBlur" />
```

No `UpdateOn` mode changes what Submit, or the whole-form re-check that follows a post-submit edit,
validates. What `UpdateOn` decides is when a commit reaches the engine; after a submit each commit
also restarts that re-check's timer (`RefreshDebounce`, 300 ms).

| | A rule the live channel selects (by default, every one) | A rule it doesn't (`LiveProfile` narrowed past it) |
|---|---|---|
| `UpdateOn="InputUpdateMode.OnChange"` (default) | On the element's `change` event: the commit starts a check and the message lands on that field. | Not before submit. At submit; then, after each commit, in the whole-form re-check 300 ms later. |
| `UpdateOn="InputUpdateMode.OnInput"` | On every keystroke, and you see the answer for what you last typed. | Not before submit. At submit; then in the whole-form re-check 300 ms after the typing stops. |
| `UpdateOn="InputUpdateMode.OnBlur"` | When the field loses focus after a change, so a multi-segment control never starts a check mid-edit; a blur with no commit before it starts nothing. | Not before submit. At submit; then, after each blur-commit, in the whole-form re-check 300 ms later. |

Which column a rule falls in is a configuration choice rather than a property of the bucket it was
declared in: [`FormidableOptions.LiveProfile`](profiles.md#which-profile-runs-when) draws the line.
What keeps the left column from nagging is engagement rather than rule selection: a field you have
not changed shows no live message, however loudly its rule fails (a load of values disclosed with
`DiscloseLoadedValuesAsync` counts as a change for the fields it fills).

**Only at submit** means the right-hand column for every rule. There is no switch that turns the
live check off. `LiveProfile` draws the line by ruleset, so a rule the live profile does not select
waits for Submit.

`ValidationProfile.Draft` selects the default rules, so a validator whose rules are all default rules
gives it nothing to leave out. To hold rules back, put them in a ruleset: derive from
`DraftSubmitValidator<T>`, write the rules that should wait in `ConfigureSubmitRules()`, and set
`Options.LiveProfile = ValidationProfile.Draft`. That changes the validator's shape, not its rules.

**Read more:**

- [Profiles](profiles.md)
- [Options](options.md)
- [Disclosure](disclosure.md#why-isnt-my-message-showing-yet)
- [narrow what the live channel validates](#i-want-to-narrow-what-the-live-channel-validates)

Samples:

- [`/field-state`](../samples/Formidable.Sample/Pages/FieldStateVisualizer.razor)
- [`/profiles`](../samples/Formidable.Sample/Pages/Profiles.razor)
- [`/custom-profiles`](../samples/Formidable.Sample/Pages/CustomProfiles.razor) (`OnBlur` on the
  typed `FormidableInputDate`)
- [`/workout`](../samples/Formidable.Sample/Pages/Workout.razor) (`OnBlur` on the string-modelled
  pattern, both date fields)
- [`/server`](../samples/Formidable.Sample/Pages/ServerRoundTrip.razor) (every rule in
  `ConfigureSubmitRules()` and `LiveProfile` at `Draft`, so nothing answers before Submit)

### I want presence rules to wait for submit while formats answer live

**Set:** two things, because two separate decisions are involved. Derive from
`DraftSubmitValidator<T>` and put malformed-value rules (format, length, range) in
`ConfigureDraftRules()` and presence rules (`NotEmpty`, `NotNull`) in `ConfigureSubmitRules()`. Then
point `FormidableOptions.LiveProfile` at `ValidationProfile.Draft`, which is what holds the submit
bucket back from the live channel.

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

Buckets are the authoring axis: draft rules ask whether a value is malformed and treat an empty one
as fine, submit rules ask whether it is present, and a lenient draft save enforces the draft bucket
alone. `LiveProfile` is the runtime axis, and it alone decides which of those rules the live channel
runs.

The trade is real: the field the visitor just emptied says nothing until they press a button and are
told they cannot. Hold the message back where the rule is expensive rather than merely strict; the
next recipe is that case in full.

**Read more:**

- [Profiles](profiles.md)
- [Options](options.md#liveprofile)

Samples:

- [`/server`](../samples/Formidable.Sample/Pages/ServerRoundTrip.razor) does the narrowing
- [`/profiles`](../samples/Formidable.Sample/Pages/Profiles.razor) and
  [`/workout`](../samples/Formidable.Sample/Pages/Workout.razor) are the contrast, left on the
  defaults so their presence rules answer live

### I want to narrow what the live channel validates

**Set:** `FormidableOptions.LiveProfile`. It defaults to `null`, meaning the live channel evaluates
whatever `SubmitProfile` selects; give it a profile and it evaluates that instead.

This recipe narrows which rules run as you edit. Live messages gated on their field being on
screen are [`LiveDisclosure`](options.md#livedisclosure); a form silent until Submit is
[the first recipe's last case](#i-want-to-validate-while-typing-on-blur-or-only-at-submit).

```csharp
Options.LiveProfile = ValidationProfile.Draft;
// SubmitProfile is left at its default, ValidationProfile.Submit - only the live channel narrows.
```

`ValidationProfile.Draft` is the usual choice: the default rules alone, leaving every
`"Submit"`-ruleset rule to the submit button and the whole-form re-check after it.

Reach for this on cost rather than strictness. The live channel checks on every committed change, so
a rule that calls a server or walks a long collection is worth keeping off it.

**Keeping one rule live while the rest wait.** That rule needs membership in the narrow profile as
well as in `"Submit"`, without existing twice:

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

`Profile(name, ...)` splits a joined name on `,` or `;`, trimming each part, so the call above tags
the guest-name rule into both `"Submit"` and `"Live"`. Selection is not where FluentValidation
splits a name, so `ValidationProfile.Named` rejects a joined entry. The rule then answers live where
the visitor engages it, while the rules in `ConfigureSubmitRules()` wait for submit.

**Nothing on the server needs to change:** the shared rule belongs to `"Submit"` too. Naming the
narrow profile there would quietly stop enforcing every Submit-only rule, since `"Live"` alone
resolves to the default rules plus the `"Live"` ruleset. A minimal-API route takes a composite
profile directly:

```csharp
app.MapGroup("/api/signups").Validate<Signup>(
    ValidationProfile.Named(
        "SubmitPlusExtra", includeDefaultRules: true,
        ValidationProfile.SubmitRuleSetName, "SomeOtherRuleset"));
```

**What it costs.** Narrowing changes which checks run a rule, not what the form enforces at submit.
After a submit, one edit still runs a shared rule once, however many profile names reach it (a rule
reached through `Include()` or `SetValidator` is the exception and may run again); only a validator
with a class-level cascade stop, or one that is not FluentValidation's `AbstractValidator`, re-runs
every rule on each check.

With `TrackFormValidity` on, narrowing saves nothing: the rules the live channel skipped still run
at each check to keep `IsFormValid` honest. And no field wears the `Valid` class on the strength of
a narrowed check, because that class means a submit would pass.

**Read more:**

- [Profiles](profiles.md)
- [Options](options.md#liveprofile)
- [`TrackFormValidity`](options.md#trackformvalidity)
- [Disclosure](disclosure.md#why-isnt-my-message-showing-yet)
- [why a rule runs once per edit](async-validation.md#does-the-library-ever-run-my-rule-twice-for-one-edit)
- [what puts green on a field](css-and-accessibility.md#what-puts-green-on-a-field)

Sample:

- [`/server`](../samples/Formidable.Sample/Pages/ServerRoundTrip.razor) — `LiveProfile` at `Draft`
  over an empty draft bucket, so the server is the only judge

### I want an async check with a pending indicator

**Set:** put the `MustAsync` rule in the draft bucket, honour the `CancellationToken` it hands you,
and render the indicator from `field.State.IsValidating` inside a `FormidableField` (kit inputs also
append the `Pending` class on their own). The bucket is about meaning rather than timing: the live
channel runs both buckets by default, and a uniqueness check belongs in the draft bucket if a draft
save should answer it too.

This recipe makes the check answer as the visitor types (`OnInput`). To have it answer less
often, leave `UpdateOn` at its default or set [`LiveDebounce`](options.md#livedebounce);
[Async validation](async-validation.md#how-do-i-stop-the-check-firing-on-every-keystroke)
has both.

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
    <em role="status">@(field.State.IsValidating ? "checking…" : null)</em>
</FormidableField>
```

Render the element always and let only the text inside it come and go, so assistive technology knows
about the live region before the content arrives.

The `UpdateOn="InputUpdateMode.OnInput"` above is what makes the check answer as the user types.
`field.State.IsValidating` is scoped to the fields the check concerns (as you type, the one you
changed); `IFormidableEngine.IsValidating` is the form-wide flag, true while any check but
`TrackFormValidity`'s runs. You only ever see the answer for what you last typed, because a newer
check cancels the older one, which is why the rule has to honour its token.

**Read more:**

- [Async validation](async-validation.md)
- [which fields show "checking…"](async-validation.md#where-does-checking-show-and-where-doesnt-it)
- [remember a slow check's answer](#i-want-a-slow-async-check-to-remember-its-answer)
- [why the element renders empty](css-and-accessibility.md#why-does-the-summary-render-empty-regions-before-anything-is-wrong)
- [Options](options.md) (`RefreshDebounce`)
- [CSS and accessibility](css-and-accessibility.md) (`Pending`)

Samples:

- [`/async`](../samples/Formidable.Sample/Pages/AsyncRules.razor)
- [`/field-state`](../samples/Formidable.Sample/Pages/FieldStateVisualizer.razor)

### I want a slow async check to remember its answer

**Set:** hold an `AsyncRuleMemo<TKey, bool>` as a field on the validator, with a window of seconds,
and write the rule with `MustAsyncMemoized` where `MustAsync` would go (`IUsernameDirectory`
below is your own lookup).

```csharp
using FluentValidation;
using Formidable;

public class UniqueHandleValidator : DraftSubmitValidator<Handle>
{
    private readonly AsyncRuleMemo<string, bool> _free = new(TimeSpan.FromSeconds(10));
    private readonly IUsernameDirectory _directory;

    public UniqueHandleValidator(IUsernameDirectory directory) => _directory = directory;

    protected override void ConfigureDraftRules() =>
        RuleFor(h => h.Username)
            .MustAsyncMemoized(_free, (username, token) => _directory.IsFreeAsync(username!, token))
            .WithMessage("That username is taken")
            .When(h => !string.IsNullOrEmpty(h.Username));

    protected override void ConfigureSubmitRules() =>
        RuleFor(h => h.Username)
            .NotEmpty()
            .WithMessage("A username is required");
}
```

`MustAsyncMemoized` is `MustAsync` with a lookup in front of it, so everything after it chains the
same way; a `null` value skips the memo and reaches the check directly. On the memoized path the
check runs under `CancellationToken.None`, since its answer belongs to every caller waiting on it: a
caller that gives up stops waiting while the call carries on.

There is deliberately no overload handing the check the model, since the memo keys its answers by
the value alone. A check that needs more than the value belongs on `MustAsync` directly.

Three things decide whether a memo is safe on a rule, and the first goes wrong silently:

- **Hold the memo on the validator.** Built inside the rule's own lambda it is rebuilt on every
  call and never hits.
- **Keep the check pure with respect to its key.** A coupon check that really asks "is this code
  valid *for me*", keyed on the code alone, answers the second customer with the first's answer.
- **Size the window to the pause it has to survive**; the sample's `MemoizedHandleValidator` uses
  ten seconds.

The container's lifetime for the validator is the memo's (`AddValidatorsFromAssembly` registers
scoped unless told otherwise): scoped keeps it inside the circuit or request that built it, and a
singleton hands every visitor the same held answers. `Invalidate(key)` and `Clear()` answer news that
a value the memo still holds as available has just been taken; both govern future lookups only.

`MustAsyncMemoized` reaches every property type but a nullable value type (`int?`, `Guid?` and
the rest), where inference fails and the compiler reports CS0411. Unwrap it and call `GetAsync`
directly:

```csharp
        RuleFor(t => t.SeatNumber)
            .MustAsync((seat, token) => seat is null
                ? Task.FromResult(true)
                : _seatFree.GetAsync(
                    seat.Value,
                    (key, ct) => _seating.IsFreeAsync(key, ct),
                    token));
```

A validator could hold its own last answer in a field instead; the memo adds joining a check
already running (two checks around the same moment share one round trip) and a bounded set rather
than one slot (a `RuleForEach` visiting every row hits on all of them).

**Read more:**

- [why the same value gets checked twice](async-validation.md#why-did-the-same-value-get-checked-twice-and-how-do-i-stop-it)
- [an async check with a pending indicator](#i-want-an-async-check-with-a-pending-indicator)

Sample:

- [`/async`](../samples/Formidable.Sample/Pages/AsyncRules.razor) supplies `MemoizedHandleValidator`
  in place of the sample's plain `HandleValidator`

### I want to validate on the server and show its verdict

**Set:** on the server, `Validate<TModel>(profile?)` on a minimal-API handler or route group, or
`[Validate]` on an MVC action or controller. On the client, deserialize the 400 body into
`FormidableValidationProblem`, guarding the parse against a body that is not one, and hand the
result to `_form!.ApplyServerIssues(...)`. Every issue lands on the field it names (errors whether
or not the field is rendered, advisories wherever that field can show them).

This recipe wires the round trip. For what a server error does once the visitor edits its
field, see
[Server integration](server-integration.md#what-happens-to-a-server-error-when-i-edit-the-field).

```csharp
var response = await Http.PostAsJsonAsync("/api/orders", Model);
if (!response.IsSuccessStatusCode)
{
    var problem = await response.Content.ReadFromJsonAsync<FormidableValidationProblem>();
    if (problem is not null)
    {
        _form!.ApplyServerIssues(problem);
    }
}
```

The guard above is half of it. A 400 from a proxy or a gateway is no verdict, often not JSON at
all, and the deserialize throws on such a body rather than returning `null`, so the parse itself
belongs inside a `try`. Pass `problem.ToIssues()` instead when the page wants the flattened issues
for something of its own. Each apply replaces the previous server verdict rather than adding to it.

**Normalize before posting** when the model implements `INormalizableModel`: the filters normalize
too, so cleaning first keeps the paths in the response lined up with the rows on screen. Call
`model.Normalize()` yourself, or set
[`FormidableOptions.NormalizeOnSubmit`](options.md#normalizeonsubmit) and the submit normalizes
before it validates.

**Read more:**

- [Server integration](server-integration.md)
- [what if the 400 is not Formidable's](server-integration.md#what-if-the-400-is-not-formidables)
- [Severity](severity.md)

Samples:

- [`/server`](../samples/Formidable.Sample/Pages/ServerRoundTrip.razor)
- [`/normalize`](../samples/Formidable.Sample/Pages/Normalize.razor)
- [`/workout`](../samples/Formidable.Sample/Pages/Workout.razor)

### I want to open a form on values the visitor did not type

**Set:** nothing. Fill the model the form is already bound to, then call
`DiscloseLoadedValuesAsync()` on the component you captured with `@ref`. Call it once, after the
values land, from the renderer's synchronization context.

This recipe is for a form filled all at once (a saved draft, a record opened for editing). A
single value your code changes wants `field.NotifyChanged()` instead
([Async validation](async-validation.md#why-did-a-value-my-code-changed-keep-its-old-message)).

```csharp
_proposal.Title = "Progressive disclosure in practice";
_proposal.ContactEmail = "ada.lovelace";
_proposal.Summary = string.Empty;

await _form!.DiscloseLoadedValuesAsync();
```

<!-- Excerpt from `samples/Formidable.Sample/Pages/DraftLoad.razor.cs` -->

Writing model properties notifies nothing, so without that last line a loaded form looks pristine
however good or bad its contents are. The call confirms a good value, discloses a wrong one, and
leaves a field holding nothing silent and unstyled; `FormidableValidator` carries the same method on
the same terms.

**Fill the instance rather than replacing it.** A new instance reaches the form as a `Model`
parameter, and a parameter arrives on the form's next render. The call would then run against the
old object the form is still bound to, find every field of it empty, and do nothing at all. Where
the values genuinely do arrive as a new instance, bind it with `@bind-Model` and call from the
render that follows the swap.

**Read more:**

- [Component kit](component-kit.md#saying-what-loaded-values-have-earned)

Sample:

- [`/draft-load`](../samples/Formidable.Sample/Pages/DraftLoad.razor)

### I want to mark fields required when the rules cannot say so

**Set:** `FormidableOptions.RequiredOverride`, returning a `FieldRequirement` for the fields you are
declaring and `null` for everything else. Most forms need none of it.

This recipe declares requiredness for particular fields. To render no marker anywhere,
set [`ShowRequiredIndicators`](options.md#showrequiredindicators) to `false`; that switch
leaves `aria-required` in place.

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
or `NotNull()`. `RequiredOverride` covers what reading misses: a validator written by hand, a
wrapper presenting only the `IModelValidator<TModel>` seam, and the rule shapes below.

The override answers before the validator's own rules are read, and it declares in both directions.
`Required` marks a field the rules cannot be read to demand, `NotRequired` unmarks one they can. It
decides the marker and the input's `aria-required` together, so the two cannot disagree.

**What the rules cannot say.** `FieldRequirement.NotRequired` means "not known to be required"
rather than "proven optional", and the shapes below draw no mark:

- Presence written as a predicate, `Must(s => !string.IsNullOrWhiteSpace(s))`.
- Any field of a validator that cannot be inspected at all.
- A presence rule that carries a condition (a `When`/`Unless`, or a collection rule's per-row
  `Where` filter) which answers `ConditionallyRequired`.

A child validator scoped by the `SetValidator` call itself
(`RuleFor(x => x.Address).SetValidator(new AddressValidator(), "Admin")`) is read under the
selection FluentValidation runs it with, built from those ruleset names and replacing the profile's
own.

A rule tagged into those rulesets demands its field whenever the holding rule is selected. Any other
rule inside the child, tagged into a set the call does not name or not tagged at all, draws no mark
anywhere, because FluentValidation runs it under no profile.

**Read more:**

- [Options](options.md#requiredoverride)
- [Component kit](component-kit.md#formidablerequiredindicatortvalue)
- [wrapping the validator](#i-want-to-wrap-the-validator-without-losing-what-it-can-do)

### I want to reveal fields conditionally without losing their rules

**Set:** if the field's relevance is decided by *data*, mirror the `@if` condition in the rule's
`.When(...)`. The rule then never runs for the path that hides it, so the alternate answer stays
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

If relevance is decided by *UI state* alone, leave the rule unconditional: the rule keeps running
and counting toward validity while the field is off screen, its message waits for a submit that
finds the field rendered, and `FormidableOptions.SuppressedIssueDiagnostic` reports each error held
back. When every failing field is hidden, the form blocks anyway and explains itself with one
model-level message rather than failing silently.

**Reach past what is rendered with `DisclosureOverride`:** it answers for an issue whose field
nothing renders, and at submit `true` reveals that field while `false` withholds the issue's own
contribution to revealing it. For rows a `Virtualize` container disposes, `KeepRegistered` holds an
already-showing error open, and an override on the collection covers rows it has never rendered.

**Read more:**

- [Disclosure](disclosure.md#why-is-the-submit-blocked-with-no-message-in-sight)
- [Options](options.md)

Samples:

- [`/disclosure`](../samples/Formidable.Sample/Pages/Disclosure.razor)
- [`/virtualized`](../samples/Formidable.Sample/Pages/Virtualized.razor)
- [`/workout`](../samples/Formidable.Sample/Pages/Workout.razor)

### I want to advise without blocking

**Set:** `.WithSeverity(Severity.Warning)` or `.WithSeverity(Severity.Info)` on the validator that
should advise. It attaches to the one it follows, so each component of a chain that should advise
needs its own.

```csharp
RuleFor(l => l.Tags).Must(tags => tags.Count <= 5)
    .WithSeverity(Severity.Warning)
    .WithMessage("More than five tags rarely helps discovery");
```

`SubmitOutcome.CanProceed` counts error-severity issues only, so a report of warnings and infos
submits successfully, and the same rule holds on the server. `FormidableFieldMessage` and
`FormidableSummary` render every severity, while a native `ValidationMessage` shows errors only.

**Read more:**

- [Severity](severity.md)
- [the warning lifetime](severity.md#how-long-does-a-warning-stay-on-screen)

Samples:

- [`/severity`](../samples/Formidable.Sample/Pages/SeverityLevels.razor)
- [`/workout`](../samples/Formidable.Sample/Pages/Workout.razor)

### I want every summary entry to land somewhere

**Set:** make sure a rendered element carries the field's deterministic id and can take focus. The
kit's inputs render `FormidableFieldId.For(field)` themselves and are focusable already. Anything
the page renders needs both explicitly, which for a container means `tabindex="-1"`.

```csharp
private string TeamsId => FormidableFieldId.For(Model, m => m.Teams);
```

```razor
<div id="@TeamsId" tabindex="-1">
    <FormidableCollectionMessage For="() => Model.Teams" />
</div>
```

A collection needs this by hand, since its rule fails against the list rather than against any one
input, and so does the model-level field on a page using `FormidableValidator`, which renders no
`<form>` to carry the id that `FormidableForm` puts on its own.

**For a field a summary click cannot reach**, give `FormidableSummary` a `FocusFallback`: make the
element reachable, return `true`, and the summary retries the focus once. `FormidableForm` takes the
identical parameter for its own blocked-submit auto-focus, so wire the same callback to both.

**Read more:**

- [CSS and accessibility](css-and-accessibility.md#how-do-i-compute-an-id-by-hand)
- [Component kit](component-kit.md#focusfallback)

Samples:

- [`/scroll-focus`](../samples/Formidable.Sample/Pages/ScrollFocus.razor)
- [`/virtualized`](../samples/Formidable.Sample/Pages/Virtualized.razor)
- [`/vanilla`](../samples/Formidable.Sample/Pages/VanillaInterop.razor)
- [`/workout`](../samples/Formidable.Sample/Pages/Workout.razor)

### I want a modal dialog to announce a blocked submit

**Set:** open the dialog from `OnInvalidSubmit`, and suppress the form's own focus move in the same
handler. Give the summary inside the dialog a `PrepareFocus` that dismisses it, and ask for the move
yourself once the dialog closes.

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

**Two nestings in that markup are load-bearing.** The summary sits inside the dialog so its
clickable entries are in front of the overlay. The dialog sits inside the form, since
`FormidableSummary` throws outside a root's cascade.

**Suppressing the form's own move is not optional:** left alone, the form moves focus to the first
error the moment the handler returns, and the caret lands in a field the overlay is covering. In
attach mode the page owns the submit call, so set `FocusFirstErrorOnInvalidSubmit="false"` on
`FormidableValidator` and call `FocusFirstErrorAsync()` once the dialog has closed.

**The dismissal has to finish before it reports back:** `PrepareFocus` is awaited, and a dialog is
not gone on the state change that starts its close, so complete the callback on the dialog's own
closed event.

The dialog itself is yours: the library ships none, and no styling either. `ItemTemplate` says what
an entry reads, `GroupByField` gives one entry per field, and `MaxItems` with `OverflowTemplate`
caps the list and stands a line of your own in for the rest.

**`NameOf` above is the page's own helper**, because `Issue.DisplayName` is nullable: a model-level
issue carries none, and neither does an error the server sent. Fall back to the issue's `Path`, and
to `ModelLevelDisplayName` where there is no path, read off `Engine.Options`.

**Read more:**

- [`PrepareFocus`](component-kit.md#preparefocus)
- [suppressing the automatic focus](component-kit.md#suppressing-the-automatic-focus)
- [asking for the first-error move](component-kit.md#asking-for-the-first-error-move)
- [`FormidableSummary`](component-kit.md#formidablesummary)
- [one entry per field](component-kit.md#one-entry-per-field)
- [`ModelLevelDisplayName`](options.md#modelleveldisplayname)

Sample:

- [`/dialog-submit`](../samples/Formidable.Sample/Pages/DialogSubmit.razor)

### I want the summary ordered by where fields appear on screen

**Set:** register your own `IFormidableFieldOrderService` ahead of `AddFormidableBlazor()`, which
keeps a registration already there. Map each field to the id its element carries, ask the browser
where those elements actually are, and map the answer back.

Seeing the order the rules were written in, rather than the page's? That is not this recipe's case:
under `FormidableForm`, page order is the default and needs no setting. A summary that stays in rule
order means the page's order never reached it.

Where no order was resolved (`FormidableValidator` in attach mode, or an app that registered no
order service), the
[migration guide](migration-guide.md#attach-mode-lists-issues-in-the-engines-order-not-the-pages)
has it. Where no rendered element carries the fields' ids (`id="@field.ElementId"` on a control the
page renders itself), none of them could be placed, and every one sorts last, in rule order.

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

The shipped service sorts by document order, the order the *markup* declares. That is the right
answer almost always, and the wrong one exactly where the layout disagrees with the markup: a
two-column form, or a `flex` container whose children carry `order`. Only the browser can say where
those fields really sit, and asking it is async, which is why this seam is a service rather than a
delegate.

**Answer `null` where you could not resolve an order at all.** An empty list says something
different (none of these fields are on the page) and the form takes that as settled. `null` says ask
again on a later render.

**Decide deliberately where the model-level field goes.** It arrives like any other, with an empty
`FieldName` and its id on the `<form>` element, which document order puts first for free. A rect
comparison can tie it with the first field instead, so put it at the front yourself to keep the
shipped behaviour.

An order that does not depend on the layout (blocking fields first, one section ahead of another)
is `FormidableOptions.OrderIssues` instead. It re-sorts what the shipped service already resolved,
and costs no JavaScript of your own.

**Read more:**

- [the order entries appear in](component-kit.md#the-order-entries-appear-in)
- [`OrderIssues`](options.md#orderissues)

Sample: no page. Every sample form lays its fields out top to bottom, where document order and
visual order are the same answer.

### I want my own summary markup

**Set:** nothing, in most cases — check first that
[`FormidableSummary`](component-kit.md#formidablesummary) cannot be shaped into what you want.
`ItemTemplate`, `GroupByField` and `MaxItems` with `OverflowTemplate` between them give a field-name
list, deduplicated and capped. The click is none of their business: the component wires every entry
to the focus service itself, which a hand-rolled list rebuilds.

A surface those cannot reach (a grid-laid dialog, a status bar) reads what the component reads:

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

    private IFormidableEngine? _subscribed;

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

What the shipped component knows, yours has to know too:

- **`GetVisibleIssues()` is the whole answer.** It is the view the kit's message components read
  per field, computed on every ask, each issue paired with the `FieldIdentifier` it resolved to,
  the model-level identifier included.
- **`Issue.DisplayName` is the user-facing name, and it is nullable.** A model-level issue carries
  none, and neither does an error the server sent. Hence the two-step fallback above, ending at
  `ModelLevelDisplayName` read from `Context.Engine.Options`, so re-voicing that option changes
  your list and the shipped summary together.
- **The order is the page's under `FormidableForm`,** which resolves where the fields sit.
  `FormidableValidator` resolves no order, so a list inside someone else's `EditForm` arrives in
  validator order.
- **Focus goes through `IFormidableFocusService`,** whose currency is the `FieldIdentifier` your
  entry already holds. `FormidableSummary`'s miss recovery is not free: reading the `false` and
  retrying once the element is reachable is yours.
- **Subscribe to `StateChanged`, and keep the announcing element persistent.** The engine raises it
  whenever validation or field state changes; without the subscription the list is only as fresh
  as whatever else re-rendered. Render the `<ul>` and its role from the first paint, so assistive
  technology knows the element before anything arrives in it.

**Read more:**

- [`FormidableSummary`](component-kit.md#formidablesummary)
- [the order entries appear in](component-kit.md#the-order-entries-appear-in)
- [the summary as a live region](css-and-accessibility.md#how-does-the-summary-announce-to-a-screen-reader)
- [`ModelLevelDisplayName`](options.md#modelleveldisplayname)

Sample:

- [`/summary-shape`](../samples/Formidable.Sample/Pages/SummaryShape.razor) works `ItemTemplate`,
  `GroupByField`, `MaxItems` and `OverflowTemplate` over one fixed set of issues. No page rolls its
  own list.

### I want to use a native or third-party control

**Set:** wrap it in `FormidableField` and call `field.NotifyChanged()` from its change handler. The
context supplies `ElementId`, `CssClass`, `AriaInvalid`, `AriaDescribedBy`, `Requirement` and
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

Reach for `FormidableFieldAnchor` instead when the control already notifies the `EditContext`
itself, as every native `InputBase` descendant does, and only needs registering.

**A native date or number input needs no seam at all:** where the model is genuinely a `DateOnly` or
a `decimal`, reach for `FormidableInputDate`/`FormidableInputNumber` with
`UpdateOn="InputUpdateMode.OnBlur"`. A `change` per typed segment then never starts a check
mid-edit. Both convert through `CultureInfo.InvariantCulture`, so the model gets the typed value
with no culture in the way.

**Where the model is a string holding one**, splat the type onto `FormidableInputText` and parse
that string by hand (the pattern `/workout`'s own date fields use).

Write the model in `@onchange` and call `NotifyChanged()` from `@onblur` by hand only for a control
this seam exists for. That means one that is not a plain `<input>` or `<textarea>`, or whose value
does not bind through `value`; a plain checkbox binds through `checked`, so it needs the seam too.

Native `InputBase` components inside a `FormidableForm` pick up the configured state classes on
their own, pending included, but neither the id nor the aria attributes, so a page renders those
itself; matching a UI library's own class names is `FormidableOptions.CssClasses`.

**Read more:**

- [the foreign-control pattern](component-kit.md#the-foreign-control-pattern)
- [`FormidableFieldAnchor`](component-kit.md#formidablefieldanchortvalue)
- [`UpdateOn`](options.md#updateon-per-input-not-a-formidableoptions-property)
- [the `FieldCssClassProvider` bridge](css-and-accessibility.md#the-fieldcssclassprovider-bridge)
- [`CssClasses`](options.md#cssclasses)

Samples:

- [`/foreign`](../samples/Formidable.Sample/Pages/ForeignControl.razor)
- [`/vanilla`](../samples/Formidable.Sample/Pages/VanillaInterop.razor)
- [`/bootstrap`](../samples/Formidable.Sample/Pages/BootstrapFitting.razor)
- [`/workout`](../samples/Formidable.Sample/Pages/Workout.razor)

### I want profiles of my own

**Set:** derive from `ProfiledValidator<T>`. Shared rules go in `ConfigureCommonRules()` and run
under every profile that includes the default rules. Each named ruleset goes in
`ConfigureProfiles()`, through `Profile(name, ...)`.

This recipe builds a validator without the draft/submit shape (`ProfiledValidator<T>`). For a
third profile beside Draft and Submit, stay on `DraftSubmitValidator<T>` and register its
ruleset in `ConfigureAdditionalProfiles()` ([Profiles](profiles.md#how-do-i-define-a-custom-profile)).

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
`FormidableOptions.SubmitProfile` at it. With `LiveProfile` unset, that one setting moves both
channels: the live channel follows whichever instance `SubmitProfile` holds. An engaged field then
answers the custom profile between submits, with no second setting to keep in step.

**Switch profiles at runtime on the options instance the form already holds:** the assignment takes
effect from the next check, and handing the form a wholly new `FormidableOptions` throws unless
`Model` changes with it.

Server-side, minimal APIs take a `ValidationProfile` value directly while
`[Validate(Profile = "…")]` takes a name. Any name other than `Draft` or `Submit` (matched
case-insensitively) becomes the default rules plus the same-named ruleset, while a blank name, or
one joining several with `,` or `;`, is refused.

**Read more:**

- [Profiles](profiles.md)
- [how the engine reads options](options.md#need-to-know)
- [`FormidableOptions` is read once](options.md#formidableoptions-is-read-once)
- [profile string mapping](server-integration.md#profile-string-mapping)

Sample:

- [`/custom-profiles`](../samples/Formidable.Sample/Pages/CustomProfiles.razor) picks a profile at
  runtime, with the live channel following `SubmitProfile` as it moves

### I want localized messages

**Set:** three `FormidableOptions` strings, below. Every rule's own message reaches you through
FluentValidation's localization untouched. Keep a rule's default message and FluentValidation
translates it by `CultureInfo.CurrentUICulture`. For your own text, pass a message *factory*, so the
lookup runs with the rule under the culture in force then.

```csharp
RuleFor(p => p.Age).Must(BeAWholeAgeInRange)
    .WithMessage(_ => ValidationMessages.AgeRange);

RuleFor(o => o.Description).NotEmpty().WithName("Order description");
```

`WithName(...)` display names land on `ValidationIssue.DisplayName`, and from there in
`SubmitOutcome.VisibleErrorSummary`, ready for a dialog without extra mapping.

Three strings a form puts on screen come from the engine rather than from a rule. All three ship in
English:

- **`DefensiveGateMessage`** is the explanation a blocked submit shows when every field that failed
  is hidden.
- **`ModelLevelDisplayName`** is the name a model-level entry is listed under in
  `SubmitOutcome.VisibleErrorSummary`.
- **`ValidationFaultMessage`** is what the form shows when a validator throws during a live check
  or the whole-form re-check (a submit or a load throws to your own code instead).

Nothing else the kit renders on a form is the library's own words: every other message was written
by a rule, or came back from your server.

One piece of the library's English renders where no form does: the paragraph `FormidableForm` puts
in a form's place on a statically rendered page with no render mode. It speaks to whoever built the
page rather than to a visitor, and no option localizes it.

Give the three a resource lookup and the form speaks your language:

```csharp
_options = new FormidableOptions
{
    DefensiveGateMessage = ValidationMessages.HiddenFieldsInvalid,
    ModelLevelDisplayName = ValidationMessages.ThisForm,
    ValidationFaultMessage = ValidationMessages.ValidationIncomplete
};
```

Assign these on the options instance the form already holds, since each is read where it is used. A
change to `ValidationFaultMessage` reaches the next fault and leaves one already on screen as it
was.

**Server-side, one string takes a different seam.** The endpoint filter fills an otherwise-empty 400
with `"A request body is required."` where the platform refuses a request whose body bound to null.
That is a response rather than something a form renders, so it comes from the call site instead of
the options:

```csharp
app.MapPost("/orders", (Order order) => Results.Ok(order))
    .Validate<Order>(missingBodyMessage: ValidationMessages.BodyRequired);
```

Pass nothing and the English default stands.

**Read more:**

- [localization and display names](profiles.md#where-do-display-names-and-localized-messages-come-from)
- [`DefensiveGateMessage`](options.md#defensivegatemessage)
- [`ModelLevelDisplayName`](options.md#modelleveldisplayname)
- [`ValidationFaultMessage`](options.md#validationfaultmessage)
- [`FormidableOptions` is read once](options.md#formidableoptions-is-read-once)
- [culture at WebAssembly boot](component-kit.md#culture-at-webassembly-boot)
- [a body bound to null](server-integration.md#a-body-bound-to-null)

Sample:

- [`/localization`](../samples/Formidable.Sample/Pages/Localization.razor)

### I want to validate a nested object

**Set:** `SetValidator` on the parent property's rule, or `ChildRules` to write them inline. Name
the nested field with its full path in `For` or `@bind-Value`.

This recipe covers one nested object (`Order.ShippingAddress`). A list of rows is
[Collections and row identity](collections-and-row-identity.md); a list inside a row is
[its nesting section](collections-and-row-identity.md#how-do-i-nest-one-collection-inside-another).

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

Depth needs no special handling: the reported path is walked down the live graph and the issue keyed
to the object it reaches, so `() => Model.ShippingAddress.Region.Code` is one field, owned by the
`Region` instance. Give that nested object a value the markup can reach
(`public Address ShippingAddress { get; set; } = new();`), since the page's own binding dereferences
it while rendering.

**What goes wrong when the nested object is replaced.** A field is an owner object plus a member
name. After `Model.ShippingAddress = new Address()` the next check answers for
`ShippingAddress.Street` under the new instance, while the components rendering that field go on
asking under the old.

An ask under the old owner comes back clean. The field's messages go, and `aria-invalid` and
`aria-describedby` go with them. A field the visitor had already touched can end up wearing the
valid class. The required marker and `aria-required` go too, at the next requirement derivation
rather than at that check.

The rule that failed still blocks the submit, so a form with nothing else failing shows the
defensive gate rather than a field anyone can fix.

**The habit that avoids it: keep both sides naming one object.** `@key` the markup around the nested
object by that object, so replacing it rebuilds the components inside against the new owner.
Swapping the whole `Model` rebuilds the engine, the registry and every binding, and
`FormidableForm.ResetAsync()` rebinds over the model already bound with none of the old state
surviving, so it answers a "start over" button, not a nested swap.

**A safety net.** Until a repair is in place, turning `VerifyRowKeys` on throws on the divergence
rather than leaving it to be spotted on screen.

**Read more:**

- [Collections and row identity](collections-and-row-identity.md) (path resolution and the
  render-before-notify rule)
- [`VerifyRowKeys`](options.md#verifyrowkeys)
- [returning the form to pristine](component-kit.md#returning-the-form-to-pristine)
- [the defensive gate](disclosure.md)

Why: [how the engine works: what a component registers](how-the-engine-works.md#what-a-component-registers-and-re-reads).

Sample:

- [`/collections`](../samples/Formidable.Sample/Pages/Collections.razor) — teams holding members,
  nested inside a collection

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

Two capabilities sit beside the `IModelValidator<TModel>` seam a form validates through:
`IRuleInspectingValidator<TModel>` reads what the rules demand of a field, and
`IRuleLevelValidator<TModel>` runs a chosen set of them. `DelegatingModelValidator<TModel>` forwards
all three interfaces and answers each capability tester with the wrapped validator's own answer.
Wrap a validator that has neither and the wrapper presents neither: inspection reports the empty
answer, and the members that select or run rules one set at a time throw `NotSupportedException`.

**Three things go quiet where a validator presents neither.** Required markers and `aria-required`
reach only the fields `RequiredOverride` declares. `DiscloseLoadedValuesAsync` goes on disclosing a
wrong saved value and stops confirming a good one. And no rule's answer is reused between checks, so
after a submit a rule can run twice for one edit.

The same missing reuse is why a field a submit would accept wears no green after an edit until a
submit, a load of values, or the whole-form re-check (after a submit, or after a change to which
fields are on screen) has answered for it, unless `TrackFormValidity` is on.

**Only inspection's half is reported.** A validator that cannot report its rules gets one
Information-level line when the engine is built, naming what will not render, on the `Trace` and
`ILogger` channels suppressions use; it names the state rather than the mistake, since the two read
alike. The other capability has no line.

**Override every entry point that validates.** `Validate`, `ValidateAsync` and `ValidateRulesAsync`
all take a model, and which runs is the caller's choice. A form that can take the validator rule by
rule validates through `ValidateRulesAsync`, a set of rules at a time, and reaches neither of the
other two. A wrapper staging its model at `ValidateAsync` alone therefore changes nothing that form
sees.

**State the wrapper reads from outside the model needs telling.** Staging a new upload raises no
field-changed notification, so the form goes on judging the model as it was until the next edit,
submit, load of values, or change to which fields are on screen. Call `NotifyChanged()` on the field
the staged state belongs to whenever that state changes.

**Read more:**

- [what a form resolves](component-kit.md#need-to-know)
- [`TrackFormValidity`](options.md#trackformvalidity) (green before a submit)
- [opening a form on saved values](#i-want-to-open-a-form-on-values-the-visitor-did-not-type)
- [marking fields required](#i-want-to-mark-fields-required-when-the-rules-cannot-say-so)
- [a native or third-party control](#i-want-to-use-a-native-or-third-party-control)

Why: [how the engine works: what a validator can be asked](how-the-engine-works.md#rule-level-versus-whole-profile-execution).

Sample: no page. The worked examples are Formidable's own tests, `DelegatingModelValidatorTests.cs`.

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

Those three are the only kit services a rendered form resolves that talk to JavaScript, and doubling
them makes focus and issue order assertable as a side benefit. `FormidableSummary` injects the focus
service outright, so a form rendering one needs it present either way.

Assert through bUnit's `WaitForAssertion`, since a verdict lands a render later than the event that
asked for it, and call the form's own methods (`SubmitAsync()`, `ResetAsync()`,
`ApplyServerIssues(...)`) through `InvokeAsync`, since all three trigger renders.

**Read more:**

- [Testing](testing.md#testing-your-forms) (the same ground in full, including how to pin a pending
  state)
- [Component kit](component-kit.md)

Sample: no page. The worked examples are Formidable's own component tests, such as
`FormidableFormComponentTests.cs`, which render the kit exactly this way.
