# Severity levels

**You should already know:** the draft/submit split and which profile runs when
([Core concepts](core-concepts.md)), and how a `ValidationProfile` selects rules out of
one validator ([Profiles](profiles.md)).

A validation rule looks binary until product wants a nudge instead of a wall. A listing with no
title should block — there's nothing to sell without a name. A listing whose description has three
exclamation marks probably shouldn't; it's shouty, not broken, and the person who typed it should
hear about it without losing the ability to publish. Give a rule engine only pass and fail and
both problems come out the same shape: a message on screen, a submit button that won't fire, no
way to tell "you must fix this" from "you might want to fix this."

FluentValidation already has a third state built in. This page is about what Formidable does
with it: which severities block, how they render, and how long a warning or an info stays on
screen once it's been shown.

## Need to know

A rule declares its severity with FluentValidation's own `.WithSeverity(...)`; leaving it off
makes the rule an `Error`, exactly as it always was:

```csharp
/// <summary>Severity of a <see cref="ValidationIssue"/>.</summary>
public enum ValidationSeverity
{
    /// <summary>A failure that blocks submission.</summary>
    Error = 0,

    /// <summary>Shown to the user but does not block submission.</summary>
    Warning = 1,

    /// <summary>Informational only; never blocks submission.</summary>
    Info = 2
}
```

*Source: `src/Formidable/ValidationSeverity.cs`*

