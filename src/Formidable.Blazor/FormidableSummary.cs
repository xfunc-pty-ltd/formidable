using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;

namespace Formidable.Blazor;

/// <summary>
/// Renders a live, severity-grouped summary of every currently-visible validation issue across
/// the form, backed by <see cref="IFormValidationEngine.GetVisibleIssues"/> — the same
/// submit-then-live-deduped view <c>FormidableFieldMessage</c>/<c>FormidableCollectionMessage</c>
/// use per-field, but for the whole form at once, and in the order that view reports: where the
/// fields sit on the page, once the host has resolved that. Renders nothing while the form has no
/// visible issues; otherwise a region with one list per non-empty severity group (errors, then
/// warnings, then infos), each item a button that moves focus to the offending field via
/// <see cref="IFormidableFocusService"/>. The region carries <c>role="alert"</c> when any visible
/// issue is error-severity, and the politer <c>role="status"</c> when the visible issues are
/// advisories only — an errors-free submit that surfaces only warnings/infos should not interrupt
/// the way a blocking error does. Subscribes to the cascaded engine's
/// <see cref="IFormValidationEngine.StateChanged"/> so the summary stays current through live
/// edits, refreshes, and server-applied issues — not just at submit time.
/// </summary>
public sealed class FormidableSummary : FormidableComponentBase
{
    private const string ErrorGroupClass = "formidable-summary__group formidable-summary__group--error";
    private const string WarningGroupClass = "formidable-summary__group formidable-summary__group--warning";
    private const string InfoGroupClass = "formidable-summary__group formidable-summary__group--info";

    private const string ErrorBandClass = "formidable-summary__band formidable-summary__band--error";
    private const string WarningBandClass = "formidable-summary__band formidable-summary__band--warning";
    private const string InfoBandClass = "formidable-summary__band formidable-summary__band--info";

    [Inject]
    private IFormidableFocusService FocusService { get; set; } = default!;

    /// <summary>
    /// Invoked when a clicked issue's element is not in the DOM (focus miss) — e.g. a virtualized
    /// row outside the render window. Return <c>true</c> after making the element renderable
    /// (scrolling its container, expanding a section) and the summary retries the focus exactly
    /// once; return <c>false</c> to leave the miss as-is. When unset, a miss is silently ignored,
    /// matching the component's pre-fallback behaviour.
    /// </summary>
    [Parameter]
    public Func<FieldIdentifier, ValueTask<bool>>? FocusFallback { get; set; }

    /// <summary>
    /// Which severity band this summary renders. Defaults to <see cref="SummaryFilter.All"/> —
    /// today's single combined summary. Set to render one severity band on its own (e.g. a
    /// page that shows errors and advisories as two separate summaries); a filter that matches
    /// nothing renders nothing, the same as a clean form.
    /// </summary>
    [Parameter]
    public SummaryFilter Show { get; set; } = SummaryFilter.All;

    /// <summary>
    /// Heading for the error band, rendered as a <c>formidable-summary__heading</c> element and
    /// wired to that band's list via <c>aria-labelledby</c> — the id relationship is minted and
    /// applied by this component, not left for a consumer to get right. Null (the default) omits
    /// the heading and the <c>aria-labelledby</c> attribute entirely, leaving output unchanged
    /// for anyone who does not opt in. This library ships no user-facing text of its own, so
    /// there is no shipped English default here either; pass a localized string to label the
    /// band.
    /// </summary>
    [Parameter]
    public string? ErrorsHeading { get; set; }

    /// <summary>Heading for the warning band. See <see cref="ErrorsHeading"/> for the contract.</summary>
    [Parameter]
    public string? WarningsHeading { get; set; }

    /// <summary>Heading for the info band. See <see cref="ErrorsHeading"/> for the contract.</summary>
    [Parameter]
    public string? InfosHeading { get; set; }

    /// <summary>
    /// The HTML heading level (1-6) rendered for whichever band(s) currently carry a heading — see
    /// <see cref="ErrorsHeading"/>. Defaults to 2. This component has no visibility into where a
    /// consumer's summary sits inside their page's own heading outline, so it cannot pick a level
    /// that is guaranteed to stay monotonic there; because <see cref="ErrorsHeading"/> and its
    /// siblings are strings rather than a <c>RenderFragment</c>, a consumer has no other way to
    /// supply their own heading element either, so this is the correction. Must be between 1 and 6
    /// inclusive; an out-of-range value throws from <see cref="OnParametersSet"/> rather than being
    /// silently clamped to the nearest valid level, so a mistake here is visible immediately
    /// instead of shipping a heading level nobody chose.
    /// </summary>
    [Parameter]
    public int HeadingLevel { get; set; } = 2;

    /// <summary>
    /// Null: the summary speaks for the whole form rather than for one field, so it registers
    /// nothing — it reads the engine's already-visible issues and has no field of its own to
    /// reveal.
    /// </summary>
    /// <param name="context">The context now being bound.</param>
    /// <returns>Always null.</returns>
    protected override FieldRegistration? Register(FormidableFormContext context) => null;

