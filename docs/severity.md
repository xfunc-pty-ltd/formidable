# Severity levels

**You should already know:** the draft/submit split
([Draft and submit rules](tutorial/2-draft-and-submit.md)), plus how a `ValidationProfile` selects
rules out of one validator and which profile runs when ([Profiles](profiles.md)).

A validation rule looks binary until product wants a nudge instead of a wall. A listing with no
title should block: there's nothing to sell without a name. A listing whose description has three
exclamation marks probably shouldn't; it's shouty, not broken, and the person who typed it should
hear about it without losing the ability to publish.

Give a rule engine only pass and fail and both problems come out the same shape: a message on
screen, and a submit button that won't fire. There is no way to tell "you must fix this" from "you
might want to fix this."

FluentValidation already has a third state built in. This page is about what Formidable does with
it: which severities block, how they render, and how long a warning or an info stays on screen once
it's been shown.

## Need to know

Severity comes from FluentValidation's own `.WithSeverity(...)`, and it attaches to the validator it
follows rather than to the rule around it. On
`NotEmpty().MaximumLength(2).WithSeverity(Severity.Warning)` the length failure is a warning while
the empty one still blocks. Every component of a chain that should advise needs its own call.
Leaving it off makes a failure an `Error`, exactly as it always was:

```csharp
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

<!-- Source: `src/Formidable/ValidationSeverity.cs` -->

The adapter maps FluentValidation's `Severity` enum onto `ValidationSeverity` one-to-one —
`Severity.Warning` → `ValidationSeverity.Warning`, `Severity.Info` → `ValidationSeverity.Info`, and
everything else (including FluentValidation's own default) → `ValidationSeverity.Error`. That
"everything else" leg includes an explicit `.WithSeverity(Severity.Error)`. Writing the default out
by hand maps exactly the way leaving it off does, for teams that prefer every component to state its
severity.

Where the rule lives decides what a save and a submit enforce, same as any other rule. Put it in the
common/draft bucket if a lenient draft save should answer the advisory too, in
`ConfigureSubmitRules()` if only a submit should:

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
        // Common bucket: enforced by a draft save and by a submit alike.
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

<!-- Source: `samples/Formidable.Sample.Shared/Listing.cs` -->

Both advisory rules above live in `ConfigureDraftRules()`, Formidable's shared "common" bucket,
which is folded into `"Submit"` as well (see [Profiles](profiles.md)). So a submit enforces them,
and a lenient draft save answers them too. An advisory in the other bucket,
`ConfigureSubmitRules()`, is one a draft save leaves alone. Either way, the same lifetime applies
once the issue has first been shown ([below](#how-long-does-a-warning-stay-on-screen)).

Severity has no bearing on when a rule runs. The two advisory rules answer live on each committed
change, exactly like the required-title error above them: `FormidableOptions.LiveProfile` defaults
to the submit profile, and the field is one the visitor has engaged. Under the default `UpdateOn`,
that commit lands as the field is left.

None of that changes what a warning or an info does to the submit itself: nothing. `IsValid` counts
only errors, and `SubmitOutcome.CanProceed` is the same flag for a submit that landed; a submit
displaced by a newer submit, a load, or the form being rebuilt or torn down reports `false` whatever
its report holds. `FormidableForm<TModel>.SubmitAsync()` routes on it: `OnValidSubmit` when
`CanProceed`, `OnInvalidSubmit` otherwise. A model that's all warnings and infos, with no errors,
submits successfully.

That's the whole authoring surface: mark severities, put the rule where it should run, read
`CanProceed` instead of counting errors by hand. What follows backs that guarantee with the actual
types, shows how a severity renders, and covers how long a warning stays visible once it's shown.

## Does a warning or an info block the submit?

No. Validity counts errors alone: `IsValid` is false only when the report carries an error-severity
issue, and `SubmitOutcome.CanProceed` is the same flag for a submit that landed (a displaced one
reports `false`, as above).

```csharp
/// <summary>True when the report carries no <see cref="ValidationSeverity.Error"/> issue; warnings and infos do not affect validity.</summary>
public bool IsValid => Errors.Count == 0;
```

<!-- Source: `src/Formidable/ValidationReport.cs` -->

```csharp
/// <summary>The result of a submit: whether it may proceed, the full report, and the names of the errors it showed.</summary>
/// <param name="CanProceed"><see langword="true"/> when the submit landed with a report holding no error; <see langword="false"/> for a blocked submit, and for one displaced by a newer submit, a load, or the engine being rebuilt or torn down, whatever its report holds. Warnings and infos do not block.</param>
/// <param name="Report">The submit profile's full report.</param>
/// <param name="VisibleErrorSummary">The distinct display names of the errors the submit showed, for a dialog or a summary line.</param>
/// <remarks>Grows by init-only properties, never by constructor parameters, so code constructing it keeps compiling.</remarks>
public sealed record SubmitOutcome(
    bool CanProceed,
    ValidationReport Report,
    IReadOnlyList<string> VisibleErrorSummary);
