# Severity levels

Every `ValidationIssue` carries a `ValidationSeverity`:

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

A rule declares its severity with FluentValidation's own `.WithSeverity(...)`. The adapter maps
FluentValidation's `Severity` enum onto `ValidationSeverity` one-to-one — `Severity.Warning` →
`ValidationSeverity.Warning`, `Severity.Info` → `ValidationSeverity.Info`, and everything else
(including FluentValidation's own default) → `ValidationSeverity.Error`. A rule with no
`.WithSeverity(...)` call is an error, exactly as it always was.

## Declaring severity

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

The warning and info rules here live in `ConfigureDraftRules()` — Formidable's shared "common"
bucket, run by both the draft/live ruleset and folded into `"Submit"` (see
[`docs/profiles.md`](profiles.md)) — so under the default `LiveProfile` (`Draft`) they run live,
on blur, exactly like the required-title error above, and are still enforced when the form
submits. A validator that instead wants its warnings and infos to stay quiet until submit places
them in `ConfigureSubmitRules()` — the scratch validator in
`tests/Formidable.Blazor.Tests/SubmitSeverityRenderingTests.cs` is a ready-made example of that
shape. Either way, the disclosure lifecycle described in "The warning lifetime" below applies once
the issue has first been shown.

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

`FormidableForm<TModel>.SubmitAsync()` runs the submit pipeline and routes on exactly that flag —
`OnValidSubmit` when `CanProceed`, `OnInvalidSubmit` (with the full `SubmitOutcome`) otherwise —
so a model that's all warnings and infos, with no errors, submits successfully:

```razor
<FormidableForm @ref="_form" Model="_listing">
    <FormSummary />

    <div class="field"><label>Title <FormidableInputText For="() => _listing.Title" @bind-Value="_listing.Title" /></label>
        <FieldMessage For="() => _listing.Title" /></div>
    <div class="field"><label>Description <FormidableInputText For="() => _listing.Description" @bind-Value="_listing.Description" /></label>
        <FieldMessage For="() => _listing.Description" /></div>
    <div class="field"><label>Tags (comma-separated) <FormidableInputText For="() => _listing.Tags" @bind-Value="_listing.Tags" /></label>
        <FieldMessage For="() => _listing.Tags" /></div>

    <div class="actions"><button type="button" class="primary" @onclick="Submit">Submit</button></div>
</FormidableForm>

<p role="status">@_status</p>
```

*Excerpt from `samples/Formidable.Sample/Pages/SeverityLevels.razor`* — the page also carries a
teaching panel above the form.

The submit handler in the code-behind routes purely on `CanProceed`, and reports what got through
without blocking:

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
`FieldState.HasWarnings`, which is what `FieldMessage` and `FormSummary` render from.

## Rendering

`FieldMessage` (and its collection-level sibling, `CollectionMessage`) render every current issue
for a field as a list item, whatever its severity, with a class built straight from it:

```csharp
            var severitySuffix = issue.Severity switch
            {
                ValidationSeverity.Error => "--error",
                ValidationSeverity.Warning => "--warning",
                _ => "--info"
            };

            builder.OpenElement(sequence++, "li");
            builder.AddAttribute(sequence++, "class", $"formidable-message formidable-message{severitySuffix}");
```

*Source: `src/Formidable.Blazor/FieldMessage.cs`*

So a rendered message carries `formidable-message formidable-message--error`,
`formidable-message formidable-message--warning`, or `formidable-message formidable-message--info`
— style each in your own stylesheet; Formidable ships no CSS of its own (see
[`docs/css-and-accessibility.md`](css-and-accessibility.md)).

`FormSummary` groups the whole form's currently-visible issues by severity — errors, then
warnings, then infos — one list per non-empty group, the same suffix convention applied to the
group itself:

```csharp
        foreach (var group in groups)
        {
            var severitySuffix = group.Key switch
            {
                ValidationSeverity.Error => "--error",
                ValidationSeverity.Warning => "--warning",
                _ => "--info"
            };

            builder.OpenElement(sequence++, "ul");
            builder.AddAttribute(sequence++, "class", $"formidable-summary__group formidable-summary__group{severitySuffix}");
```

*Source: `src/Formidable.Blazor/FormSummary.cs`*

— giving `formidable-summary__group formidable-summary__group--error`,
`formidable-summary__group formidable-summary__group--warning`, and
`formidable-summary__group formidable-summary__group--info`. Each item in a group is a
`formidable-summary__item` wrapping a `formidable-summary__link` button that moves focus to the
offending field.

## The warning lifetime

Submit is the disclosure event for a warning or an info exactly as it is for an error.
`ValidateForSubmitAsync` decides, once, which currently-rendered fields carry a visible issue of
any severity; from that point, the debounced refresh that follows every further edit
(`RefreshDebounce`, see [`docs/options.md`](options.md)) re-validates the whole model but only
ever narrows *that* already-visible set — it does not go looking for newly warning-worthy fields
on its own. Practically: a warning that was showing when the user last submitted keeps refreshing
live as they keep editing — it clears the moment they fix it, and comes back if they break it
again — while a field that only starts failing a warning-severity rule after that submit stays
quiet, the same way a newly-failing error field would, until the user submits again.

## Server-side

On the server, both the minimal-API `Validate<T>()` filter and the MVC `[Validate]` attribute
short-circuit to a 400 `ValidationProblemDetails` only when the report has at least one
error-severity issue; a report that's all warnings and infos lets the request through unblocked.
When a request *is* blocked, any warnings or infos in that same report ride along on the
response's `warnings` extension key, alongside — not inside — the standard `errors` dictionary, so
a client that wants to show them next to the fields that actually failed can. See
[`docs/server-integration.md`](server-integration.md) for the full wire format and how the client
re-applies a server response through `ApplyServerIssues`.

**Sample:** [`/severity`](../samples/Formidable.Sample/Pages/SeverityLevels.razor)
