# Validation profiles

**You should already know:** how a Formidable form wires up end to end
([Quickstart](quickstart.md)).

One validator ends up serving more than two moments the instant a form grows past a toy
example. A signup form's email needs a shape check while the visitor is typing and a presence
check at submit — core concepts already covers that split. Add a save-draft button and there's a
third moment: valid enough to keep, not yet valid enough to send. Add an approval step behind
submit and there's a fourth: valid enough to submit, not yet valid enough to approve. Write a
separate validator per moment and the same `RuleFor(x => x.Email)` exists in every one of them,
agreeing today and drifting the first time someone fixes one copy and forgets the rest.

A `ValidationProfile` is Formidable's answer: one validator, and a name that selects which
subset of its rules applies at any given moment. This page covers the two profiles Formidable
ships as a convention, how to define more when a form's lifecycle doesn't fit them, and which
profile runs at which point in the client lifecycle.

## Need to know

`DraftSubmitValidator<T>` packages the common case — a format-checking profile and a
completeness-checking profile — as two methods to override, no ruleset name required:

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
        // Completeness rules: enforced only at submit.
        RuleFor(b => b.Title).NotEmpty().WithMessage("Title is required to submit");
        RuleFor(b => b.Summary).NotEmpty().WithMessage("Summary is required to submit");
    }
}
```

*Source: `samples/Formidable.Sample.Shared/DraftedBrief.cs`*

`ConfigureDraftRules()` routes to the validator's default (unnamed) rules; `ConfigureSubmitRules()`
routes to a `"Submit"` ruleset layered on top of them. Skip the split and a blank field earns two
complaints for the price of one mistake — "this is required" and "this is malformed" — the moment
anything validates it at all. Keeping the two questions on separate axes is what keeps a single
mistake to a single message. Draft rules ask "is this value malformed?" and treat an empty value
as fine, since a blank field mid-draft isn't wrong yet. Submit rules ask "is this value present at
all?" and treat that same empty value as missing.

| Profile | Rules it runs |
|---|---|
| `ValidationProfile.Draft` | Default (unnamed) rules only. |
| `ValidationProfile.Submit` | Default rules plus the `"Submit"` ruleset (`ValidationProfile.SubmitRuleSetName`). |

`DraftSubmitValidator<T>` also runs a diagnostic once, at construction. For every property whose
same-kind rule appears in both the draft rules and the `"Submit"` ruleset, it invokes
`OnOverlappingRuleAxes` for that (property, validator) pair. That overlap is usually a sign the
malformed/missing convention was broken by accident. The default implementation is a
`Debug`-output warning; override it to route elsewhere or suppress it.

That's the whole decision most forms make: derive from `DraftSubmitValidator<T>`, override two
methods, done. What follows is for the forms that need a third moment, and how the client decides
which profile runs when.

## Custom profiles

A three-stage approval workflow, a wizard with its own rules per step — anything where
"malformed vs. missing" isn't the axis that matters needs more than the draft/submit pair, and
there's more than one way to get it. Staying on `DraftSubmitValidator<T>` and registering a
further ruleset through `ConfigureAdditionalProfiles()` is the lighter touch. The `"Approve"`
profile below is exactly that: `ApprovableOrderValidator` is a `DraftSubmitValidator<TestOrder>`,
and its `ConfigureAdditionalProfiles()` override registers an `"Approve"` ruleset alongside the
draft/submit pair it already gets for free.

```csharp
        var approve = ValidationProfile.Named("Approve", includeDefaultRules: true, ValidationProfile.SubmitRuleSetName, "Approve");
```

*Source: `tests/Formidable.Tests/DraftSubmitValidatorTests.cs`*

This profile runs the default rules, the `"Submit"` ruleset, and an `"Approve"` ruleset together.
At least one ruleset is required when `includeDefaultRules` is `false` — a profile selecting
neither would validate nothing, and `Named` throws rather than silently accept that.

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

**Sample:** [`/custom-profiles`](../samples/Formidable.Sample/Pages/CustomProfiles.razor) — a
third, custom ruleset (`AdminReview`) alongside the built-in pair, picked at runtime.

## The client lifecycle

`FormidableForm<TModel>`'s engine reads two profiles off `FormidableOptions` (see
[Options](options.md)):

- **Live passes** — one per field change — run `LiveProfile` (`FormidableOptions.LiveProfile`,
  defaults to `ValidationProfile.Draft`).
- **Submit** runs `SubmitProfile` (`FormidableOptions.SubmitProfile`, defaults to
  `ValidationProfile.Submit`), and so does the debounced refresh that follows it.

Saving a draft doesn't go through the engine's submit pipeline at all — it's a separate, lenient
validation call straight against the injected `IModelValidator<T>` with the `Draft` profile:

```csharp
    private async Task SaveDraft()
    {
        var report = await Validator.ValidateAsync(_brief, ValidationProfile.Draft);
        _status = report.IsValid
            ? "Draft saved — completeness rules were not enforced."
            : $"Draft blocked by format rules: {string.Join("; ", report.Errors.Select(e => e.Message))}";
    }
```

*Excerpt from `samples/Formidable.Sample/Pages/Profiles.razor.cs`*

**Sample:** [`/profiles`](../samples/Formidable.Sample/Pages/Profiles.razor) — draft-save next to
an ordinary submit, both against the same `DraftSubmitValidator<T>`.

## Server-side profile selection

Minimal APIs pass a `ValidationProfile` value directly:

```csharp
app.MapGroup("/api/orders").Validate<RoundTripOrder>(ValidationProfile.Draft);
```

MVC's `[Validate]` attribute takes a profile **name** string instead, resolved per request:

```csharp
[Validate(Profile = "Draft")]
public IActionResult SaveDraft([FromBody] RoundTripOrder order) => Ok();
```

`"Draft"` and `"Submit"` match case-insensitively to the two built-in singletons. Any other name
becomes a custom profile shaped the same way `Submit` itself is built —
`ValidationProfile.Named(name, includeDefaultRules: true, name)`, i.e. default rules plus one
ruleset with the same name as the profile.

## Localization and display names

FluentValidation's localization and `WithName(...)` display names pass through unchanged —
Formidable adds no translation or renaming layer of its own. A rule's `WithName(...)` call lands
on `ValidationIssue.DisplayName`, and from there in `SubmitOutcome.VisibleErrorSummary` — ready
for a dialog or summary without any extra mapping step on your end. A localized message from
FluentValidation's own resource pipeline takes the same route.

**Test:** `Submit_shows_only_revealed_fields_and_reports_their_display_names`
(`tests/Formidable.Blazor.Tests/FormValidationEngineSubmitTests.cs`) pins a rule with
`WithName("Order description")` surfacing that exact display name in
`SubmitOutcome.VisibleErrorSummary`.

**Sample:** [`/localization`](../samples/Formidable.Sample/Pages/Localization.razor)
