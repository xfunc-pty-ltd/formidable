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
    /// Null: the summary speaks for the whole form rather than for one field, so it registers
    /// nothing — it reads the engine's already-visible issues and has no field of its own to
    /// reveal.
    /// </summary>
    /// <param name="context">The context now being bound.</param>
    /// <returns>Always null.</returns>
    protected override FieldRegistration? Register(FormidableFormContext context) => null;

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

        var sequence = 0;
        builder.OpenElement(sequence++, "div");
        builder.AddAttribute(sequence++, "class", "formidable-summary");
        builder.AddAttribute(sequence++, "role", hasError ? "alert" : "status");

        foreach (var group in groups)
        {
            builder.OpenElement(sequence++, "ul");
            builder.AddAttribute(
                sequence++,
                "class",
                FormidableCss.SelectBySeverity(group.Key, ErrorGroupClass, WarningGroupClass, InfoGroupClass));

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
