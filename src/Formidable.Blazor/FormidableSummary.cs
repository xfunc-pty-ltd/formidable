using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;

namespace Formidable.Blazor;

/// <summary>Lists the form's currently showing issues, grouped by severity into fixed-role live regions, each entry a button that moves focus to its field; <see cref="Show"/>, <see cref="GroupByField"/> and <see cref="MaxItems"/> narrow what is listed.</summary>
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

    /// <summary>Attributes splatted onto the wrapper element only, ahead of its computed values: <c>class</c> merges (the splatted value first, then <c>formidable-summary</c>); nothing reaches the regions inside.</summary>
    [Parameter(CaptureUnmatchedValues = true)]
    // The regions, and every element this component builds inside them, are contract, so a
    // consumer cannot re-role a region, or decorate anything under one, by splatting.
    public IReadOnlyDictionary<string, object>? AdditionalAttributes { get; set; }

    /// <summary>Called once when a clicked entry's field does not take focus; return <see langword="true"/> after making it reachable and the click retries once, <see langword="false"/> to leave the miss.</summary>
    /// <remarks>
    /// Unset, a miss is ignored and the click has no effect, where the roots report a
    /// diagnostic. The same delegate shape as <see cref="FormidableForm{TModel}.FocusFallback"/> and
    /// <see cref="FormidableValidator{TModel}.FocusFallback"/>, so one callback serves all three.
    /// </remarks>
    [Parameter]
    public Func<FieldIdentifier, ValueTask<bool>>? FocusFallback { get; set; }

    /// <summary>Awaited before a clicked entry's focus move, with the field about to be focused, so the page can make it reachable first.</summary>
    /// <remarks>
    /// Complete it when the page is ready to take focus: on a dialog's closed event, not on the
    /// state change that starts the close. It runs once per click, ahead of the first attempt
    /// and not again before a <see cref="FocusFallback"/> retry; a throw surfaces out of the
    /// click. The same delegate shape as <see cref="FormidableForm{TModel}.PrepareFocus"/> and
    /// <see cref="FormidableValidator{TModel}.PrepareFocus"/>.
    /// </remarks>
    [Parameter]
    // A Func rather than an EventCallback, because invoking one of those routes through
    // IHandleEvent on the component that supplied the handler, and ComponentBase's implementation
    // calls StateHasChanged for it: once for a handler that completes synchronously, and a second
    // time once an asynchronous one completes. This hook is awaited in the middle of a click's
    // focus move (after the entry's field is chosen, before its element is addressed), and a
    // render of the page belongs to what the page changed, not to its having been asked to make
    // the target reachable.
    public Func<FieldIdentifier, ValueTask>? PrepareFocus { get; set; }

    /// <summary>Which severities this summary lists, and so which regions it renders: <see cref="SummaryFilter.All"/> both, <see cref="SummaryFilter.Errors"/> the <c>alert</c> region alone, the three advisory filters the <c>status</c> region alone. Defaults to <see cref="SummaryFilter.All"/>.</summary>
    [Parameter]
    public SummaryFilter Show { get; set; } = SummaryFilter.All;

    /// <summary>The heading rendered above the error band as an <c>h{HeadingLevel}</c> with class <c>formidable-summary__heading</c>, wired to the band's list by <c>aria-labelledby</c>. Defaults to <see langword="null"/>, which renders neither.</summary>
    [Parameter]
    // No English default stands in for it: a band's label is the page's own wording in the
    // page's own language, neither of which a component can guess.
    public string? ErrorsHeading { get; set; }

    /// <summary>The heading above the warning band, on <see cref="ErrorsHeading"/>'s terms. Defaults to <see langword="null"/>.</summary>
    [Parameter]
    public string? WarningsHeading { get; set; }

    /// <summary>The heading above the info band, on <see cref="ErrorsHeading"/>'s terms. Defaults to <see langword="null"/>.</summary>
    [Parameter]
    public string? InfosHeading { get; set; }

    /// <summary>The HTML heading level, 1 to 6, the band headings render at; a value outside that range throws from <see cref="OnParametersSet"/>. Defaults to 2.</summary>
    [Parameter]
    // An int rather than an enum of the six levels, because heading levels are arithmetic at
    // both ends: the value becomes the digit in the h1-h6 tag name, and a summary slotted into a
    // page's own outline computes HeadingLevel="@(parentLevel + 1)", which an enum would turn
    // into a cast. The component cannot see where it sits in the page's outline, and the heading
    // parameters are strings rather than fragments, so this is the one way to supply the level;
    // the loud throw, rather than a clamp, is the guard that arithmetic needs.
    public int HeadingLevel { get; set; } = 2;

    /// <summary>The content of each entry's button, handed the entry's <see cref="VisibleIssue"/>; the button, its <c>formidable-summary__link</c> class and its click stay the component's. Defaults to <see langword="null"/>, which renders the issue's <see cref="ValidationIssue.Message"/>.</summary>
    [Parameter]
    // The button is not a template and is not meant to become one: a reworded entry still reads
    // to assistive technology as the button its list item promises, still matches a stylesheet
    // written against the structural class names, and still takes the visitor to the field. A
    // summary that needs different markup around the entries is one a page builds for itself out
    // of GetVisibleIssues and IFormidableFocusService.
    public RenderFragment<VisibleIssue>? ItemTemplate { get; set; }

    /// <summary>Whether each band lists one entry per field (the field's first issue) rather than one per issue, which makes <see cref="MaxItems"/> a count of fields. Defaults to <see langword="false"/>.</summary>
    [Parameter]
    // By field identity rather than by display name, though showing names is the usual reason
    // to switch it on: two different fields are free to carry the same WithName(...), and merging
    // those would drop one from a list whose whole job is to be complete. Field identity is also
    // what a click has to resolve, so it is the key already required to be unambiguous.
    public bool GroupByField { get; set; }

    /// <summary>The most entries a band lists, or <see langword="null"/> for all of them; per band, never per summary. <c>0</c> lists none, and a negative value throws from <see cref="OnParametersSet"/>. Defaults to <see langword="null"/>.</summary>
    [Parameter]
    // Per band because a band is a list of its own with a heading of its own, and capping across
    // the summary would let a run of warnings decide how many errors a visitor gets to read. A
    // negative value throws rather than reading as 0: this is a number pages arrive at by
    // arithmetic, and arithmetic that has gone below zero is a mistake worth seeing rather than
    // a list that quietly empties itself.
    public int? MaxItems { get; set; }

    /// <summary>What a band renders after its last shown entry when <see cref="MaxItems"/> held entries back, handed those entries in order (never an empty list) inside the component's <c>li.formidable-summary__overflow</c>. Defaults to <see langword="null"/>, which renders nothing there.</summary>
    [Parameter]
    // The entries themselves, not their number, because a number answers only the questions that
    // are about how many: an expander that reveals what was dropped, a tooltip listing it, a line
    // that names the fields, each needs the entries, and a line that only counts reads Count.
    // Unset, a capped band renders nothing in place of what it dropped, deliberately: what stands
    // in for dropped entries is a sentence, and a sentence has a language and a plural rule
    // behind it that this component cannot pick.
    public RenderFragment<IReadOnlyList<VisibleIssue>>? OverflowTemplate { get; set; }

    /// <summary>Registers nothing: the summary speaks for the whole form and reads the engine's visible issues.</summary>
    /// <param name="context">The context being bound.</param>
    /// <returns>Always <see langword="null"/>.</returns>
    protected override FieldRegistration? Register(FormidableFormContext context) => null;

    /// <summary>Checks <see cref="HeadingLevel"/> and <see cref="MaxItems"/>, then binds through the base.</summary>
    /// <exception cref="InvalidOperationException"><see cref="HeadingLevel"/> is outside 1 to 6, <see cref="MaxItems"/> is negative, or no <see cref="FormidableFormContext"/> is cascaded.</exception>
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

    /// <summary>Renders the wrapper and, per <see cref="Show"/>, the errors region and the advisories region, each banding the issues <see cref="IFormidableEngine.GetVisibleIssues"/> reports; renders nothing before the first bind.</summary>
    /// <param name="builder">The render tree builder.</param>
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

    // The one thing a clicked entry does: take the visitor to the field that entry names. Naming
    // the field is the click's whole contribution — the steps that get the visitor there are the
    // ones a root's own focus move runs, prepare and fallback included. The answer goes unread: a
    // click has nowhere else to send the visitor if the field will not take focus.
    private async Task ActivateAsync(VisibleIssue entry) =>
        await FirstErrorFocus.TryFocusAsync(FocusService, entry.Field, PrepareFocus, FocusFallback);

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
}
