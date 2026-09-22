# Validation profiles

A `ValidationProfile` selects which rules run out of one FluentValidation validator: the
default (unnamed) rules, plus zero or more named FluentValidation rulesets. One validator
definition serves several lifecycles — draft, submit, a wizard step, an approval stage — by
pointing different profiles at it instead of writing a validator per lifecycle.

Two profiles ship as conventions:

| Profile | Rules it runs |
|---|---|
| `ValidationProfile.Draft` | Default (unnamed) rules only. |
| `ValidationProfile.Submit` | Default rules plus the `"Submit"` ruleset (`ValidationProfile.SubmitRuleSetName`). |

The convention behind them: draft rules answer "is this value malformed?" (format, length,
range) and treat a default value (empty string, `0`, `null`) as valid; submit rules answer "is
this value present?" and treat a default value as missing. Keeping those two questions on
separate axes means a single mistake never produces two error messages.

## Custom profiles

`ValidationProfile.Named(name, includeDefaultRules = true, params string[] ruleSets)` builds a
profile from any combination of rulesets — wizard steps, an approval stage layered on top of
Submit, whatever your form's lifecycle needs:

```csharp
var approve = ValidationProfile.Named("Approve", includeDefaultRules: true, ValidationProfile.SubmitRuleSetName, "Approve");
```

*Source: `tests/Formidable.Tests/DraftSubmitValidatorTests.cs`*

This profile runs the default rules, the `"Submit"` ruleset, and an `"Approve"` ruleset
together. At least one ruleset is required when `includeDefaultRules` is `false` — a profile
selecting neither would validate nothing, and `Named` throws rather than silently accept that.

## Two ways to define a validator

### `ProfiledValidator<T>` — unopinionated

The base class for organizing rules around profiles without prescribing what those profiles
are. Override `ConfigureCommonRules()` for rules that run under any profile with
`IncludeDefaultRules` set, and register named rulesets with `Profile(ruleSetName,
configureRules)` inside `ConfigureProfiles()`:

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

Derive from this directly when a form's lifecycle doesn't match the draft/submit shape — a
three-stage approval workflow, for example, where "malformed vs. missing" isn't the axis that
matters.

### `DraftSubmitValidator<T>` — the shipped convention

`DraftSubmitValidator<T>` is one packaged convention over `ProfiledValidator<T>`: it fixes the
profile shape to the common save-draft/submit lifecycle so most forms never need to name a
ruleset at all. Override `ConfigureDraftRules()` (routed to `ConfigureCommonRules()`) and
`ConfigureSubmitRules()` (routed to the `"Submit"` ruleset); add further profiles beyond the
draft/submit pair in `ConfigureAdditionalProfiles()`.

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

`DraftSubmitValidator<T>` also runs a diagnostic once, at construction: for every property whose
same-kind rule appears in both the draft rules and the `"Submit"` ruleset — usually a sign the
malformed/missing convention was broken by accident — it invokes `OnOverlappingRuleAxes` for
that (property, validator) pair. The default implementation is a `Debug`-output warning;
override it to route elsewhere or suppress it.

Forms that don't fit the draft/submit shape derive from `ProfiledValidator<T>` directly.

## The client lifecycle

`FormidableForm<TModel>`'s engine reads two profiles off `FormidableOptions` (see
[`docs/options.md`](options.md)):

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
Formidable adds no translation or renaming layer of its own. A rule's `WithName(...)` call (or a
localized message from FluentValidation's own resource pipeline) lands on
`ValidationIssue.DisplayName` and, from there, in `SubmitOutcome.VisibleErrorSummary` — ready for
a dialog or summary without any extra mapping step on your end.

**Test:** `Submit_shows_only_revealed_fields_and_reports_their_display_names`
(`tests/Formidable.Blazor.Tests/FormValidationEngineSubmitTests.cs`) pins a rule with
`WithName("Order description")` surfacing that exact display name in
`SubmitOutcome.VisibleErrorSummary`.

**Sample:** [`/profiles`](../samples/Formidable.Sample/Pages/Profiles.razor)