    /// <inheritdoc />
    protected override void OnParametersSet()
    {
        if (HeadingLevel is < 1 or > 6)
        {
            throw new InvalidOperationException(
                $"{nameof(FormidableSummary)}.{nameof(HeadingLevel)} must be between 1 and 6 (a " +
                $"valid HTML heading level), but was {HeadingLevel}.");
        }

        base.OnParametersSet();
    }

    /// <inheritdoc />
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        if (Context is null)
        {
            return;
        }

        var visibleIssues = Context.Engine.GetVisibleIssues();
        if (Show != SummaryFilter.All)
        {
            visibleIssues = [.. visibleIssues.Where(v => Matches(v.Issue.Severity))];
        }

        if (visibleIssues.Count == 0)
        {
            return;
        }

        var groups = visibleIssues
            .GroupBy(v => v.Issue.Severity)
            .OrderBy(g => g.Key);

        var hasError = visibleIssues.Any(v => v.Issue.Severity == ValidationSeverity.Error);
        var headingTag = $"h{HeadingLevel}";

        var sequence = 0;
        builder.OpenElement(sequence++, "div");
        builder.AddAttribute(sequence++, "class", "formidable-summary");
        builder.AddAttribute(sequence++, "role", hasError ? "alert" : "status");

        foreach (var group in groups)
        {
            var heading = HeadingFor(group.Key);
            var headingId = heading is null ? null : HeadingIdFor(group.Key);

            // Emitted for every band, heading or not, so the sample's CSS has one consistent
            // shape to style rather than a wrapper that appears only when a heading is set.
            builder.OpenElement(sequence++, "div");
            builder.AddAttribute(
                sequence++,
                "class",
                FormidableCss.SelectBySeverity(group.Key, ErrorBandClass, WarningBandClass, InfoBandClass));

            if (heading is not null)
            {
                builder.OpenElement(sequence++, headingTag);
                builder.AddAttribute(sequence++, "id", headingId);
                builder.AddAttribute(sequence++, "class", "formidable-summary__heading");
                builder.AddContent(sequence++, heading);
                builder.CloseElement();
            }

            builder.OpenElement(sequence++, "ul");
            builder.AddAttribute(
                sequence++,
                "class",
                FormidableCss.SelectBySeverity(group.Key, ErrorGroupClass, WarningGroupClass, InfoGroupClass));
            if (headingId is not null)
            {
                builder.AddAttribute(sequence++, "aria-labelledby", headingId);
            }

            foreach (var visibleIssue in group)
            {
                builder.OpenElement(sequence++, "li");
                builder.AddAttribute(sequence++, "class", "formidable-summary__item");

                builder.OpenElement(sequence++, "button");
                builder.AddAttribute(sequence++, "type", "button");
                builder.AddAttribute(sequence++, "class", "formidable-summary__link");
                builder.AddAttribute(sequence++, "onclick", EventCallback.Factory.Create(this, () => FocusWithFallbackAsync(visibleIssue.Field)));
                builder.AddContent(sequence++, visibleIssue.Issue.Message);
                builder.CloseElement();

                builder.CloseElement();
            }

            builder.CloseElement();

            builder.CloseElement();
        }

        builder.CloseElement();
    }

    private bool Matches(ValidationSeverity severity) => Show switch
    {
        SummaryFilter.Errors => severity == ValidationSeverity.Error,
        SummaryFilter.Advisories => severity != ValidationSeverity.Error,
        SummaryFilter.Warnings => severity == ValidationSeverity.Warning,
        SummaryFilter.Infos => severity == ValidationSeverity.Info,
        _ => true,
    };

    private string? HeadingFor(ValidationSeverity severity) => severity switch
    {
        ValidationSeverity.Error => ErrorsHeading,
        ValidationSeverity.Warning => WarningsHeading,
        _ => InfosHeading,
    };

    // Reuses FormidableFieldId's own id-minting convention (owner-identity hash + sanitized
    // name) rather than inventing a second one: the "owner" here is this component instance, not
    // a field, since a severity band has no FieldIdentifier of its own to hash. That keeps the id
    // stable across this instance's re-renders and distinct across sibling FormidableSummary
    // instances on the same page (e.g. two disjoint Show-filtered summaries).
    private string HeadingIdFor(ValidationSeverity severity)
    {
        var suffix = severity switch
        {
            ValidationSeverity.Error => "errors-heading",
            ValidationSeverity.Warning => "warnings-heading",
            _ => "infos-heading",
        };
        return FormidableFieldId.For(new FieldIdentifier(this, suffix));
    }

    private async Task FocusWithFallbackAsync(FieldIdentifier field)
    {
        if (await FocusService.FocusAsync(field))
        {
            return;
        }

        if (FocusFallback is null || !await FocusFallback(field))
        {
            return;
        }

        await FocusService.FocusAsync(field);
    }
}
