using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;

namespace Formidable.Blazor;

/// <summary>
/// Renders a live, severity-grouped summary of every currently-visible validation issue across
/// the form, backed by <see cref="IFormValidationEngine.GetVisibleIssues"/> — the same
/// submit-then-live-deduped view <c>FormidableFieldMessage</c>/<c>FormidableCollectionMessage</c>
/// use per-field, but for the whole form at once, and in the order that view reports: where the
/// fields sit on the page, once the host has resolved that. The markup is one persistent
/// <c>&lt;div class="formidable-summary"&gt;</c> wrapper holding fixed-role region elements that
/// render from the first paint and stand empty while the form has nothing to show:
/// <c>formidable-summary__region--errors</c> carries <c>role="alert"</c> and receives the error
/// band, and <c>formidable-summary__region--advisories</c> carries the politer
/// <c>role="status"</c> and receives the warning and info bands — an errors-free submit that
/// surfaces only warnings/infos should not interrupt the way a blocking error does. No role ever
/// changes on any element, and every band that arrives after a region's own first render inserts
/// under a role the DOM already carried, which is what makes the insertion reliable for assistive
/// technology to announce — the same persistent-element reasoning behind
/// <c>FormidableFieldMessage</c>'s always-rendered list. <see cref="Show"/> decides which of the
/// two regions exist at all, and it is a parameter: changing it at runtime while matching issues
/// are already showing renders an added region in the same pass as that region's first band.
/// Inside a region, each non-empty severity group renders one band (errors, then warnings, then
/// infos), each item a button that moves focus to the offending field via
/// <see cref="IFormidableFocusService"/>. Subscribes to the cascaded engine's
/// <see cref="IFormValidationEngine.StateChanged"/> so the summary stays current through live
/// edits, refreshes, and server-applied issues — not just at submit time.
/// </summary>
public sealed class FormidableSummary : FormidableComponentBase
{
    private const string ErrorsRegionClass = "formidable-summary__region formidable-summary__region--errors";
    private const string AdvisoriesRegionClass = "formidable-summary__region formidable-summary__region--advisories";

    private const string ErrorGroupClass = "formidable-summary__group formidable-summary__group--error";
    private const string WarningGroupClass = "formidable-summary__group formidable-summary__group--warning";
    private const string InfoGroupClass = "formidable-summary__group formidable-summary__group--info";

    private const string ErrorBandClass = "formidable-summary__band formidable-summary__band--error";
    private const string WarningBandClass = "formidable-summary__band formidable-summary__band--warning";
    private const string InfoBandClass = "formidable-summary__band formidable-summary__band--info";

    [Inject]
    private IFormidableFocusService FocusService { get; set; } = default!;

    /// <summary>Additional attributes splatted onto the persistent wrapper element.</summary>
    /// <remarks>
    /// The splat lands on the wrapper (<c>formidable-summary</c>) and reaches nothing below it:
    /// the fixed-role regions, and every element this component builds inside them, are contract,
    /// so a consumer cannot re-role a region — or decorate anything under one — by splatting. On
    /// the wrapper the kit's usual ordering applies: the splat enters the render tree first and
    /// the computed values after, so a consumer-splatted <c>class</c> is merged rather than
    /// replaced — the splatted value first, then <c>formidable-summary</c>.
    /// </remarks>
    [Parameter(CaptureUnmatchedValues = true)]
    public IReadOnlyDictionary<string, object>? AdditionalAttributes { get; set; }

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
    /// Which severities this summary renders — and with it, which fixed-role regions its
    /// wrapper holds. Defaults to <see cref="SummaryFilter.All"/>, which renders both regions —
    /// the single combined summary. <see cref="SummaryFilter.Errors"/> renders the
    /// <c>role="alert"</c> region alone; the advisory filters (<see cref="SummaryFilter.Advisories"/>,
    /// <see cref="SummaryFilter.Warnings"/>, <see cref="SummaryFilter.Infos"/>) render the
    /// <c>role="status"</c> region alone — e.g. a page that shows errors and advisories as two
    /// separate summaries. A filter that matches nothing renders its region empty, the same as a
    /// clean form: the region persists so that whatever arrives in it later is announced from an
    /// element already carrying its role.
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
    /// <remarks>
    /// An <see langword="int"/> deliberately, not an enum of the six valid levels: heading
    /// levels are arithmetic at both ends. The value becomes the digit in the <c>h1</c>-<c>h6</c>
    /// tag name this component renders, and a summary slotted into a page's own outline computes
    /// <c>HeadingLevel="@(parentLevel + 1)"</c>, which an enum would turn into a cast. The loud
    /// throw is the guard that arithmetic needs.
    /// </remarks>
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

        // Each region builds inside its own sequence space (OpenRegion, the same isolation
        // AddContent wraps around any RenderFragment it appends). Blazor's diff matches sibling
        // frames by SEQUENCE NUMBER, so numbering the advisories region after the errors region's
        // variable-length band content would make its number depend on that content — and a
        // changed number reads as remove-plus-insert, replacing the very element whose stable
        // identity is this component's contract. Isolated spaces pin every region frame to the
        // same numbers on every render, whatever the bands are doing.
        builder.OpenElement(0, "div");
        builder.AddMultipleAttributes(1, AdditionalAttributes!);
        builder.AddAttribute(2, "class", FormidableCss.CombineClassNames(AdditionalAttributes, "formidable-summary"));

        if (Show is SummaryFilter.All or SummaryFilter.Errors)
        {
            builder.OpenRegion(3);
            BuildRegion(builder, ErrorsRegionClass, "alert", visibleIssues, errorRegion: true);
            builder.CloseRegion();
        }

        if (Show is not SummaryFilter.Errors)
        {
            builder.OpenRegion(4);
            BuildRegion(builder, AdvisoriesRegionClass, "status", visibleIssues, errorRegion: false);
            builder.CloseRegion();
        }

        builder.CloseElement();
    }

    // One fixed-role region: the element and its role render whether or not any issue currently
    // matches, so a band arriving later inserts into a live region assistive technology has
    // already been told about — a role, once in the DOM, never changes, and the element carrying
    // it outlives every band that comes and goes inside it. Show is what decides a region exists
    // at all, so a runtime Show change is where a region and its first band still share a render.
    // The caller wraps each call in its own sequence-number region, so the local counter here
    // starts at zero and the region element keeps its frame numbers — and with them its DOM
    // identity — however much content the sibling region carries.
    private void BuildRegion(
        RenderTreeBuilder builder,
        string regionClass,
        string role,
        IReadOnlyList<VisibleIssue> visibleIssues,
        bool errorRegion)
    {
        var sequence = 0;
        builder.OpenElement(sequence++, "div");
        builder.AddAttribute(sequence++, "class", regionClass);
        builder.AddAttribute(sequence++, "role", role);

        var groups = visibleIssues
            .Where(v => (v.Issue.Severity == ValidationSeverity.Error) == errorRegion && Matches(v.Issue.Severity))
            .GroupBy(v => v.Issue.Severity)
            .OrderBy(g => g.Key);

        var headingTag = $"h{HeadingLevel}";

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