The adapter maps FluentValidation's `Severity` enum onto `ValidationSeverity` one-to-one —
`Severity.Warning` → `ValidationSeverity.Warning`, `Severity.Info` → `ValidationSeverity.Info`,
and everything else (including FluentValidation's own default) → `ValidationSeverity.Error`.
That "everything else" leg includes an explicit `.WithSeverity(Severity.Error)`: writing the
default out by hand maps exactly the way leaving it off does, for teams that prefer every rule
to state its severity. Where the rule lives decides when it's checked, same as any other rule —
the common/draft bucket if an advisory should nag live, `ConfigureSubmitRules()` if it should
stay quiet until the user commits:

```csharp
public class Listing
{
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Tags { get; set; } = string.Empty;
}

public class ListingValidator : DraftSubmitValidator<Listing>
{
    protected override void ConfigureDraftRules()
    {
        // Common bucket: live while editing and enforced at submit.
        RuleFor(l => l.Title).NotEmpty().WithMessage("Title is required");
        RuleFor(l => l.Description)
            .Must(d => !d.Contains('!'))
            .WithSeverity(Severity.Warning)
            .WithMessage("Exclamation marks read as shouty — consider removing them");
        RuleFor(l => l.Tags)
            .Must(t => t.Length == 0 || t.Split(',').Length <= 5)
            .WithSeverity(Severity.Info)
            .WithMessage("More than five tags rarely helps discovery");
    }

    protected override void ConfigureSubmitRules()
    {
    }
}
```

*Source: `samples/Formidable.Sample.Shared/Listing.cs`*

Both advisory rules above live in `ConfigureDraftRules()`, Formidable's shared "common" bucket.
That bucket is run by the draft/live ruleset and folded into `"Submit"` (see
[Profiles](profiles.md)). So under the default `LiveProfile` (`Draft`) they run live, on blur,
exactly like the required-title error above them, and they are still enforced when the form
submits. The scratch validator in `tests/Formidable.Blazor.Tests/SubmitSeverityRenderingTests.cs`
shows the other shape: warnings and infos placed in `ConfigureSubmitRules()` instead, so they stay
quiet until submit. Either way, the disclosure lifecycle described in "The warning lifetime" below
applies once the issue has first been shown.

None of that changes what a warning or an info does to the submit itself: nothing. `IsValid`
counts only errors, and `SubmitOutcome.CanProceed` is the same flag under a different name.
`FormidableForm<TModel>.SubmitAsync()` routes on it: `OnValidSubmit` when `CanProceed`,
`OnInvalidSubmit` otherwise. A model that's all warnings and infos, with no errors, submits
successfully. That's the whole authoring surface: mark severities, put the rule where it should
run, read `CanProceed` instead of counting errors by hand. What follows backs that guarantee with
the actual types, shows how a severity renders, and covers how long a warning stays visible once
it's shown.

## Warnings and infos never block

```csharp
    /// <summary>
    /// True when there are no <see cref="ValidationSeverity.Error"/> issues.
    /// Warnings and infos do not affect validity.
    /// </summary>
    public bool IsValid => !Errors.Any();
```

*Source: `src/Formidable/ValidationReport.cs`*

```csharp
/// <summary>The result of running the submit pipeline.</summary>
/// <param name="CanProceed">True when no error-severity issues exist (warnings do not block).</param>
/// <param name="Report">The full validation report from the submit profile.</param>
/// <param name="VisibleErrorSummary">Distinct display names of the errors shown to the user — dialog/summary fodder.</param>
public sealed record SubmitOutcome(
    bool CanProceed,
    ValidationReport Report,
    IReadOnlyList<string> VisibleErrorSummary);
```

*Source: `src/Formidable.Blazor/SubmitOutcome.cs`*

`FormidableForm<TModel>.SubmitAsync()` runs the submit pipeline and routes on exactly that flag:
`OnValidSubmit` when `CanProceed`, `OnInvalidSubmit` otherwise — both handlers receive the full
`SubmitOutcome`, so a passing submit's advisories are readable without a separate `Engine` read.
So a model that's all warnings and infos, with no errors, submits successfully:

```razor
<FormidableForm @ref="_form" Model="_listing">
    <div class="summary-slot">
        <FormidableSummary Show="SummaryFilter.Errors" ErrorsHeading="Errors" />
    </div>
    <div class="summary-slot">
        <FormidableSummary Show="SummaryFilter.Advisories" WarningsHeading="Warnings" InfosHeading="Info" />
    </div>

    <div class="field"><label>Title <FormidableInputText @bind-Value="_listing.Title" /></label>
        <FormidableFieldMessage For="() => _listing.Title" /></div>
    <div class="field"><label>Description <FormidableInputText @bind-Value="_listing.Description" /></label>
        <FormidableFieldMessage For="() => _listing.Description" /></div>
    <div class="field"><label>Tags (comma-separated) <FormidableInputText @bind-Value="_listing.Tags" /></label>
        <FormidableFieldMessage For="() => _listing.Tags" /></div>

    <div class="actions"><button type="button" class="primary" @onclick="Submit">Submit</button></div>
</FormidableForm>

<p role="status">@_status</p>
```

*Excerpt from `samples/Formidable.Sample/Pages/SeverityLevels.razor`* — the page also carries a
teaching panel above the form.

The submit handler in the code-behind routes purely on `CanProceed`, and reports what got
through without blocking:

```csharp
    private async Task Submit()
    {
        var outcome = await _form!.SubmitAsync();
        _status = outcome.CanProceed
            ? $"Submitted with {outcome.Report.Warnings.Count()} warning(s) and {outcome.Report.Infos.Count()} info(s) — none of them blocked."
            : "Blocked by errors — fix them and resubmit.";
    }
```

*Excerpt from `samples/Formidable.Sample/Pages/SeverityLevels.razor.cs`*

Warnings never reach the `EditContext`'s own message store, either — only error-severity issues
are written there, which is what built-in `InputBase`/`ValidationMessage` interop sees. The full
issue set, warnings included, is available through `GetIssues`/`GetVisibleIssues` and
`FieldState.HasWarnings`, which is what `FormidableFieldMessage` and `FormidableSummary` render from.

## Rendering

`FormidableFieldMessage` (and its collection-level sibling, `FormidableCollectionMessage`) render
every current issue for a field as a list item, whatever its severity, with a class built
straight from it:

```csharp
            builder.OpenElement(sequence++, "li");
            builder.AddAttribute(
                sequence++,
                "class",
                FormidableCss.SelectBySeverity(issue.Severity, ErrorItemClass, WarningItemClass, InfoItemClass));
```

*Source: `src/Formidable.Blazor/FormidableFieldMessage.cs`*

`ErrorItemClass`/`WarningItemClass`/`InfoItemClass` are the three constant strings —
`"formidable-message formidable-message--error"` and so on — and `FormidableCss.SelectBySeverity`
is the one shared switch that every per-issue class in the kit picks its constant through, paying
no allocation to choose among them. So a rendered message carries
`formidable-message formidable-message--error`, `formidable-message formidable-message--warning`,
or `formidable-message formidable-message--info`. Style each in your own stylesheet; Formidable
ships no CSS of its own (see [CSS and accessibility](css-and-accessibility.md)).

`FormidableSummary` groups the whole form's currently-visible issues by severity — errors, then
warnings, then infos — one list per non-empty group, the same convention applied to the group
itself:

```csharp
        foreach (var group in groups)
        {
```

```csharp
            builder.OpenElement(sequence++, "ul");
            builder.AddAttribute(
                sequence++,
                "class",
                FormidableCss.SelectBySeverity(group.Key, ErrorGroupClass, WarningGroupClass, InfoGroupClass));
```

*Excerpt from `src/Formidable.Blazor/FormidableSummary.cs`* — elided in between is each group's
band wrapper and optional heading; see [Component kit](component-kit.md#formidablesummary) for
both.

— giving `formidable-summary__group formidable-summary__group--error`,
`formidable-summary__group formidable-summary__group--warning`, and
`formidable-summary__group formidable-summary__group--info`. Each item in a group is a
`formidable-summary__item` wrapping a `formidable-summary__link` button that moves focus to the
offending field.

One summary carries all three groups by default. A page that wants the blocking problems and the
commentary in different places on the form renders a summary per band instead, with
`Show="SummaryFilter.Errors"` and `Show="SummaryFilter.Advisories"` — see
[Component kit](component-kit.md#showing-one-severity-band) for what that costs in announcements.

## The warning lifetime

Submit is the disclosure event for a warning or an info exactly as it is for an error.
`ValidateForSubmitAsync` decides, once, which currently-rendered fields carry a visible issue of
any severity. The union of that submit's error-visible and advisory-visible fields is the watched
set from then on. Every further edit arms the debounced refresh (`RefreshDebounce`, see
[Options](options.md)), which re-validates the whole model but only ever narrows that set. It
never goes looking for newly warning-worthy fields outside it.

A warning that was showing when the user last submitted keeps refreshing live as they keep
editing: it clears the moment they fix it, and comes back if they break it again. A field that was
an error site at submit picks up a newly-appearing warning too, because it is already in the
watched set — whether or not it carried a warning at submit time. Only a field with neither an
error nor a warning at submit stays quiet when it starts failing a warning-severity rule. It waits
for the next submit, the same way a newly-failing error field would.

## Server-side

On the server, both the minimal-API `Validate<T>()` filter and the MVC `[Validate]` attribute
short-circuit to a 400 `ValidationProblemDetails` only when the report has at least one
error-severity issue; a report that's all warnings and infos lets the request through unblocked.
When a request *is* blocked, any warnings or infos in that same report ride along on the
response's `advisories` extension key. That key sits alongside the standard `errors` dictionary, not
inside it, and `ApplyServerIssues` applies the whole body at the severity each issue carries: the
errors block, the advisories land on their own fields without blocking. See
[Server integration](server-integration.md) for the full wire format and the round trip in full.

**Sample:** [`/severity`](../samples/Formidable.Sample/Pages/SeverityLevels.razor)