```

<!-- Source: `src/Formidable.Blazor/SubmitOutcome.cs` -->

`FormidableForm<TModel>.SubmitAsync()` runs the submit pipeline and routes on exactly that flag:
`OnValidSubmit` when `CanProceed`, `OnInvalidSubmit` otherwise. Both handlers reach the full
`SubmitOutcome`, so a passing submit's advisories are readable without a separate `Engine` read.
`OnValidSubmit` is handed the outcome directly; `OnInvalidSubmit` reads it off the
`FormidableInvalidSubmitContext` it is handed instead (see
[Component kit](component-kit.md#suppressing-the-automatic-focus) for what else that context is
for). So a model that's all warnings and infos, with no errors, submits successfully:

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

<!-- Excerpt from `samples/Formidable.Sample/Pages/SeverityLevels.razor` -->
The page also carries a teaching panel above the form.

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

<!-- Excerpt from `samples/Formidable.Sample/Pages/SeverityLevels.razor.cs` -->

Warnings never reach the `EditContext`'s own message store, either. Only error-severity issues are
written there, which is what built-in `InputBase`/`ValidationMessage` interop sees. The full issue
set, warnings included, is available through `GetIssues`/`GetVisibleIssues` and
`FieldState.HasWarnings`, which is what the kit's own message components and `FormidableSummary`
render from.

Why: [how the engine works: what the message store carries](how-the-engine-works.md#the-message-store-projection).

## Where do warnings and infos show, and how do I style them?

In the same places an error shows, and a stylesheet keys on the severity in the class.
`FormidableFieldMessage` renders every current issue for a field as a list item, whatever its
severity, and its collection-level sibling `FormidableCollectionMessage` does the same for a
collection. `FormidableSummary` groups the whole form's currently-visible issues by severity:
errors, then warnings, then infos, one list per non-empty group. Each group sits inside its own band
wrapper, `formidable-summary__band` plus `formidable-summary__band--{severity}`, and the band can
carry a heading of its own (see [Component kit](component-kit.md#heading-each-band)).

A message item carries `formidable-message` and one of `formidable-message--error`,
`formidable-message--warning` or `formidable-message--info`. A summary group carries
`formidable-summary__group` and one of `formidable-summary__group--error`,
`formidable-summary__group--warning` or `formidable-summary__group--info`. Style each in your own
stylesheet; Formidable ships no CSS of its own (see
[CSS and accessibility](css-and-accessibility.md)).

Each entry in a group is a `formidable-summary__item` wrapping a `formidable-summary__link` button,
which moves focus to the offending field. A group that
[`MaxItems` capped](component-kit.md#capping-the-list) ends in one further list item, and only when
the page supplied an `OverflowTemplate`. That item is `formidable-summary__overflow`, carrying what
the template renders for the entries held back, and no button.

One summary carries all three groups by default. A page that wants the blocking problems and the
commentary in different places renders a summary per band instead, with
`Show="SummaryFilter.Errors"` and `Show="SummaryFilter.Advisories"`. See
[Component kit](component-kit.md#showing-one-severity-band) for what that costs in announcements.

## How long does a warning stay on screen?

Until the rule stops failing or the form is reset. It comes back if the rule fails again: on the
live channel for a field the visitor has engaged, and on the submit channel for a field a submit has
shown.

On a field the visitor has committed a change to, a warning or an info appears on the field's own
row at that edit, exactly as an error would. What waits for the next submit is a warning on a field
nobody has engaged, or one whose rule the form's `LiveProfile` narrows past. A warning a server
reply put on screen follows the server round trip's own rule instead
([Server integration](server-integration.md#what-happens-to-a-server-error-when-i-edit-the-field)).

A warning that was showing when the user last submitted keeps refreshing live as they keep editing.
It clears the moment they fix it, and comes back if they break it again, since fixing it ends the
message rather than the watch. Neither direction waits for a second submit.

The live channel answers as the user edits. Its answer carries advisories and errors alike, for
every field the visitor has *engaged*, and by default it evaluates the same rules a submit would
(see [Disclosure](disclosure.md#why-isnt-my-message-showing-yet)).

On the submit channel, submit is the disclosure event for a warning or an info exactly as it is for
an error. A submit names the currently-rendered fields carrying a visible issue of any severity, and
each one stays watched until the form passes or is reset. A later blocked submit adds fields to the
watch and applying a server reply adds fields; short of a passing submit or a reset, nothing takes a
field back out.

After a submit, every edit re-checks the whole form after the `RefreshDebounce` wait (300 ms by
default; see [Options](options.md#refreshdebounce)), and that re-check updates what the watched
fields say. On the default `LiveProfile` the edit's own live check updates this channel's answer
too, so with no `LiveDebounce` set neither direction waits out a debounce at all. The re-check never
goes looking for a newly warning-worthy field that neither a submit nor a server reply has shown.

A field that was an error site at submit picks up a newly-appearing warning too, because it is
already watched. That holds whether or not it carried a warning at submit time. Only a
field with no visible issue of any severity at submit is left outside that re-check when it starts
failing a warning-severity rule — the same way a newly-failing error field is.

A passing submit ends every watch but one kind. An advisory the form is showing as it passes keeps
its watch and goes on updating as the visitor edits; everything else starts over. An error the
visitor fixed before that submit is no longer watched: for it to show again, something has to
disclose it afresh (the live channel for an engaged field, the next submit, or a server reply that
names it).

Why: [how the engine works: what a submit reveals](how-the-engine-works.md#the-reveal-ledgers).

## What does the server do with a warning?

Nothing that blocks. On the server, both the minimal-API `Validate<T>()` filter and the MVC
`[Validate]` attribute short-circuit to a 400 `ValidationProblemDetails` only when the report has at
least one error-severity issue. A report that's all warnings and infos lets the request through
unblocked, still readable in the handler through `GetFormidableValidationReport` (see
[the report in `HttpContext.Items`](server-integration.md#what-is-the-report-in-httpcontextitems-for)).

When a request *is* blocked, any warnings or infos in that same report ride along on the response's
`advisories` extension key. That key sits alongside the standard `errors` dictionary, not inside it.
`ApplyServerIssues` applies the whole body at the severity each issue carries: the errors block, the
advisories land on their own fields without blocking. See
[Server integration](server-integration.md) for the wire format and the round trip in full.

**Sample:** [`/severity`](../samples/Formidable.Sample/Pages/SeverityLevels.razor)
