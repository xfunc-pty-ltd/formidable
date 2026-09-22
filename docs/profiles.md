# Validation profiles

**You should already know:** how a Formidable form wires up end to end
([Quickstart](quickstart.md)).

One validator ends up serving more than two moments the instant a form grows past a toy example. A
signup form's email needs a shape check while the visitor is typing and a presence check at submit.
That is two moments already. Add a save-draft button and there's a third moment: valid enough to
keep, not yet valid enough to send. Add an approval step behind submit and there's a fourth: valid
enough to submit, not yet valid enough to approve.

Write a separate validator per moment and the same `RuleFor(x => x.Email)` exists in every one of
them, agreeing today and drifting the first time someone fixes one copy and forgets the rest.

A `ValidationProfile` is Formidable's answer: one validator, and a name that selects which subset of
its rules applies at any given moment. This page covers the two profiles Formidable ships as a
convention, how to define more when a form's lifecycle doesn't fit them, and which profile runs at
which point in the client lifecycle.

## Need to know

`DraftSubmitValidator<T>` packages the common case (a format-checking profile and a
completeness-checking profile) as two methods to override, no ruleset name required:

```csharp
public class DraftedBriefValidator : DraftSubmitValidator<DraftedBrief>
{
    protected override void ConfigureDraftRules()
    {
        // Format rules: enforced always, including while drafting.
        RuleFor(b => b.Title).MaximumLength(60).WithMessage("Title is 60 characters max");
    }

    protected override void ConfigureSubmitRules()
    {
        // Completeness rules: the submit bucket, so a draft save leaves them alone.
        RuleFor(b => b.Title).NotEmpty().WithMessage("Title is required to submit");
        RuleFor(b => b.Summary).NotEmpty().WithMessage("Summary is required to submit");
    }
}
```

<!-- Source: `samples/Formidable.Sample.Shared/DraftedBrief.cs` -->

`ConfigureDraftRules()` routes to the validator's default (unnamed) rules; `ConfigureSubmitRules()`
routes to a `"Submit"` ruleset layered on top of them. Skip the split and a blank field earns two
complaints for the price of one mistake ("this is required" and "this is malformed") the moment
anything validates it at all. Keeping the two questions on separate axes is what keeps a single
mistake to a single message.

Draft rules ask "is this value malformed?" and treat an empty value as fine, since a blank field
mid-draft isn't wrong yet. Submit rules ask "is this value present at all?" and treat that same
empty value as missing.

| Profile | Rules it runs |
|---|---|
| `ValidationProfile.Draft` | Default (unnamed) rules only. |
| `ValidationProfile.Submit` | Default rules plus the `"Submit"` ruleset (`ValidationProfile.SubmitRuleSetName`). |

`DraftSubmitValidator<T>` also runs a diagnostic once, at construction. For every property whose
same-kind rule appears in both the draft rules and the `"Submit"` ruleset, it invokes
`OnOverlappingRuleAxes` for that (property, validator) pair. That overlap is usually a sign the
malformed/missing convention was broken by accident. The default implementation writes a
`Debug`-output line in a debug build of the library and nothing in the released package; override it
to route the report elsewhere.

That's the whole decision most forms make: derive from `DraftSubmitValidator<T>`, override two
methods, done. What follows is for the forms that need a third moment, and how the client decides
which profile runs when.

## How do I define a custom profile?

Register a further ruleset on the validator and name a profile that selects it; there's more than
one way to register it. Staying on `DraftSubmitValidator<T>` and registering the ruleset through
`ConfigureAdditionalProfiles()` is the lighter touch, and it covers a three-stage approval workflow,
a wizard with its own rules per step, or anything else where "malformed vs. missing" isn't the axis
that matters. The `"Approve"` profile below is exactly that: `ApprovableOrderValidator` is a
`DraftSubmitValidator<TestOrder>`, and its `ConfigureAdditionalProfiles()` override registers an
`"Approve"` ruleset alongside the draft/submit pair it already gets for free.

```csharp
var approve = ValidationProfile.Named("Approve", includeDefaultRules: true, ValidationProfile.SubmitRuleSetName, "Approve");
```

<!-- Source: `tests/Formidable.Tests/DraftSubmitValidatorTests.cs` -->

This profile runs the default rules, the `"Submit"` ruleset, and an `"Approve"` ruleset together. At
least one ruleset is required when `includeDefaultRules` is `false`: a profile selecting neither
would validate nothing, and `Named` throws rather than silently accept that.

`ProfiledValidator<T>` is the other way in: the base class underneath `DraftSubmitValidator<T>`,
without the draft/submit shape baked in. Reach for it when nothing about the lifecycle resembles
draft/submit at all. Override `ConfigureCommonRules()` for rules that run under any profile with
`IncludeDefaultRules` set, and register named rulesets with `Profile(ruleSetName, configureRules)`
inside `ConfigureProfiles()`:

```csharp
public class MyValidator : ProfiledValidator<MyModel>
{
    protected override void ConfigureCommonRules() =>
        RuleFor(x => x.Name).MaximumLength(100);

    protected override void ConfigureProfiles()
    {
        Profile("Step1", () => RuleFor(x => x.Name).NotEmpty());
        Profile("Step2", () => RuleFor(x => x.Owner).NotNull());
    }
}
```

