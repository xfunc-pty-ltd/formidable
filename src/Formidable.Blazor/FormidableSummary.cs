using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;

namespace Formidable.Blazor;

/// <summary>
/// Renders a live, severity-grouped summary of the currently-visible validation issues across
/// the form — all of them unless <see cref="Show"/>, <see cref="GroupByField"/> or
/// <see cref="MaxItems"/> narrows what is listed — backed by
/// <see cref="IFormValidationEngine.GetVisibleIssues"/>: the same
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
/// <see cref="IFormidableFocusService"/>. A band lists one entry per issue it holds, unless
/// <see cref="GroupByField"/> collapses the issues sharing a field into a single entry or
/// <see cref="MaxItems"/> caps how many entries that band renders; an entry reads as its own
/// issue's message unless <see cref="ItemTemplate"/> supplies something else. Subscribes to the
/// cascaded engine's <see cref="IFormValidationEngine.StateChanged"/> so the summary stays
/// current through live edits, refreshes, and server-applied issues — not just at submit time.
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
    /// Invoked when a clicked issue's element does not take focus (focus miss) — nothing on the
    /// page carries the field's id, as for a virtualized row outside the render window, or the
    /// element that carries it will not take focus, as one inside a collapsed section will not.
    /// Return <c>true</c> after making the element reachable (scrolling its container, expanding
    /// that section) and the summary retries the focus exactly once; return <c>false</c> to leave
    /// the miss as-is. When unset, a miss is silently ignored, matching the component's
    /// pre-fallback behaviour.
    /// </summary>
    [Parameter]
    public Func<FieldIdentifier, ValueTask<bool>>? FocusFallback { get; set; }

    /// <summary>
    /// Invoked and awaited before a clicked entry's focus is attempted, so the page can make the
    /// target reachable first: dismissing a modal that covers it, expanding a collapsed section,
    /// switching to the tab it sits on. Receives the field about to be focused. Distinct from
    /// <see cref="FocusFallback"/>, which runs only after an attempt has already missed: this runs
    /// whether or not the element is reachable, and the try-fallback-retry pipeline behind it is
    /// unchanged. Same delegate shape as <c>FormidableForm</c>'s and <c>FormidableValidator</c>'s
    /// parameters of the same name, so one page callback wires to all three.
    /// </summary>
    /// <remarks>
    /// The callback must complete when the page is ready to be focused, not when it has begun
    /// getting ready — a dialog is the case that makes the difference visible. Closing one runs a
    /// transition, removes an overlay, and hands focus back to whatever opened it. That last step
    /// is what takes a premature focus move straight back, leaving the visitor somewhere neither
    /// they nor the summary chose; the transition and the overlay are why a target focused ahead
    /// of them is not yet one the visitor can use. So a dismissal callback completes on the
    /// dialog's own closed event, not on the state change that starts the close.
    /// <para>
    /// This callback runs once per click, before the first attempt: a fallback's retry does not
    /// run it a second time. A throw is treated exactly as one from <see cref="FocusFallback"/>
    /// is — it faults the click handler's task and the renderer surfaces it — so a page that
    /// wires both parameters gets one behaviour rather than two.
    /// </para>
    /// </remarks>
    [Parameter]
    public Func<FieldIdentifier, ValueTask>? PrepareFocus { get; set; }

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
    /// for anyone who does not opt in. No English default stands in for it: a band's label is the
    /// page's own wording in the page's own language, neither of which a component can guess.
    /// Pass a localized string to label the band.
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
    /// Replaces what each entry's button CONTAINS, and nothing else about the entry. The fragment
    /// receives that entry's <see cref="VisibleIssue"/>, the field and the issue together, so it
    /// can render the issue's <see cref="ValidationIssue.DisplayName"/> in place of the
    /// <see cref="ValidationIssue.Message"/> the default renders. Left unset, every entry renders
    /// that message.
    /// </summary>
    /// <remarks>
    /// The button around the fragment is not a template and is not meant to become one: its
    /// element and its <c>formidable-summary__link</c> class stay this component's, so an entry
    /// whose wording a page rewrote still reads to assistive technology as the button its list
    /// item promises, and still matches a stylesheet written against the kit's structural class
    /// names. What a click on that button does stays this component's too, so rewording an entry
    /// changes what it reads as and nothing about where the click takes the visitor. A summary
    /// that needs different markup around the entries is a summary a page builds for itself out
    /// of <see cref="IFormValidationEngine.GetVisibleIssues"/> and
    /// <see cref="IFormidableFocusService"/>, which this component does not stand in the way of.
    /// </remarks>
    [Parameter]
    public RenderFragment<VisibleIssue>? ItemTemplate { get; set; }

    /// <summary>
    /// Collapses each severity band's entries to one per field. <see langword="false"/>, the
    /// default, renders one entry per issue, so a field failing two rules is listed twice.
    /// <see langword="true"/> keeps the first issue of each distinct
    /// <see cref="VisibleIssue.Field"/> within the band and drops the rest — which also makes
    /// <see cref="MaxItems"/> a count of fields rather than of findings, the difference between
    /// "the first four problems" and "the first four fields with a problem".
    /// </summary>
    /// <remarks>
    /// Grouping is by field identity rather than by the issues' display names, though showing
    /// names is the usual reason to switch it on: two genuinely different fields are free to
    /// carry the same <c>WithName(...)</c>, and merging those would drop one of them from a list
    /// whose whole job is to be complete. Field identity is also what a click has to resolve, so
    /// it is the key already required to be unambiguous. The surviving issue carries its own
    /// display name for <see cref="ItemTemplate"/> to render.
    /// <para>
    /// It groups within a band and never across the summary, because a band is the grouping this
    /// component already imposes: a field carrying an error and a warning is listed
    /// once in each, which is one field described two ways rather than one description repeated.
    /// Entries hold the position of each field's first issue, so a grouped band is still in the
    /// order <see cref="IFormValidationEngine.GetVisibleIssues"/> reported. Every model-level
    /// issue shares one field identifier, so a band holding more than one of them renders a
    /// single entry for the lot.
    /// </para>
    /// </remarks>
    [Parameter]
    public bool GroupByField { get; set; }

    /// <summary>
    /// The most entries a severity band renders, or <see langword="null"/> — the default — for as
    /// many as the band has. What it counts is entries, which is issues by default and fields
    /// under <see cref="GroupByField"/>. Whatever the cap holds back is what
    /// <see cref="OverflowTemplate"/> receives.
    /// </summary>
    /// <remarks>
    /// The cap is per band, not per summary: a band is a list of its own with a heading of its
    /// own, and capping across the summary would let a run of warnings decide how many errors a
    /// visitor gets to read. A value at or above a band's entry count leaves that band exactly as
    /// an uncapped summary renders it. <c>0</c> is legal and renders the band's list with no
    /// entries in it, which — with an <see cref="OverflowTemplate"/> — is how a summary hands that
    /// fragment the band whole and lists none of it itself, and without one is an empty list and
    /// nothing else. A negative value throws from <see cref="OnParametersSet"/> instead of being
    /// read as <c>0</c>: this is a number pages arrive at by arithmetic, and arithmetic that has
    /// gone below zero is a mistake worth seeing rather than a list that quietly empties itself.
    /// </remarks>
    [Parameter]
    public int? MaxItems { get; set; }

    /// <summary>
    /// Renders after a band's last shown entry when <see cref="MaxItems"/> held entries back,
    /// receiving the entries that band held back: the ones it would have listed, minus the ones it
    /// did list, in the order it would have listed them. That list is never empty, because a band
    /// that held nothing back does not reach the fragment at all, so the list's <c>Count</c> is
    /// always one or more and is the whole of what a line that only counts the remainder needs.
    /// This component supplies the list item and its <c>formidable-summary__overflow</c> class;
    /// the fragment supplies what goes inside it.
    /// </summary>
    /// <remarks>
    /// The entries themselves, not their number, because a number answers only the questions that
    /// are about how many. An expander that reveals what was dropped, a tooltip listing it, a line
    /// that names the fields rather than counting them: each of those needs the entries. Each entry
    /// is a <see cref="VisibleIssue"/>, the same field-and-issue pair <see cref="ItemTemplate"/> is
    /// handed for a shown entry, so a page can render a held-back entry exactly as it renders a
    /// shown one.
    /// <para>
    /// What a band held back is that band's own, because <see cref="MaxItems"/> is counted per
    /// band: a band offers this fragment its own entries and no other band's, so a summary showing
    /// more than one band renders this fragment once for each band that held anything back.
    /// </para>
    /// <para>
    /// Left unset, a capped band renders nothing whatever in place of what it dropped. That is
    /// deliberate rather than an omission: what stands in for dropped entries is a sentence, and
    /// a sentence has a language and a plural rule behind it that this component cannot pick. A
    /// page that wants the line writes it here, in its own words.
    /// </para>
    /// </remarks>
    [Parameter]
    public RenderFragment<IReadOnlyList<VisibleIssue>>? OverflowTemplate { get; set; }

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

        if (MaxItems is < 0)
        {
            throw new InvalidOperationException(
                $"{nameof(FormidableSummary)}.{nameof(MaxItems)} cannot be negative, but was " +
                $"{MaxItems}. Pass 0 to render no entries, or null for no cap.");
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
    //
    // aria-atomic is spelled out because the two roles used here are atomic by default: status
    // and alert each carry an implicit aria-atomic of true, so without it every change inside a
    // region re-announces the whole of it, and correcting one field reads the entire remaining
    // error band back — assertively, in the alert region. False narrows each announcement to the
    // entries that actually changed, which is what a visitor working through a blocked submit
    // needs to hear. (A bare aria-live carries no such implication, which is why the message
    // lists spell out nothing.)
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
        builder.AddAttribute(sequence++, "aria-atomic", "false");

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

            var entries = EntriesFor(group);
            var shown = MaxItems is { } max && max < entries.Count ? max : entries.Count;

            // Without a key, sibling entries match by position, so correcting the field the first
            // entry names rewrites the text of every entry below it and drops the last one — a
            // whole band's worth of churn where one node should have left. Two halves make the key
            // work, and neither is sufficient alone.
            //
            // The first is what the key IS: the entry paired with its ordinal among the entries
            // EQUAL to it, never its position. Two issues carrying the same field, message,
            // severity, code and state are equal records — a shape a validator reaches by
            // declaring one rule twice — and Blazor rejects duplicate sibling keys outright, at
            // the first DIFF rather than the first render, so keying by value alone would paint a
            // form correctly and then throw on the next pass. An entry with no equal in its band
            // keeps ordinal 0 wherever it moves, so the pairing costs the duplicate case alone,
            // and a band showing one entry cannot collide with itself at all.
            //
            // The second is the numbering: every entry emits the SAME sequence numbers, which is
            // what a Razor @foreach compiles to and why the entries render inside their own
            // sequence-number region. A key decides which old entry a new one matches, but the
            // frames INSIDE it are still matched by sequence — so under a running counter the
            // matched entry's button and content would carry numbers seven higher than the entry
            // that replaced it, and the whole subtree would be destroyed and rebuilt under a li
            // that survived. Identical numbering settles what follows a band as well: the region
            // takes one sequence number however many entries it holds, so an entry arriving or
            // leaving leaves the overflow line and the band after it on the numbers they had.
            var occurrences = shown > 1 ? new Dictionary<VisibleIssue, int>(shown) : null;

            builder.OpenRegion(sequence++);

            for (var index = 0; index < shown; index++)
            {
                var entry = entries[index];
                var occurrence = 0;
                if (occurrences is not null)
                {
                    occurrences.TryGetValue(entry, out occurrence);
                    occurrences[entry] = occurrence + 1;
                }

                var entrySequence = 0;
                builder.OpenElement(entrySequence++, "li");
                builder.SetKey((entry, occurrence));
                builder.AddAttribute(entrySequence++, "class", "formidable-summary__item");

                builder.OpenElement(entrySequence++, "button");
                builder.AddAttribute(entrySequence++, "type", "button");
                builder.AddAttribute(entrySequence++, "class", "formidable-summary__link");
                builder.AddAttribute(entrySequence++, "onclick", EventCallback.Factory.Create(this, () => ActivateAsync(entry)));
                if (ItemTemplate is null)
                {
                    builder.AddContent(entrySequence++, entry.Issue.Message);
                }
                else
                {
                    builder.AddContent(entrySequence, ItemTemplate, entry);
                }

                builder.CloseElement();

                builder.CloseElement();
            }

            builder.CloseRegion();

            // Nothing at all when the band suppressed nothing, and nothing at all when it did but
            // no template says what that should read as: the alternative to a consumer's own
            // sentence is silence rather than an English one nobody asked for. Like the heading
            // block above, it takes sequence numbers only in the renders where it emits frames,
            // so a later band's numbering moves with it — which stays inside the region the
            // caller opened, and so cannot reach the region element whose DOM identity is the
            // contract.
            if (OverflowTemplate is not null && entries.Count > shown)
            {
                builder.OpenElement(sequence++, "li");
                builder.AddAttribute(sequence++, "class", "formidable-summary__overflow");
                builder.AddContent(sequence++, OverflowTemplate, entries.GetRange(shown, entries.Count - shown));
                builder.CloseElement();
            }

            builder.CloseElement();

            builder.CloseElement();
        }

        builder.CloseElement();
    }

    // What one band actually lists, before MaxItems caps it. GroupBy keeps the first occurrence of
    // each key in the order the keys first appeared, so a grouped band holds each field's first
    // issue in the position that issue already had, in whatever order GetVisibleIssues reported.
    // A List rather than the interface so a capped band can hand OverflowTemplate its tail as one
    // right-sized copy: GetRange is that slice, and it keeps the order this list is already in.
    private List<VisibleIssue> EntriesFor(IEnumerable<VisibleIssue> bandIssues) =>
        GroupByField
            ? bandIssues.GroupBy(v => v.Field).Select(g => g.First()).ToList()
            : bandIssues.ToList();

    // The one thing a clicked entry does: take the visitor to the field that entry names.
    private Task ActivateAsync(VisibleIssue entry) => FocusWithFallbackAsync(entry.Field);

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
        if (PrepareFocus is not null)
        {
            await PrepareFocus(field);
        }

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