For a wizard, let Next submit the form under the current step's profile. As the visitor advances,
reassign `SubmitProfile` on the `FormidableOptions` instance the form already holds
(`ValidationProfile.Named("Step2", includeDefaultRules: true, "Step2")` once step 1 has passed).
The final Submit takes a profile naming every step's ruleset. Leave `LiveProfile` unset and the
live check follows the step ([profiles of my own](recipes.md#i-want-profiles-of-my-own) has the
runtime switch).

A rule that needs membership in two rulesets without existing twice takes a comma-separated name:
`Profile("Step1,Step2", ...)` tags every rule inside with both, and either name composes into a
profile on its own — see
[narrow what the live channel validates](recipes.md#i-want-to-narrow-what-the-live-channel-validates)
for a worked example.

A `ProfiledValidator<T>` checks a profile's ruleset names before it validates against it, once per
distinct profile. A name the validator never registered throws rather than quietly selecting
nothing. FluentValidation's own `"*"` and `"default"` always pass: its selector honours those
without any validator declaring them. The check rides the profile-taking `Validate`/`ValidateAsync`
overloads `ValidatorProfileExtensions` adds, so a plain `AbstractValidator<T>` called through them
is not covered.

**Sample:** [`/custom-profiles`](../samples/Formidable.Sample/Pages/CustomProfiles.razor) — a third,
custom ruleset (`AdminReview`) alongside the built-in pair, picked at runtime.

## Which profile runs when?

Submit runs `SubmitProfile`; live checking runs `LiveProfile`, which is `null` by default and then
means the submit profile itself; a draft save is your own call against the validator and runs the
profile you pass, `Draft` by convention. `FormidableForm<TModel>`'s engine reads the two profiles
off `FormidableOptions` (see [Options](options.md)):

- **Submit** runs `SubmitProfile` (`FormidableOptions.SubmitProfile`, defaults to
  `ValidationProfile.Submit`). After a submit, the whole-form re-check that follows each edit
  answers for that same profile. On a validator the engine can take rule by rule, that re-check
  runs only the rules nothing has yet answered for that edit (see
  [Async validation](async-validation.md#does-the-library-ever-run-my-rule-twice-for-one-edit)).
- **Live** checks run `LiveProfile` (`FormidableOptions.LiveProfile`). It is nullable and defaults
  to `null`, which means the live channel evaluates the submit profile itself, whichever instance
  `SubmitProfile` currently holds. Point `LiveProfile` somewhere narrower and the live channel
  evaluates that profile instead.

Following the submit profile is what lets a live message say what a submit would actually complain
about, presence rules included. What keeps that from nagging is not the rule selection but the
engaged set. Short of a submit or a server reply, a live check discloses only for the fields
something has engaged, so a field nobody has reached stays silent however loudly its rule is
failing underneath (see [Disclosure](disclosure.md#why-isnt-my-message-showing-yet)).

Narrowing `LiveProfile` is the blunter lever beside the engaged set, and it is worth reaching for
when a submit rule is genuinely too expensive to run on every change, such as a uniqueness check
against a server. Narrowing cannot tell an untouched field from an engaged one, so it silences the
field the visitor is working in along with the rest.

A validator that declares no ruleset (a plain `AbstractValidator<T>`) has only default (unnamed)
rules, which `Draft` selects, so `LiveProfile = ValidationProfile.Draft` holds nothing back until
the rules that should wait sit in a ruleset
([validate while typing, on blur, or only at submit](recipes.md#i-want-to-validate-while-typing-on-blur-or-only-at-submit)).

Saving a draft doesn't go through the engine's submit pipeline at all, whatever the live channel is
doing. The save is a separate, lenient validation call straight against the injected
`IModelValidator<T>` with the `Draft` profile:

```csharp
private async Task SaveDraft()
{
    var report = await Validator.ValidateAsync(_brief, ValidationProfile.Draft);
    _status = report.IsValid
        ? "Draft saved — completeness rules were not enforced."
        : $"Draft blocked by format rules: {string.Join("; ", report.Errors.Select(e => e.Message))}";
}
```

<!-- Excerpt from `samples/Formidable.Sample/Pages/Profiles.razor.cs` -->

Why: [how the engine works: which profile each check runs](how-the-engine-works.md#the-five-pass-kinds).

**Sample:** [`/profiles`](../samples/Formidable.Sample/Pages/Profiles.razor) — draft-save next to an
ordinary submit, both against the same `DraftSubmitValidator<T>`.

## How do I pick a profile on the server?

By value on a minimal API and by name on MVC. Minimal APIs pass a `ValidationProfile` value
directly:

```csharp
app.MapGroup("/api/orders").Validate<RoundTripOrder>(ValidationProfile.Draft);
```

MVC's `[Validate]` attribute takes a profile **name** string instead, resolved per request:

```csharp
[Validate(Profile = "Draft")]
public IActionResult SaveDraft([FromBody] RoundTripOrder order) => Ok();
```

`[Validate]` resolves that name string through `ValidationProfile.FromName(name)`: `"Draft"` and
`"Submit"` match case-insensitively to the two built-in singletons. Any other name becomes a custom
profile shaped the same way `Submit` itself is built
(`ValidationProfile.Named(name, includeDefaultRules: true, name)`, i.e. default rules plus one
ruleset with the same name as the profile). A blank name, or one joining several with `,` or `;`,
is refused loudly instead: FluentValidation splits a joined name where a rule is *declared*, never
where one is selected, so such a profile would silently select nothing.

## Where do display names and localized messages come from?

From FluentValidation, untouched. Its localization and `WithName(...)` display names pass through
unchanged: Formidable puts no translation or renaming layer between a rule and the message or name
it produces. A rule's `WithName(...)` call lands on `ValidationIssue.DisplayName`, and from there in
`SubmitOutcome.VisibleErrorSummary` — ready for a dialog or summary without any extra mapping step
on your end. A localized message from FluentValidation's own resource pipeline takes the same route.

A rule with no `WithName(...)` gets FluentValidation's own display name there: the property path
with its words split apart, so `ContactEmail` reads as "Contact Email" and `Address.CityName` as
"Address City Name".

**Sample:** [`/localization`](../samples/Formidable.Sample/Pages/Localization.razor)
