using Formidable.Introspection;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Logging;

namespace Formidable.Blazor;

/// <summary>
/// The validation engine behind a Formidable form. Owned by a root component (engine lifetime
/// equals component lifetime), it is the single writer to its
/// <see cref="ValidationMessageStore"/>: error-severity issues become EditContext messages
/// (interop with built-in components), while the full issue state (warnings, codes) is exposed
/// via <see cref="GetFieldState"/> and the report objects. Issues resolve to
/// <see cref="FieldIdentifier"/>s through the introspector, so identity follows object
/// instances — reordering or removing collection rows cannot misattribute errors.
/// </summary>
/// <remarks>
/// Validation-result state (issue maps, the store, IsValidating, HasSubmitted) mutates on the
/// renderer's dispatcher and only from the still-current pass, for every validation pass — and
/// synchronously on the calling thread within <see cref="ApplyServerIssues"/>, which is why that
/// method (like <see cref="ValidateForSubmitAsync"/>) documents that it must be called from the
/// renderer's synchronization context. Pass bookkeeping (_version, _passCts, _currentPass,
/// _touched, _engagedFields, _pendingRefreshFields, _pendingDebouncedLiveFields) mutates
/// synchronously on the caller's context — except on the dispatcher for: _pendingRefreshFields,
/// when a refresh pass snapshots and clears it as it begins; _engagedFields, when a
/// rendered-field-set change drops the fields that have left the page from it — a live pass
/// snapshots the set as it begins and intersects that snapshot with the set again as its
/// verdict lands, but no pass ever removes an entry, and submit leaves the set standing;
/// _pendingDebouncedLiveFields, when the live
/// debounce timer fires and snapshots and clears it before starting the live pass those fields
/// triggered, and when a rendered-field-set change drops the fields that have left the page
/// from it; and _currentPass, which the pass that recorded it clears alongside IsValidating.
/// A third, independent mechanism covers IsFormValid: _formValidityStamp mutates synchronously
/// on the caller's context when a probe starts, and IsFormValid itself mutates on the
/// dispatcher, gated on that stamp still being the current one — the same last-write-wins shape
/// _version gates issue maps with, but the probe is not a pass, so it never touches
/// _currentPass, _passCts, or any of the pass bookkeeping above.
/// A fourth mechanism covers the per-rule verdict store: _editStamp mutates synchronously on the
/// caller's context as each field change arrives, while the store itself is read on the
/// dispatcher as a pass decides what is left to execute and written only in a pass's verdict
/// apply — version- and generation-gated, on the dispatcher, in the same dispatch as the issue
/// maps beside it — and its generation mutates with the rendered-field-set change, also on the
/// dispatcher. A pass in flight across such a change can therefore neither read a store that is
/// mutating under it nor write verdicts computed against a page that has since moved.
/// </remarks>
public sealed class FormValidationEngine<TModel> : IFormValidationEngine, IValidatingFieldReader, IDisposable
    where TModel : class
{
    private readonly TModel _model;
    private readonly IModelValidator<TModel> _validator;
    private readonly IModelIntrospector _introspector;
    private readonly FormidableOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly Func<Func<Task>, Task> _renderDispatch;
    private readonly ILogger? _logger;
    private readonly ValidationMessageStore _store;
    private readonly EventHandler<FieldChangedEventArgs> _fieldChangedHandler;

    private readonly Dictionary<FieldIdentifier, List<ValidationIssue>> _liveIssues = [];
    private readonly HashSet<FieldIdentifier> _touched = [];

    // The fields the user has committed a change to and that are still on the page — fed by
    // HandleFieldChanged, pruned by OnRenderedFieldsChanged, never cleared by any pass. A live
    // pass's verdict answers exactly this set (snapshotted as the pass begins), which is what
    // lets a cross-field verdict clear, or appear, on a field the triggering edit never named.
    // Distinct from _touched: touched gates CSS state classes, engagement gates live verdict
    // application.
    private readonly HashSet<FieldIdentifier> _engagedFields = [];

    private readonly HashSet<FieldIdentifier> _pendingRefreshFields = [];
    private readonly HashSet<FieldIdentifier> _pendingDebouncedLiveFields = [];
    private Dictionary<FieldIdentifier, List<ValidationIssue>> _submitIssues = [];
    private Dictionary<FieldIdentifier, List<ValidationIssue>> _submitAdvisories = [];
    private HashSet<FieldIdentifier> _submitVisible = [];
    private HashSet<FieldIdentifier> _advisoryVisible = [];
    private List<(FieldIdentifier Field, ValidationIssue Issue)> _appliedServerIssues = [];
    private ValidationIssue? _faultIssue;
    private IReadOnlyDictionary<FieldIdentifier, int>? _fieldOrder;

    /// <summary>The result every issue read shares when a field has nothing to say.</summary>
    private static readonly IReadOnlyList<ValidationIssue> NoIssues = [];

    private ITimer? _refreshTimer;
    private ITimer? _liveTimer;

    private CancellationTokenSource? _passCts;

    // One token for the probe's whole fire-and-forget lifetime, not per-probe like _passCts:
    // probes are never superseded by cancellation (the stamp handles that), so the only thing
    // this token ever needs to mean is "the engine is disposed" - cancelled once, in Dispose.
    private readonly CancellationTokenSource _probeCts = new();

    private bool _disposed;
    private HashSet<FieldIdentifier>? _validatingScope;

    private int _version;
    private int _formValidityStamp;

    // Counts field changes, so a stored rule verdict can be asked whether it still answers for
    // the model. A pass stamps every verdict it writes with the count it read as it began, and a
    // later pass reuses one only while the two stamps agree. What moves this is a notification,
    // not a mutation: a model changed without one moves the counter no more than an untouched
    // model does, so the agreement it reports is only ever as good as the notifications it is
    // given. The store clear in OnRenderedFieldsChanged covers the one silent change the engine
    // can see unaided — the rendered field set moving — and nothing covers the rest.
    private int _editStamp;

    // Every rule's most recent verdict, keyed by the rule's own identity — which is what makes
    // reuse bidirectional and order-independent: whichever pass ran a rule last at the current
    // stamp has answered it for every later pass at that stamp, whatever profile either ran.
    // An edit leaves the entries in place and merely strands their stamps; only a
    // rendered-field-set change empties the dictionary, so its size stays bounded by rule count.
    private readonly Dictionary<RuleIdentity, RuleVerdict> _ruleVerdicts = [];

    // Names the rendered field set the stored verdicts were computed against, the one staleness
    // the edit counter cannot see. OnRenderedFieldsChanged bumps it as it clears the store, and
    // a pass writes verdicts only while the generation it captured at its own begin still
    // stands — a pass in flight across a field-set change would otherwise put back, verbatim,
    // exactly what the clear just removed.
    private int _storeGeneration;

    // The pass in flight, or null when none is. One descriptor rather than a flag per kind so it
    // cannot go stale: every pass records itself here as it begins, a newer pass overwrites that
    // record outright, and only a pass that is still the current one ever clears it. A pass that
    // was superseded and therefore declined to write state — it is no longer current, so it must
    // not — cannot leave the engine deferring to a pass that ended long ago.
    private PassScope? _currentPass;

    /// <summary>
    /// Creates an engine bound to one model + edit context pair. <paramref name="logger"/> is
    /// optional — a direct construction with none supplied gets the engine's other diagnostics
    /// (Trace, <see cref="FormidableOptions.SuppressedIssueDiagnostic"/>) unaffected.
    /// </summary>
    public FormValidationEngine(
        TModel model,
        EditContext editContext,
        IModelValidator<TModel> validator,
        IModelIntrospector introspector,
        FormidableOptions options,
        TimeProvider? timeProvider = null,
        Func<Func<Task>, Task>? renderDispatch = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(editContext);
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(introspector);
        ArgumentNullException.ThrowIfNull(options);

        _model = model;
        EditContext = editContext;
        _validator = validator;
        _introspector = introspector;
        _options = options;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _renderDispatch = renderDispatch ?? (work => work());
        _logger = logger;
        _store = new ValidationMessageStore(editContext);
        _fieldChangedHandler = HandleFieldChanged;
        editContext.OnFieldChanged += _fieldChangedHandler;
        editContext.SetFieldCssClassProvider(new FormidableFieldCssClassProvider(options.CssClasses, this));
        Registry = new FieldRegistry();

        if (options.TrackFormValidity)
        {
            // A pristine, never-touched form still needs a truthful answer, so tracking gets its
            // first probe here rather than waiting for a first edit that may never come.
            _ = ProbeFormValidityAsync();
        }
    }

    /// <inheritdoc />
    public EditContext EditContext { get; }

    /// <inheritdoc />
    public FieldRegistry Registry { get; }

    /// <inheritdoc />
    public FormidableOptions Options => _options;

    /// <inheritdoc />
    public bool IsValidating { get; private set; }

    /// <inheritdoc />
    public bool HasSubmitted { get; private set; }

    /// <inheritdoc />
    public bool IsFormValid { get; private set; }

    /// <inheritdoc />
    public event Action? StateChanged;

    /// <inheritdoc />
    public event Action<Exception>? ValidationFaulted;

    /// <inheritdoc />
    public FieldState GetFieldState(FieldIdentifier field)
    {
        var (hasErrors, hasWarnings, hasInfos) = ScanFieldSeverities(field);

        return new FieldState(
            IsTouched: _touched.Contains(field),
            IsModified: EditContext.IsModified(field),
            IsValidating: IsFieldValidating(field),
            HasErrors: hasErrors,
            HasWarnings: hasWarnings,
            HasInfos: hasInfos);
    }

    /// <summary>
    /// Walks every channel that can hold an issue for <paramref name="field"/> — live, submit
    /// errors, submit advisories — stopping the moment an error, a warning, and an info have all
    /// been seen. <see cref="GetFieldState"/> and <see cref="IValidatingFieldReader.FieldAdvisories"/>
    /// both answer from this one walk rather than each re-reading the channels their own way.
    /// </summary>
    private (bool HasErrors, bool HasWarnings, bool HasInfos) ScanFieldSeverities(FieldIdentifier field)
    {
        var hasErrors = false;
        var hasWarnings = false;
        var hasInfos = false;

        if (_liveIssues.TryGetValue(field, out var live))
        {
            ScanSeverities(live, ref hasErrors, ref hasWarnings, ref hasInfos);
        }

        if (!(hasErrors && hasWarnings && hasInfos) && _submitIssues.TryGetValue(field, out var submit))
        {
            ScanSeverities(submit, ref hasErrors, ref hasWarnings, ref hasInfos);
        }

        if (!(hasErrors && hasWarnings && hasInfos) && _submitAdvisories.TryGetValue(field, out var advisories))
        {
            ScanSeverities(advisories, ref hasErrors, ref hasWarnings, ref hasInfos);
        }

        return (hasErrors, hasWarnings, hasInfos);
    }

    /// <summary>
    /// Whether a validation pass in flight currently covers <paramref name="field"/> — form-wide
    /// for a submit pass, scoped to the field(s) that triggered a live pass (one for an immediate
    /// edit, every field an open <see cref="FormidableOptions.LiveDebounce"/> window accumulated
    /// for a debounced one), scoped to the fields edited within the debounce window for a refresh
    /// pass. <see cref="GetFieldState"/> folds this into
    /// its own read; <see cref="IValidatingFieldReader"/> exposes it standalone for a caller (the
    /// css class provider) that wants only this, without the rest of what building a full
    /// <see cref="FieldState"/> costs.
    /// </summary>
    private bool IsFieldValidating(FieldIdentifier field) =>
        IsValidating && (_validatingScope is null || _validatingScope.Contains(field));

    /// <inheritdoc cref="IValidatingFieldReader.IsFieldValidating"/>
    bool IValidatingFieldReader.IsFieldValidating(FieldIdentifier field) => IsFieldValidating(field);

    /// <inheritdoc cref="IValidatingFieldReader.IsFieldTouched"/>
    bool IValidatingFieldReader.IsFieldTouched(FieldIdentifier field) => _touched.Contains(field);

    /// <inheritdoc cref="IValidatingFieldReader.FieldAdvisories"/>
    (bool HasWarnings, bool HasInfos) IValidatingFieldReader.FieldAdvisories(FieldIdentifier field)
    {
        var (_, hasWarnings, hasInfos) = ScanFieldSeverities(field);
        return (hasWarnings, hasInfos);
    }

    /// <inheritdoc cref="IValidatingFieldReader.InlineMessageRole"/>
    string? IValidatingFieldReader.InlineMessageRole => _options.InlineMessageRole;

    /// <inheritdoc />
    /// <remarks>
    /// Ordering is part of what a message list renders: the submit channel first (errors, then
    /// advisories), then the live channel, and the fault issue last — a fault is about the pass
    /// rather than the field, so it trails the field's own verdict. Everything after the errors is
    /// filtered against what is already showing, so a message a later channel repeats — a server
    /// response echoing an advisory the client already disclosed, a live rule failing the same way
    /// twice — reads once, in the position the first channel to say it gave it.
    /// </remarks>
    public IReadOnlyList<ValidationIssue> GetIssues(FieldIdentifier field)
    {
        var hasSubmit = _submitIssues.TryGetValue(field, out var submit);
        var hasAdvisories = _submitAdvisories.TryGetValue(field, out var advisories);
        var hasLive = _liveIssues.TryGetValue(field, out var live);
        var fault = _faultIssue is not null && field.Equals(ModelLevelField) ? _faultIssue : null;

        if (!hasSubmit && !hasAdvisories && !hasLive && fault is null)
        {
            return NoIssues;
        }

        var result = new List<ValidationIssue>();
        var showing = new HashSet<string>(StringComparer.Ordinal);

        if (hasSubmit)
        {
            AddShowing(result, showing, submit!);
        }

        if (hasAdvisories)
        {
            result.AddRange(ExceptShadowed(advisories!, showing));
        }

        if (hasLive)
        {
            result.AddRange(ExceptShadowed(live!, showing));
        }

        if (fault is not null)
        {
            result.Add(fault);
        }

        return result;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Collected channel by channel — the fault issue first, then every field's submit errors, then
    /// every field's advisories, then the live channel, each channel after the errors minus
    /// whatever is already showing for the same field, exactly as <see cref="GetIssues"/> filters
    /// them — and then sorted by field, if <see cref="SetFieldOrder"/> has been given a page to
    /// sort by. What the summary shows within a severity group is that final order, so the sort is
    /// what makes it the page's rather than the validator's; collecting channel by channel is still
    /// what decides which issues are in it at all.
    /// </remarks>
    public IReadOnlyList<VisibleIssue> GetVisibleIssues()
    {
        var result = new List<VisibleIssue>();

        // Only the filtered phases consult the shadow map, so it exists only when there is one to
        // consult it — a summary showing submit errors alone builds nothing.
        var showing = _liveIssues.Count > 0 || _submitAdvisories.Count > 0
            ? new Dictionary<FieldIdentifier, HashSet<string>>()
            : null;

        if (_faultIssue is not null)
        {
            result.Add(new VisibleIssue(ModelLevelField, _faultIssue));
            RecordShowing(showing, ModelLevelField, _faultIssue);
        }

        foreach (var (field, issues) in _submitIssues)
        {
            foreach (var issue in issues)
            {
                result.Add(new VisibleIssue(field, issue));
                RecordShowing(showing, field, issue);
            }
        }

        foreach (var (field, issues) in _submitAdvisories)
        {
            foreach (var issue in ExceptShadowed(issues, ShowingFor(showing!, field)))
            {
                result.Add(new VisibleIssue(field, issue));
            }
        }

        if (showing is not null)
        {
            foreach (var (field, issues) in _liveIssues)
            {
                foreach (var issue in ExceptShadowed(issues, ShowingFor(showing, field)))
                {
                    result.Add(new VisibleIssue(field, issue));
                }
            }
        }

        if (_fieldOrder is null)
        {
            return result;
        }

        // OrderBy is a stable sort, so several issues on one field keep the order the
        // validator produced them in.
        return result.OrderBy(v => _fieldOrder.TryGetValue(v.Field, out var ordinal) ? ordinal : int.MaxValue).ToList();
    }

    /// <summary>
    /// Answers whether <paramref name="issues"/> carries an error, a warning, and an info, stopping
    /// the moment all three are answered.
    /// </summary>
    private static void ScanSeverities(
        List<ValidationIssue> issues, ref bool hasErrors, ref bool hasWarnings, ref bool hasInfos)
    {
        foreach (var issue in issues)
        {
            if (issue.Severity == ValidationSeverity.Error)
            {
                hasErrors = true;
            }
            else if (issue.Severity == ValidationSeverity.Warning)
            {
                hasWarnings = true;
            }
            else if (issue.Severity == ValidationSeverity.Info)
            {
                hasInfos = true;
            }

            if (hasErrors && hasWarnings && hasInfos)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Adds every issue to <paramref name="result"/> and records its message as already showing for
    /// the field, which is what the live channel is then filtered against.
    /// </summary>
    private static void AddShowing(
        List<ValidationIssue> result,
        HashSet<string> showing,
        List<ValidationIssue> issues)
    {
        foreach (var issue in issues)
        {
            result.Add(issue);
            showing.Add(issue.Message);
        }
    }

    /// <summary>
    /// The one shadow rule behind every merged issue read: an issue is dropped when the same
    /// message is already showing for the same field, so a rule that fails in more than one channel
    /// reads as one message rather than several. Submit errors are the against-list every other
    /// channel is filtered by; the advisory channel is filtered too, which is what collapses a
    /// server response echoing an advisory the client already disclosed — the client's copy is
    /// there first, so the client's copy is the one that shows. <paramref name="showing"/> grows as
    /// issues pass, which is also what collapses two issues in one channel carrying the same
    /// message into one.
    /// </summary>
    private static IEnumerable<ValidationIssue> ExceptShadowed(
        List<ValidationIssue> issues,
        HashSet<string> showing)
    {
        foreach (var issue in issues)
        {
            if (showing.Add(issue.Message))
            {
                yield return issue;
            }
        }
    }

    /// <summary>Records one message as showing for a field, when a shadow map is being kept.</summary>
    private static void RecordShowing(
        Dictionary<FieldIdentifier, HashSet<string>>? showing,
        FieldIdentifier field,
        ValidationIssue issue)
    {
        if (showing is not null)
        {
            ShowingFor(showing, field).Add(issue.Message);
        }
    }

    /// <summary>The messages already showing for one field, created on first use.</summary>
    private static HashSet<string> ShowingFor(
        Dictionary<FieldIdentifier, HashSet<string>> showing,
        FieldIdentifier field)
    {
        if (!showing.TryGetValue(field, out var messages))
        {
            showing[field] = messages = new HashSet<string>(StringComparer.Ordinal);
        }

        return messages;
    }

    /// <inheritdoc />
    public void MarkTouched(FieldIdentifier field)
    {
        if (_touched.Add(field))
        {
            NotifyStateChanged();
        }
    }

    /// <summary>The model-level identifier issues with an empty path resolve to.</summary>
    internal FieldIdentifier ModelLevelField => new(_model, string.Empty);

    /// <summary>
    /// Supplies the order visible issues are reported in — the document order of the rendered
    /// fields, resolved by the host. Fields absent from the map sort after every mapped field:
    /// an unrendered field cannot be scrolled to, so where it lands does not matter. The
    /// model-level field is not one of those absentees: it is offered to the resolver like any
    /// other field, and because its element is this form's own <c>&lt;form&gt;</c> — which
    /// contains every field on the page — document order puts it first. Passing <c>null</c>
    /// restores validator order.
    /// </summary>
    /// <remarks>
    /// Silence here is conditional, not unconditional. An order matching the one already in force
    /// is taken without a word — which is the common answer, since most of what provokes a resolve
    /// leaves the reading order exactly where it was — and only an order that genuinely differs
    /// raises <see cref="StateChanged"/>. That keeps the cost the silence exists to avoid:
    /// notifying on every resolve would put a full re-render round behind every registration
    /// change, and on a page whose registered set churns as it scrolls (a virtualized collection)
    /// that is a steady stream of them, one that can re-order a summary out from under a click.
    /// What it must not do is stay quiet about a change. The order IS what a summary lists by, and
    /// a resolve necessarily lands after the render that produced the elements it measured, so a
    /// changed order has no other way onto the page: a reorder that registers nothing — rows moved
    /// under a <c>@key</c> — raises no pass, no edit and no server apply to ride on, and the
    /// summary would go on listing a page that is no longer there. Gating on difference is also
    /// what settles the sequence a host watching the page for those moves sets off: the re-render a
    /// changed order provokes mutates the DOM, the mutation resolves the order once more, and that
    /// second answer matches what is now in force, so it says nothing and the sequence stops.
    /// </remarks>
    /// <param name="order">Field-to-ordinal map, or <c>null</c>.</param>
    internal void SetFieldOrder(IReadOnlyDictionary<FieldIdentifier, int>? order)
    {
        if (SameFieldOrder(_fieldOrder, order))
        {
            return;
        }

        _fieldOrder = order;
        NotifyStateChanged();
    }

    /// <summary>
    /// Whether two resolved orders would sort visible issues identically. Count first, then every
    /// ordinal: both maps hold one entry per field the host rendered, so this is a handful of
    /// dictionary probes on a page and cheaper by far than the render it decides against.
    /// Null is an order in its own right — validator order — so a transition to or from it counts
    /// as a difference like any other.
    /// </summary>
    private static bool SameFieldOrder(
        IReadOnlyDictionary<FieldIdentifier, int>? current,
        IReadOnlyDictionary<FieldIdentifier, int>? replacement)
    {
        if (ReferenceEquals(current, replacement))
        {
            return true;
        }

        if (current is null || replacement is null || current.Count != replacement.Count)
        {
            return false;
        }

        foreach (var (field, ordinal) in current)
        {
            if (!replacement.TryGetValue(field, out var candidate) || candidate != ordinal)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Tells the engine that the set of fields the host has rendered moved — a collection row
    /// removed, a section collapsed, a conditional branch swapped for another. Nothing announces
    /// such a move as a field change, so this is the engine's only word that the page its
    /// disclosed verdicts describe is not the page on screen.
    /// </summary>
    /// <remarks>
    /// Five things follow, in that order. A live issue whose field is no longer rendered has no
    /// site left to display it, so it goes — from the issue map and from the message store
    /// alike, since the store is what a native <c>ValidationMessage</c> renders and what
    /// <c>EditContext.GetValidationMessages</c> answers from, and a message for an element that
    /// is gone is the very thing being removed. A field held by keep-registered has not left,
    /// which is what lets a virtualized row scroll out of view without losing its messages. A
    /// departed field also stops being one a live pass in flight will answer for, which is the
    /// same removal made one step earlier: without it the verdict that pass is about to write
    /// would put the pruned entry straight back. A field waiting only on an open
    /// <see cref="FormidableOptions.LiveDebounce"/> window — no pass in flight yet — leaves the same
    /// accumulator emptied of it, so the window's own eventual fire finds nothing left to answer for
    /// once every field it opened for has gone; a fire that finds nothing at all starts no pass. The
    /// stored rule verdicts go too: the stamp they are checked against counts edits, a
    /// rendered-field-set move is not one, so a verdict taken before the move would read as
    /// fresh while answering for a page — and, when a collection row was what left, a model —
    /// that no longer exists. Then a submitted form schedules a
    /// refresh, the one pass that recomputes the submit channel against the model as it stands.
    /// That channel still speaks only for the sticky submit-time visible set, never for what is
    /// rendered, so what a refresh drops is whatever the rules stop producing: a removed row's
    /// entry goes because its rule no longer fires, not because the row left the page — and an
    /// entry for a field a collapsed section took away survives, because the rule still fails.
    /// A form that has never been submitted schedules nothing: it has disclosed no verdict to
    /// reconcile, and a form the user has not asked to submit is not one to start reporting
    /// failures at.
    /// A model whose CONTENTS changed needs a field-change notification of its own regardless.
    /// Dropping issues is all this can do, and a rule that must START failing because a row left —
    /// a collection that requires at least one entry — produces an issue no pass has computed yet.
    /// </remarks>
    internal void OnRenderedFieldsChanged()
    {
        // Rendered-ness alone: DisclosureOverride is not consulted, so an issue an override
        // forces visible still goes once its field unregisters. The override decides whether an
        // unrendered field's issue may be SHOWN; nothing filters the live channel at all, and
        // re-deciding that here — per issue, over a map keyed by field — would suppress live
        // issues the engine otherwise reports. A field that never rendered can hold a live issue
        // too, since a consumer may notify a change for one, and it leaves the same way: what
        // is not on the page has nowhere to show it.
        //
        // Collected first: removing from the dictionary while enumerating its keys throws, and in
        // the common case (a page whose churn is rows arriving, or a virtualized one whose rows
        // stay registered) nothing leaves and there is no list to allocate.
        List<FieldIdentifier>? departed = null;
        foreach (var field in _liveIssues.Keys)
        {
            if (!IsRendered(field))
            {
                (departed ??= []).Add(field);
            }
        }

        if (departed is not null)
        {
            foreach (var field in departed)
            {
                _liveIssues.Remove(field);
            }

            // The issue map is not the only place these live: the message store holds the same
            // errors for the platform's own components to render, and dropping one without the
            // other leaves an engine read and an EditContext read disagreeing — the store still
            // offering a message whose field has no element left to focus. Rebuilding repairs
            // that and publishes it, which is why this is a rebuild rather than a bare
            // notification. Gated on something having actually left, the shape MarkTouched
            // already notifies with: a virtualized row scrolling out is keep-registered, so
            // nothing departs and a churning page pays nothing for this.
            RebuildStore();
        }

        // The same argument, applied one step earlier: a field that has left the page has no
        // verdict to receive, so it leaves the engaged set too. A live pass writes an entry for
        // every engaged field when its verdict lands — intersecting its pass-begin snapshot with
        // this set as it stands then — so leaving a departed field engaged would put back exactly
        // what was just pruned, and for a field whose rule still fails (a collapsed section,
        // whose object is still on the model) the entry put back is the issue itself. Nothing
        // filters the live channel at read time, so it would then stand until the next field-set
        // change. A field that comes back re-engages with its next committed change, which is
        // one edit away.
        //
        // Its own pass rather than the loop above, because the two sets do not have the same
        // members: a field engaged for the first time is awaiting a verdict while holding no
        // issue yet, and that is the case where the pass in flight is about to create the entry
        // rather than restore one.
        _engagedFields.RemoveWhere(field => !IsRendered(field));

        // One step earlier again: a field an open live-debounce window has only accumulated is
        // not yet pending any pass's verdict, only the window's own fire. Left in, that fire would
        // hand it to RunLivePassAsync and let it re-create the same entry the two prunes above
        // just removed, for a field the window opened for that no longer has anywhere to answer.
        _pendingDebouncedLiveFields.RemoveWhere(field => !IsRendered(field));

        // The verdict store empties whole, and the generation moves with it. The edit counter
        // cannot see this particular change — it counts field changes, and a change to which
        // fields are on the page changes which issues may be disclosed, and possibly the model
        // behind them, without a notification ever arriving — so being dropped outright is the
        // only thing that stops the next pass reusing verdicts computed against the page as it
        // stood before the move. The generation bump extends the same argument to a pass already
        // in flight: its verdict apply checks the generation it captured at begin and declines
        // to write, so the clear cannot be undone by work that predates it.
        _ruleVerdicts.Clear();
        _storeGeneration++;

        if (HasSubmitted)
        {
            ScheduleRefresh();
        }
    }

    private void HandleFieldChanged(object? sender, FieldChangedEventArgs e)
    {
        // First, before anything here can start a pass: a pass reads this counter as it begins
        // and stamps every verdict it writes with what it read, and a later pass reuses a
        // verdict only while the two stamps agree. Bumping it after a pass had already started
        // would let that pass's verdicts claim to answer for an edit they never saw.
        _editStamp++;

        MarkTouched(e.FieldIdentifier);

        // A notification is a committed change, and a committed change engages the field: from
        // here on every live pass answers it — its issue can clear, or appear, because of an
        // edit elsewhere — until it leaves the rendered page. Deliberately not folded into
        // MarkTouched: touched is CSS disclosure a component may grant on a bare blur, while
        // engagement is the engine's record of what the user has actually changed.
        _engagedFields.Add(e.FieldIdentifier);

        if (_options.LiveDebounce is { } debounce)
        {
            _pendingDebouncedLiveFields.Add(e.FieldIdentifier);
            ScheduleLiveDebounce(debounce);
        }
        else
        {
            _ = RunLivePassAsync([e.FieldIdentifier]);

            if (_options.TrackFormValidity)
            {
                _ = ProbeFormValidityAsync();
            }
        }

        if (HasSubmitted || SubmitInFlight)
        {
            // Armed at plain RefreshDebounce, whatever the live window's own width: verdict
            // reuse is keyed by rule and stamp rather than by which pass ran first, so a refresh
            // landing ahead of a still-open live window simply does the work and leaves that
            // window's pass nothing to execute — the same total cost in the other order.
            _pendingRefreshFields.Add(e.FieldIdentifier);
            ScheduleRefresh();
        }
    }

    /// <summary>
    /// Whether the pass currently in flight is a live pass — true from the moment a live pass
    /// becomes the current one until it ends, and false again as soon as any newer pass takes that
    /// place from it.
    /// </summary>
    private bool LiveInFlight => _currentPass?.Kind == PassKind.Live;

    /// <summary>
    /// Whether the pass currently in flight is a submit. Live and refresh passes both read this and
    /// stand down — submit is the higher-intent operation, and neither ever supersedes it.
    /// </summary>
    private bool SubmitInFlight => _currentPass?.Kind == PassKind.Submit;

    /// <summary>
    /// Whether the pass currently in flight is a refresh — read only by
    /// <see cref="RunDebouncedLivePassAsync"/>, which stands down for it. An IMMEDIATE live pass
    /// never reads this and never has: it is caused by a fresh edit, and that edit re-arms the
    /// refresh <see cref="ScheduleRefresh"/> already schedules, so cancelling an in-flight refresh
    /// costs nothing there. A DEBOUNCED live pass is different — the edit that will eventually
    /// supersede the refresh already happened when the debounce window opened, so nothing else
    /// re-arms it if this cancels it outright; deferring here is what keeps the refresh's own
    /// verdict from going permanently stale.
    /// </summary>
    private bool RefreshInFlight => _currentPass?.Kind == PassKind.Refresh;

    /// <summary>
    /// Cancels and disposes any in-flight pass's <see cref="CancellationTokenSource"/>, then starts a
    /// new one linked to <paramref name="external"/> and records the new pass as the current one.
    /// Only one pass (live, submit, or refresh) is ever in flight at a time — starting a new one
    /// supersedes whatever came before, which is exactly what taking over the descriptor means.
    /// </summary>
    private PassScope BeginPass(PassKind kind, CancellationToken external)
    {
        _passCts?.Cancel();
        _passCts?.Dispose();
        _passCts = CancellationTokenSource.CreateLinkedTokenSource(external);
        _version++;
        var pass = new PassScope(kind, _version, _passCts.Token);
        _currentPass = pass;
        return pass;
    }

    /// <summary>
    /// Retires the pass in flight: the validating flag, the field scope narrowing it, and the
    /// descriptor naming the pass all clear together, so nothing that runs afterwards can read a
    /// pass that has already ended. The caller establishes that the pass is still the current one —
    /// the verdict dispatch does that with its own version guard, <see cref="SetValidating"/> with
    /// its.
    /// </summary>
    private void EndPass()
    {
        IsValidating = false;
        _validatingScope = null;
        _currentPass = null;
    }

    /// <summary>
    /// Flips <see cref="IsValidating"/> and notifies, marshaled through <see cref="_renderDispatch"/> so
    /// the flip lands on the renderer's dispatcher rather than on whatever thread completed the pass.
    /// The write is skipped when <paramref name="pass"/> is no longer the current one —
    /// a superseded pass must not stomp a newer pass's state. <paramref name="fields"/> narrows which
    /// fields <see cref="GetFieldState"/> reports as validating: a live pass passes the field(s)
    /// that triggered it — one field for an immediate edit, every field an open live-debounce
    /// window accumulated for a debounced one; a refresh pass passes the fields edited within its
    /// debounce window; a submit pass passes <see langword="null"/> (form-wide, every field). Only
    /// meaningful when <paramref name="value"/> is <see langword="true"/> — clearing ends the pass outright (see
    /// <see cref="EndPass"/>), scope and descriptor with it.
    /// Also raises the EditContext's own validation-state notification, not just the engine's: a
    /// native InputBase re-renders on that event, not on <see cref="StateChanged"/>, so without it
    /// the Pending class a native input picks up through
    /// <see cref="FormidableFieldCssClassProvider"/> would light on the next store rebuild but have
    /// no later trigger to clear it once the pass ends.
    /// </summary>
    private Task SetValidating(bool value, PassScope pass, HashSet<FieldIdentifier>? fields = null) =>
        _renderDispatch(() =>
        {
            if (pass.Version == _version)
            {
                if (value)
                {
                    IsValidating = true;
                    _validatingScope = fields;
                }
                else
                {
                    EndPass();
                }

                NotifyStateChanged();
                EditContext.NotifyValidationStateChanged();
            }
            return Task.CompletedTask;
        });

    /// <summary>
    /// The one lifecycle every pass runs: begin (taking the version and the linked token that make
    /// the pass superseded-able), validate under <paramref name="profile"/>, dispatch the verdict
    /// only if this pass is still the current one, and end the pass exactly once however it left.
    /// Live, submit and refresh differ in what they hand in, not in how they run — a skeleton
    /// hand-rolled per kind is one where a single copy can quietly stop raising a notification, or
    /// stop clearing a flag, that the other two still do. How the validation step itself runs is
    /// a capability split: a validator that can validate rule by rule gets the verdict store —
    /// only the rules with no fresh verdict at this pass's stamp execute, and the report handed
    /// downstream is ASSEMBLED, every selected rule's issues in declaration order whether served
    /// from the store or just executed, so downstream never sees less than a whole-profile
    /// answer. Any other validator gets the whole profile in one call — correct, unoptimised. A
    /// pass whose every selected rule is fresh executes nothing and still runs this entire
    /// lifecycle, publishing its assembled verdict like any other.
    /// </summary>
    /// <param name="kind">
    /// Which lifecycle this is; it also decides the fault policy below, and whether freshness is
    /// consulted at all — a submit runs its full selection by fiat, repopulating the store on
    /// the way through.
    /// </param>
    /// <param name="profile">The profile the model is validated under.</param>
    /// <param name="external">
    /// The caller's own cancellation token, linked into the pass. Only a submit has one; live and
    /// refresh pass <see cref="CancellationToken.None"/>, which is what lets one cancellation filter
    /// serve all three — with no external token there is nothing a cancellation can mean except
    /// supersession by a newer pass.
    /// </param>
    /// <param name="beginScope">
    /// The fields the pending indicator covers, evaluated once the pass has begun: a refresh's scope
    /// is a snapshot-and-clear of an accumulator, so when it is taken is part of what it means.
    /// </param>
    /// <param name="applyVerdict">
    /// Writes this pass's verdict into engine state. Runs on the renderer's dispatcher, only while
    /// the pass is still current, and always in the same dispatch as the store rebuild publishing it.
    /// </param>
    /// <returns>
    /// The report this pass produced, or <see langword="null"/> when it ended before there was one —
    /// superseded mid-validation, or faulted.
    /// </returns>
    private async Task<ValidationReport?> RunPassAsync(
        PassKind kind,
        ValidationProfile profile,
        CancellationToken external,
        Func<HashSet<FieldIdentifier>?> beginScope,
        Action<ValidationReport> applyVerdict)
    {
        var pass = BeginPass(kind, external);

        // Captured synchronously with the begin, on the caller's own context, before anything
        // here awaits: the stamp names the model state this pass's verdicts will answer for —
        // recording it any later would let a verdict claim an edit the validation never saw —
        // and the generation names the rendered field set they are computed against, which is
        // what the verdict apply below checks before it writes the store.
        var editStamp = _editStamp;
        var generation = _storeGeneration;

        try
        {
            await SetValidating(true, pass, beginScope()).ConfigureAwait(false);

            var report = ValidationReport.Empty;
            Dictionary<RuleIdentity, RuleVerdict>? executed = null;
            try
            {
                if (_validator is IRuleLevelValidator<TModel> ruleLevel && ruleLevel.CanValidateByRule)
                {
                    // The store is only ever touched on the dispatcher, so the decision about
                    // what is left to execute rides one dispatch of its own — the same channel
                    // every other store mutation uses — rather than racing a clear or a write
                    // from off it. A selection error (a typo'd ruleset name, say) surfaces here
                    // exactly as a whole-profile validation surfaces it, through the fault
                    // policy below.
                    List<(RuleIdentity Rule, RuleVerdict? Fresh)> plan = null!;
                    await _renderDispatch(() =>
                    {
                        plan = BuildRulePlan(ruleLevel, profile, kind, editStamp);
                        return Task.CompletedTask;
                    }).ConfigureAwait(false);

                    (report, executed) = await ExecuteRulePlanAsync(ruleLevel, profile, plan, editStamp, pass.Token)
                        .ConfigureAwait(false);
                }
                else
                {
                    report = await _validator.ValidateAsync(_model, profile, pass.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (!external.IsCancellationRequested)
            {
                return null; // superseded by a newer pass, rather than cancelled by the caller
            }
            catch (Exception exception) when (kind != PassKind.Submit)
            {
                // A submit is the one pass someone is awaiting, so a validator that throws under
                // it has somewhere to surface: the caller's own try/catch. A live or refresh pass
                // is fire-and-forget, so its fault has to become form state and an event instead.
                await ReportFaultAsync(pass, exception).ConfigureAwait(false);
                return null;
            }

            await _renderDispatch(() =>
            {
                if (pass.Version != _version)
                {
                    return Task.CompletedTask; // superseded by a newer pass
                }

                _faultIssue = null;

                // The store write, gated one notch further than the channel writes around it:
                // the version says this pass is still the current one, and the generation says
                // the rendered field set its verdicts were computed against is still the one on
                // the page. A pass that crossed a field-set change still publishes its verdict —
                // the channels have their own reconciliation — but the store must not take back
                // entries the change's clear just removed. A faulted pass never reaches this
                // dispatch, and a superseded one stops at the version gate above, so neither
                // writes anything, store included.
                if (executed is not null && generation == _storeGeneration)
                {
                    foreach (var (rule, verdict) in executed)
                    {
                        _ruleVerdicts[rule] = verdict;
                    }
                }

                applyVerdict(report);

                // The pass ends here, not only in the finally below: retiring it before
                // RebuildStore's notification means the verdict and the cleared pending indicator
                // reach every subscriber in one round instead of two back-to-back ones — and the
                // descriptor is gone before any notification, so a handler that reacts by letting a
                // deferred refresh run cannot still see this pass as the one in flight.
                EndPass();

                RebuildStore();
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            return report;
        }
        finally
        {
            // The paths that never reach the verdict dispatch — a superseded pass, a cancelled one,
            // a faulted one — end here instead, and this no-ops once the dispatch has ended the
            // pass itself. It reaches one step further back than a per-kind hand-roll needs to: the
            // start notification is inside the try as well, so a subscriber throwing from there
            // leaves the flag cleared and costs one extra round, rather than leaving it stuck on.
            if (IsValidating)
            {
                await SetValidating(false, pass).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Decides, rule by rule, what a capability pass has left to execute: the profile's whole
    /// selection in declaration order, each rule paired with its fresh stored verdict where one
    /// exists — or with nothing, meaning the pass must run it. A submit pairs every rule with
    /// nothing by fiat: it is the disclosure event, and its full run is also what repopulates
    /// the store so the passes behind it start from answered rules. Runs on the dispatcher (the
    /// caller marshals), because the store is read here and mutates only there.
    /// </summary>
    private List<(RuleIdentity Rule, RuleVerdict? Fresh)> BuildRulePlan(
        IRuleLevelValidator<TModel> ruleLevel,
        ValidationProfile profile,
        PassKind kind,
        int editStamp)
    {
        var selection = ruleLevel.SelectRules(profile);
        var plan = new List<(RuleIdentity, RuleVerdict?)>(selection.Count);

        foreach (var rule in selection)
        {
            var fresh = kind != PassKind.Submit
                && _ruleVerdicts.TryGetValue(rule, out var verdict)
                && verdict.IsFreshFor(editStamp, profile)
                    ? verdict
                    : (RuleVerdict?)null;
            plan.Add((rule, fresh));
        }

        return plan;
    }

    /// <summary>
    /// Runs a capability pass's plan: executes the rules with no fresh verdict, sequentially —
    /// the order FluentValidation itself runs rules in — under the pass's own token, and
    /// assembles the report downstream consumes from every selected rule's issues in declaration
    /// order, served from the store or just computed. The verdicts for what actually executed
    /// are returned beside the report, stamped with the pass's begin stamp, for the
    /// version-and-generation-gated apply to write; nothing here touches the store itself, so a
    /// superseded or faulted pass's work simply evaporates with its locals.
    /// </summary>
    private async Task<(ValidationReport Report, Dictionary<RuleIdentity, RuleVerdict> Executed)> ExecuteRulePlanAsync(
        IRuleLevelValidator<TModel> ruleLevel,
        ValidationProfile profile,
        List<(RuleIdentity Rule, RuleVerdict? Fresh)> plan,
        int editStamp,
        CancellationToken token)
    {
        var executed = new Dictionary<RuleIdentity, RuleVerdict>();
        List<ValidationIssue>? issues = null;

        foreach (var (rule, fresh) in plan)
        {
            IReadOnlyList<ValidationIssue> ruleIssues;
            if (fresh is { } verdict)
            {
                ruleIssues = verdict.Issues;
            }
            else
            {
                var result = await ruleLevel.ValidateRuleAsync(_model, profile, rule, token).ConfigureAwait(false);
                ruleIssues = result.Report.Issues;
                executed[rule] = new RuleVerdict(ruleIssues, editStamp, result.IsProfileScoped, profile);
            }

            if (ruleIssues.Count > 0)
            {
                (issues ??= []).AddRange(ruleIssues);
            }
        }

        return (issues is null ? ValidationReport.Empty : new ValidationReport(issues), executed);
    }

    /// <summary>
    /// The single fault policy behind every pass that reports rather than rethrows: a form-level
    /// issue saying the verdict is incomplete, written only while <paramref name="pass"/> is still
    /// the current one, then <see cref="ValidationFaulted"/> for a host that wants to log it. The
    /// event is raised either way — a superseded pass's exception still happened.
    /// </summary>
    private async Task ReportFaultAsync(PassScope pass, Exception exception)
    {
        await _renderDispatch(() =>
        {
            if (pass.Version == _version)
            {
                _faultIssue = new ValidationIssue(
                    string.Empty,
                    "Validation could not run to completion; recent changes may not be fully validated.");
                RebuildStore();
            }
            return Task.CompletedTask;
        }).ConfigureAwait(false);

        ValidationFaulted?.Invoke(exception);
    }

    /// <summary>
    /// Runs a live pass triggered by <paramref name="triggeringFields"/> — one field for an
    /// immediate (non-debounced) edit, or every field an open debounce window accumulated before
    /// it fired. <paramref name="triggeringFields"/> become this one pass's pending-indicator
    /// scope; the verdict answers the engaged set, snapshotted as the pass begins. The indicator
    /// shows where the user acted, while the verdict covers every field the user has committed a
    /// change to — which is how a cross-field issue clears, or appears, on a field the
    /// triggering edit never named.
    /// </summary>
    private async Task RunLivePassAsync(IReadOnlyCollection<FieldIdentifier> triggeringFields)
    {
        if (SubmitInFlight)
        {
            return; // submit is the higher-intent operation; live/refresh passes never supersede it
        }

        // Snapshotted as the pass begins rather than when its verdict lands: the report answers
        // for the engaged set as this pass reads it. A field engaged after this line, under an
        // open debounce window, made an edit this report predates — its own window's fire
        // answers it, and with no debounce the engaging edit's own pass supersedes this one
        // outright. (The edit stamp the verdicts are filed under is captured the same way, at
        // the shared pass skeleton's own begin.)
        var engagedSnapshot = new HashSet<FieldIdentifier>(_engagedFields);

        // Captured beside the snapshot, and handed to the pass rather than read again later: the
        // option holds a mutable instance a consumer may swap at any moment, so the profile this
        // pass ran under is only knowable by remembering it here.
        var liveProfile = _options.LiveProfile;

        await RunPassAsync(
            PassKind.Live,
            liveProfile,
            CancellationToken.None,
            () => new HashSet<FieldIdentifier>(triggeringFields),
            report =>
            {
                // Every engaged field, not just the fields that triggered this pass: each live
                // pass answers the whole model under the same LiveProfile — executing what is
                // stale, assembling the rest from the store — so this report is a
                // complete answer for every field the user has committed a change to — a
                // superseded pass's fields included, since they were engaged before this pass
                // began. A field the report says nothing about gets an empty verdict, not a
                // skipped one, which is what lets a cross-field error clear when the fixing edit
                // lands on the OTHER field; the complement is the blank-row silence, where a
                // field never engaged gets no entry at all. Intersected with the engaged set as
                // it stands here, so a field pruned mid-pass — it left the page — does not have
                // its entry restored.
                var byField = GroupByResolvedField(report.Issues);
                foreach (var field in engagedSnapshot)
                {
                    if (_engagedFields.Contains(field))
                    {
                        _liveIssues[field] = byField.TryGetValue(field, out var forField) ? forField : [];
                    }
                }
            }).ConfigureAwait(false);
    }

    /// <summary>
    /// The whole-form validity probe behind <see cref="FormidableOptions.TrackFormValidity"/>: a
    /// standalone <see cref="FormidableOptions.SubmitProfile"/> validation, not an engine pass —
    /// it never calls <see cref="BeginPass"/>, writes nothing to the message store or the field
    /// registry, and never touches the pending indicator. Its only effect is
    /// <see cref="IsFormValid"/>, written only when the computed value differs from the current
    /// one (a flip, not every probe) and only while <c>stamp</c> is still the most recently taken
    /// one — a probe a newer probe has already superseded discards its own answer rather than
    /// overwrite a fresher one, the same last-write-wins discipline every pass verdict already
    /// follows via <c>_version</c>. A submit orders itself ahead of every probe the same way: its
    /// own verdict apply calls <see cref="AdoptFormValidity"/>, which bumps this same stamp, so a
    /// probe that started before the submit began cannot land after it and overwrite its answer
    /// — see <see cref="AdoptFormValidity"/> for why submit (and refresh) can adopt directly
    /// instead of merely invalidating. A probe never starts while a submit is already in flight,
    /// for the same reason a live pass never does (see <see cref="RunLivePassAsync"/>): submit is
    /// about to compute this exact quantity itself moments from now, so racing it buys nothing.
    /// A probe that faults reports the only way a fire-and-forget pass can: through
    /// <see cref="ValidationFaulted"/>, exactly as a live or refresh pass's own fault does — never
    /// a form-level fault issue, which would disclose something an invisible probe promises never
    /// to. Without this, a validator that throws on the submit profile (the profile a live pass
    /// never runs, so the two can genuinely disagree on whether a rule throws) would freeze
    /// <see cref="IsFormValid"/> at its last value with no diagnostic anywhere, silently stranding
    /// a disable-submit button in whatever state it was last in.
    /// </summary>
    private async Task ProbeFormValidityAsync()
    {
        if (SubmitInFlight)
        {
            return;
        }

        var stamp = ++_formValidityStamp;

        ValidationReport report;
        try
        {
            report = await _validator.ValidateAsync(_model, _options.SubmitProfile, _probeCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return; // disposed mid-probe - nothing left to write into
        }
        catch (Exception exception)
        {
            ValidationFaulted?.Invoke(exception);
            return;
        }

        await _renderDispatch(() =>
        {
            if (!_disposed && stamp == _formValidityStamp)
            {
                var isFormValid = report.IsValid;
                if (isFormValid != IsFormValid)
                {
                    IsFormValid = isFormValid;
                    NotifyStateChanged();
                }
            }

            return Task.CompletedTask;
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Adopts a whole-model <see cref="FormidableOptions.SubmitProfile"/> report's validity
    /// directly into <see cref="IsFormValid"/> — called from the submit and refresh verdict
    /// applies, both of which already compute exactly this quantity as part of their own pass, so
    /// there is nothing left for a separate probe to add. Bumps <c>_formValidityStamp</c>
    /// regardless of whether the value actually changes: a pass's own verdict is authoritative
    /// over any probe that merely happened to start earlier, so any such probe still in flight
    /// (or one that already finished and is only now reaching its write-back) must discard its
    /// answer rather than land after this one and overwrite it. A no-op when tracking is off — no
    /// probe can be in flight to invalidate, and nothing reads <see cref="IsFormValid"/>.
    /// </summary>
    private void AdoptFormValidity(ValidationReport report)
    {
        if (!_options.TrackFormValidity)
        {
            return;
        }

        _formValidityStamp++;

        var isFormValid = report.IsValid;
        if (isFormValid != IsFormValid)
        {
            IsFormValid = isFormValid;
            NotifyStateChanged();
        }
    }

    private FieldIdentifier Resolve(ValidationIssue issue) =>
        _introspector.Resolve(_model, issue.Path).ToFieldIdentifier(_model, issue.Path);

    /// <summary>
    /// The one report an issue with nowhere to render gets: a Trace line for a debugger, a logged
    /// warning for the host (WebAssembly's default provider is the browser console, so that channel
    /// needs no wiring to be seen), and the options callback for a page that wants to show its own
    /// list. Every site that decides an issue is suppressed ends here, so the three channels can
    /// never drift apart between them. A never-registered field additionally reaches
    /// <see cref="FormidableOptions.NeverRegisteredFieldDiagnostic"/> — the general channels above
    /// fire either way, unchanged.
    /// </summary>
    private void ReportSuppressed(ValidationIssue issue)
    {
        System.Diagnostics.Trace.WriteLine(
            $"Formidable: issue at '{issue.Path}' is suppressed - no rendered field registration matches and no disclosure override applies.");
        _logger?.LogWarning(
            "Formidable: issue at '{Path}' is suppressed - no rendered field registration matches and no disclosure override applies.",
            issue.Path);
        _options.SuppressedIssueDiagnostic?.Invoke(issue);

        if (_options.NeverRegisteredFieldDiagnostic is not null && !Registry.HasEverRegistered(Resolve(issue)))
        {
            _options.NeverRegisteredFieldDiagnostic(issue);
        }
    }

    private bool IsVisible(ValidationIssue issue, FieldIdentifier field)
    {
        var overridden = _options.DisclosureOverride?.Invoke(issue);
        if (overridden is not null)
        {
            return overridden.Value;
        }

        return IsRendered(field);
    }

    /// <summary>
    /// Whether the page is showing somewhere the field's issues could be read — the
    /// registration half of <see cref="IsVisible"/>, shared with
    /// <see cref="OnRenderedFieldsChanged"/> so the two cannot come to disagree about what having
    /// left the page means. The model-level field is always one of these: its element is the
    /// form's own, which is on the page for as long as the form is, and it never registers.
    /// </summary>
    private bool IsRendered(FieldIdentifier field) =>
        field.Equals(ModelLevelField) || Registry.IsRevealed(field);

    /// <summary>
    /// The submit report's non-error issues, resolved and filtered to visible fields, grouped by
    /// field. Used identically by both branches of <see cref="ValidateForSubmitAsync"/> — whether
    /// or not the same report also contains errors has no bearing on which of its advisories are
    /// disclosed.
    /// </summary>
    private Dictionary<FieldIdentifier, List<ValidationIssue>> ResolveVisibleAdvisories(ValidationReport report) =>
        report.Issues
            .Where(i => i.Severity != ValidationSeverity.Error)
            .Select(i => (Issue: i, Field: Resolve(i)))
            .Where(x => IsVisible(x.Issue, x.Field))
            .GroupBy(x => x.Field, x => x.Issue)
            .ToDictionary(g => g.Key, g => g.ToList());

    /// <summary>
    /// Resolves each issue to its field exactly once and groups by the result — one path parse and
    /// object walk per issue, rather than one per issue for every field waiting on the verdict.
    /// </summary>
    private Dictionary<FieldIdentifier, List<ValidationIssue>> GroupByResolvedField(
        IReadOnlyList<ValidationIssue> issues)
    {
        var grouped = new Dictionary<FieldIdentifier, List<ValidationIssue>>();

        foreach (var issue in issues)
        {
            var field = Resolve(issue);
            if (!grouped.TryGetValue(field, out var forField))
            {
                grouped[field] = forField = [];
            }

            forField.Add(issue);
        }

        return grouped;
    }

    private void RebuildStore()
    {
        _store.Clear();

        if (_faultIssue is not null)
        {
            _store.Add(ModelLevelField, _faultIssue.Message);
        }

        foreach (var (field, issues) in _submitIssues)
        {
            foreach (var issue in issues.Where(i => i.Severity == ValidationSeverity.Error))
            {
                _store.Add(field, issue.Message);
            }
        }

        foreach (var (field, issues) in _liveIssues)
        {
            // Deliberately not the shadow rule the issue reads share: the against-list here is this
            // field's submit issues alone and never grows, so two live errors carrying the same
            // message both reach the store, where ExceptShadowed would collapse them to one. The
            // store is the interop surface a native ValidationMessage/ValidationSummary renders
            // straight out, so narrowing the merge here would change what those components show
            // rather than what an engine read returns.
            var existing = _submitIssues.TryGetValue(field, out var submit)
                ? submit
                : (IReadOnlyList<ValidationIssue>)[];
            foreach (var issue in issues.Where(i => i.Severity == ValidationSeverity.Error))
            {
                if (!existing.Any(s => s.Message == issue.Message))
                {
                    _store.Add(field, issue.Message);
                }
            }
        }

        EditContext.NotifyValidationStateChanged();
        NotifyStateChanged();
    }

    private void NotifyStateChanged() => StateChanged?.Invoke();

    /// <inheritdoc />
    public async Task<SubmitOutcome> ValidateForSubmitAsync(CancellationToken cancellationToken = default)
    {
        // Mirrors the AspNetCore filters: normalize before the profile runs, not after, so the
        // profile - and the messages a blocked submit re-discloses - answer for the same
        // normalized values the model is left holding once this call returns.
        if (_options.NormalizeOnSubmit)
        {
            (_model as INormalizableModel)?.Normalize();
        }

        var canProceed = false;
        var summary = new List<string>();

        var passReport = await RunPassAsync(
            PassKind.Submit,
            _options.SubmitProfile,
            cancellationToken,
            static () => null, // a submit's pending indicator is form-wide: no scope narrows it
            report =>
            {
                HasSubmitted = true;

                // Submit takes the live channel over wholesale: every engaged field's verdict is
                // this report's. The engaged set itself stands — engagement records which fields
                // the user has committed changes to, and submitting does not un-commit them — so
                // the first post-submit live pass re-answers every engaged field.
                _liveIssues.Clear();

                // Submit already IS the whole-model SubmitProfile validation IsFormValid tracks —
                // adopting it here means a disable-submit button reflects the submit's own answer
                // the instant it lands, rather than waiting on the next field-change probe.
                AdoptFormValidity(report);

                if (report.IsValid)
                {
                    // No error-severity issues, so nothing blocks the submit - but the report can
                    // still carry warnings/infos (e.g. an error-free field with an advisory-severity
                    // rule). Warning lifetime is symmetric with errors: submit is the disclosure
                    // event regardless of which severity it reveals, so advisories are captured here
                    // exactly as the invalid branch below captures them, even though there is no
                    // error to pair them with.
                    canProceed = true;
                    _submitIssues = [];
                    _submitVisible = [];
                    _appliedServerIssues = [];

                    _submitAdvisories = ResolveVisibleAdvisories(report);
                    _advisoryVisible = _submitAdvisories.Keys.ToHashSet();
                }
                else
                {
                    var resolvedErrors = report.Errors
                        .Select(issue => (Issue: issue, Field: Resolve(issue)))
                        .ToList();
                    var visibleErrors = resolvedErrors.Where(x => IsVisible(x.Issue, x.Field)).ToList();

                    foreach (var suppressed in resolvedErrors.Where(x => !IsVisible(x.Issue, x.Field)))
                    {
                        ReportSuppressed(suppressed.Issue);
                    }

                    if (visibleErrors.Count == 0)
                    {
                        // Defensive gate: every failing field is hidden. Block anyway, with a
                        // form-level explanation instead of a silent no-op submit.
                        var gate = new ValidationIssue(
                            string.Empty,
                            "The form cannot be submitted because information that is not currently displayed is invalid.");
                        visibleErrors = [(gate, ModelLevelField)];
                    }

                    _submitIssues = visibleErrors
                        .GroupBy(x => x.Field, x => x.Issue)
                        .ToDictionary(g => g.Key, g => g.ToList());
                    _submitVisible = visibleErrors.Select(x => x.Field).ToHashSet();
                    _appliedServerIssues = [];

                    _submitAdvisories = ResolveVisibleAdvisories(report);

                    // Advisory sites are not necessarily error sites: a visible field can carry a
                    // warning while passing every error rule. Recording them separately is what lets
                    // the refresh pass keep those warnings current instead of dropping them (they are
                    // absent from _submitVisible, which holds error fields only).
                    _advisoryVisible = _submitAdvisories.Keys.ToHashSet();

                    summary = visibleErrors
                        .Select(x => x.Issue.DisplayName ?? x.Issue.Path)
                        .Select(name => name.Length == 0 ? "This form" : name)
                        .Distinct()
                        .ToList();
                }
            }).ConfigureAwait(false);

        // No report at all means the pass was superseded before one existed — report blocked
        // quietly. A pass superseded at the verdict dispatch has a report but never ran the writes
        // above, so the untouched locals are what it has to say: blocked, with nothing to point at.
        return passReport is null
            ? new SubmitOutcome(false, ValidationReport.Empty, [])
            : new SubmitOutcome(canProceed, passReport, summary);
    }

    /// <inheritdoc />
    public void ApplyServerIssues(IEnumerable<ValidationIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(issues);

        HasSubmitted = true;
        _faultIssue = null;

        // The payload is the server's CURRENT verdict, not an addition to its last one: undo
        // exactly what the previous call added before applying this call's issues. Each entry is
        // undone from the channel its own severity names — the same one the apply below put it in.
        // ValidationIssue is a record (value equality), so List<T>.Remove takes out one value-equal
        // entry — the instance the previous apply added, or (see RunRefreshPassAsync) the
        // message-matched refreshed issue standing in for it if a refresh landed since. If a
        // client-sourced issue happens to be value-identical to a previously-applied server issue,
        // removing either of the two equal entries is indistinguishable and acceptable.
        foreach (var (field, issue) in _appliedServerIssues)
        {
            var channel = ChannelFor(issue);
            if (channel.TryGetValue(field, out var tracked))
            {
                tracked.Remove(issue);
                if (tracked.Count == 0)
                {
                    channel.Remove(field);
                }
            }
        }

        _appliedServerIssues = [];

        foreach (var issue in issues)
        {
            var field = Resolve(issue);

            if (issue.Severity == ValidationSeverity.Error)
            {
                // Server-declared errors bypass the registry rather than defer to it: the server
                // judged what was actually submitted, and a verdict that blocks the save has to
                // reach the user whether or not the client happened to render the field. Only an
                // explicit override hides one.
                if (_options.DisclosureOverride?.Invoke(issue) == false)
                {
                    continue;
                }
            }
            else if (!IsVisible(issue, field))
            {
                // An advisory blocks nothing, so it follows the same disclosure rule the client's
                // own advisories follow rather than the bypass above. Nowhere to render it means it
                // is not shown, and the diagnostic names the registration that would have shown it.
                // No defensive gate stands in for it either — that gate exists because a hidden
                // error would otherwise fail a submit silently, and an advisory fails nothing.
                ReportSuppressed(issue);
                continue;
            }

            var channel = ChannelFor(issue);
            ValidationIssue tracked;
            if (channel.TryGetValue(field, out var existing))
            {
                // A client-sourced issue can already sit on this field carrying the exact same
                // message and severity — the field-fixed-then-re-broken case: the fix's refresh
                // found nothing to re-key the previous apply's bookkeeping against and dropped it
                // (see RunRefreshPassAsync), then the re-break's own client-side pass reproduced
                // an identical, untracked issue because _submitVisible is sticky. This apply has
                // nothing of its own to undo, so without this check it would append a second copy
                // of a message already shown. Adopt the existing instance instead of appending;
                // an issue that differs in message or severity is unrelated and still gets added.
                var matching = existing.FirstOrDefault(
                    i => i.Message == issue.Message && i.Severity == issue.Severity);
                if (matching is not null)
                {
                    tracked = matching;
                }
                else
                {
                    existing.Add(issue);
                    tracked = issue;
                }
            }
            else
            {
                channel[field] = [issue];
                tracked = issue;
            }

            // Sticky: reveal state never un-reveals a field. The two sets are separate because the
            // post-submit refresh watches them separately — a field can be an advisory site without
            // ever having been an error site, and keeps its advisory refreshed either way.
            var revealed = issue.Severity == ValidationSeverity.Error ? _submitVisible : _advisoryVisible;
            revealed.Add(field);

            _appliedServerIssues.Add((field, tracked));
        }

        RebuildStore();
    }

    /// <summary>
    /// The submit-time channel an issue belongs to. Errors and advisories are kept apart because a
    /// field can carry both at once and only the errors reach the message store, so a server issue
    /// is applied to, undone from, and matched within the channel its own severity names.
    /// </summary>
    private Dictionary<FieldIdentifier, List<ValidationIssue>> ChannelFor(ValidationIssue issue) =>
        issue.Severity == ValidationSeverity.Error ? _submitIssues : _submitAdvisories;

    /// <summary>
    /// Arms (or re-arms) the refresh timer at <see cref="FormidableOptions.RefreshDebounce"/>,
    /// from every arm site alike — an edit, a field-set change, an in-flight deferral's re-arm.
    /// A refresh that comes due while a debounced live window is still open is not a race worth
    /// scheduling around: whichever pass runs first executes the stale rules and stamps their
    /// verdicts, and the other finds nothing left to do.
    /// </summary>
    private void ScheduleRefresh()
    {
        if (_disposed)
        {
            // A refresh pass whose dispatch was still queued when the owning component went away
            // re-arms the timer from its own deferral branch; re-arming a disposed ITimer throws.
            return;
        }

        _refreshTimer ??= _timeProvider.CreateTimer(
            _ => _ = _renderDispatch(RunRefreshPassAsync),
            state: null,
            dueTime: Timeout.InfiniteTimeSpan,
            period: Timeout.InfiniteTimeSpan);
        _refreshTimer.Change(_options.RefreshDebounce, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Arms (or re-arms) the single timer behind <see cref="FormidableOptions.LiveDebounce"/>,
    /// exactly as <see cref="ScheduleRefresh"/> arms its own — one timer, created lazily and
    /// re-armed on every call, never one per field, which is what makes the debounce window
    /// shared across whatever fields change while it is open.
    /// </summary>
    private void ScheduleLiveDebounce(TimeSpan debounce)
    {
        if (_disposed)
        {
            // A debounced live pass whose dispatch was still queued when the owning component
            // went away re-arms nothing on its own, but a field change notification racing
            // Dispose could still reach here; re-arming a disposed ITimer throws.
            return;
        }

        _liveTimer ??= _timeProvider.CreateTimer(
            _ => _ = _renderDispatch(RunDebouncedLivePassAsync),
            state: null,
            dueTime: Timeout.InfiniteTimeSpan,
            period: Timeout.InfiniteTimeSpan);
        _liveTimer.Change(debounce, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// The live debounce timer's fire handler: defers first — mirroring
    /// <see cref="RunRefreshPassAsync"/>'s own defer-then-snapshot shape — and only once neither a
    /// submit nor a refresh is in flight does it snapshot and clear the fields accumulated since
    /// the window opened and run one live pass scoped to all of them. A snapshot left empty —
    /// every accumulated field pruned by <see cref="OnRenderedFieldsChanged"/> before the window
    /// closed — starts no live pass; the <see cref="FormidableOptions.TrackFormValidity"/> probe
    /// still fires either way, since it answers for the model rather than for any particular field.
    /// </summary>
    private Task RunDebouncedLivePassAsync()
    {
        if (_disposed)
        {
            // A dispatched fire can still run after the owning component went away; there is
            // nothing left here to validate against.
            return Task.CompletedTask;
        }

        if (SubmitInFlight || RefreshInFlight)
        {
            if (_options.LiveDebounce is { } liveDebounce)
            {
                // Re-arm and try again once the pass in flight finishes, touching neither the
                // accumulator nor a pass. Snapshotting here regardless (the shape every other fire
                // handler in this file uses) would still lose the fields: RunLivePassAsync's own
                // SubmitInFlight guard bails without writing them anywhere, and starting a live pass
                // against an in-flight refresh would cancel it via BeginPass without anything left to
                // re-arm it — the edit that would normally do that (see RefreshInFlight's remarks)
                // already happened when this window opened, so the refresh's own verdict would go
                // stale with no edit left to fix it. LiveInFlight is deliberately not checked: one
                // live pass superseding another is the existing, correct contract, and the
                // winner's verdict answers every engaged field — the superseded pass's fields
                // among them.
                ScheduleLiveDebounce(liveDebounce);
                return Task.CompletedTask;
            }

            // Options mutate in place, and this fire can land after a consumer has since cleared
            // LiveDebounce out from under an already-open window. There is no duration left to
            // re-arm with, so the window closes here instead of throwing, and the accumulator
            // empties with it: a field only ever accumulated here, never yet handed to a pass,
            // is the same speculative entry the departed-field prune in OnRenderedFieldsChanged
            // discards on identical reasoning — its next edit, or a submit, revalidates it
            // fresh, so nothing here needs to survive the debounce feature being turned off
            // while it was mid-window.
            _pendingDebouncedLiveFields.Clear();
            return Task.CompletedTask;
        }

        var fields = new HashSet<FieldIdentifier>(_pendingDebouncedLiveFields);
        _pendingDebouncedLiveFields.Clear();

        if (_options.TrackFormValidity)
        {
            // Rides the same debounced cadence as the live pass below rather than the raw
            // per-keystroke field-changed event this window exists to collapse — but it is not
            // itself an engine pass (see ProbeFormValidityAsync's own remarks: no BeginPass, no
            // pending indicator, no field scope), so it runs ahead of the empty-scope guard below
            // rather than under it. An edit that accumulated a now-departed field still moved the
            // model before that field left, and this is what keeps IsFormValid answering for it.
            _ = ProbeFormValidityAsync();
        }

        if (fields.Count == 0)
        {
            // Every field the window accumulated has since left the page —
            // OnRenderedFieldsChanged already pruned them out, leaving nothing here for an engine
            // pass to answer for. Starting one anyway would still validate the whole model under
            // the live profile for no field's sake, and would still flip IsValidating on for an
            // instant with an empty scope behind it.
            return Task.CompletedTask;
        }

        return RunLivePassAsync(fields);
    }

    private async Task RunRefreshPassAsync()
    {
        if (_disposed)
        {
            // A dispatched fire can still run after the owning component went away; there is
            // nothing left here to validate against, and BeginPass would cancel a _passCts that
            // Dispose already disposed.
            return;
        }

        if (SubmitInFlight || LiveInFlight)
        {
            // Defer and re-arm — the edit must still be revalidated once the pass in flight
            // finishes. Submit is the higher-intent operation and is never superseded; a live
            // pass is waited out for a different reason: starting here would cancel it (see
            // BeginPass) and this pass would then discard its own verdict for any field that was
            // not an error site at the last submit, so a single edit's answer would be lost on
            // both channels at once. An OPEN debounce window, by contrast, holds nothing back:
            // verdict reuse is keyed by rule and stamp, so a refresh that runs ahead
            // of the window simply executes the stale rules first and the window's own pass then
            // finds them answered. Waiting can in principle be starved by passes that never
            // quiesce — the same exposure the submit case has always carried, and a form whose
            // passes never settle has no moment at which a refresh would be meaningful anyway.
            ScheduleRefresh();
            return;
        }

        await RunPassAsync(
            PassKind.Refresh,
            _options.SubmitProfile,
            CancellationToken.None,
            () =>
            {
                // Snapshot-and-clear: this window's refresh flags exactly the fields the user
                // edited since the last refresh (or since submit, for the first one). Fields
                // edited while this pass is in flight land in the now-empty accumulator and are
                // flagged by the NEXT refresh instead — they are not lost, just deferred one
                // window (see ScheduleRefresh's re-arm on the deferral branch above for the
                // analogous case). If THIS pass is itself superseded before finishing (a live
                // pass never defers to a refresh — see BeginPass), its already-captured scope is
                // deliberately dropped, not merged into whatever runs next: the superseding pass
                // owns the indicator outright, exactly as one live pass already displaces
                // another's scope pre-submit — this is the same "post-submit editing reads like
                // pre-submit editing" symmetry, not a gap.

                // The empty case is reachable, and answers silently: a rendered-field-set change
                // (a virtualized panel scrolling rows into registration, a row departing) arms a
                // refresh with no field ever having been edited, and a re-armed timer can also
                // fire after an earlier refresh already snapshotted the union, leaving nothing
                // new accumulated. Either way the indicator's contract is "the fields edited
                // within this window" — an empty accumulator IS that answer, not a signal to fall
                // back to form-wide. The pass still answers for the whole model under the submit
                // profile; only which fields the indicator lights narrows.
                var edited = new HashSet<FieldIdentifier>(_pendingRefreshFields);
                _pendingRefreshFields.Clear();
                return edited;
            },
            report =>
            {
                // A refresh pass answers for the whole model under SubmitProfile too (its "scope"
                // parameter above narrows only the pending indicator, never what the verdict
                // answers for), so it is exactly as authoritative a source for IsFormValid as a
                // submit is.
                AdoptFormValidity(report);

                // The previous ApplyServerIssues call's bookkeeping, captured before the refresh's
                // own issues (below) replace both submit channels wholesale.
                var previouslyApplied = _appliedServerIssues;

                // Resurface only what the user already saw at submit AND is still failing —
                // fixed fields clear; fields revealed after submit stay quiet until the next submit.
                _submitIssues = report.Errors
                    .Select(issue => (Issue: issue, Field: Resolve(issue)))
                    .Where(x => _submitVisible.Contains(x.Field))
                    .GroupBy(x => x.Field, x => x.Issue)
                    .ToDictionary(g => g.Key, g => g.ToList());

                // Advisories follow the same "only what the user already saw" rule, but over the
                // union of the two submit-time sets: a field that was an error site keeps any
                // warning it also picked up, and a field that was only ever an advisory site keeps
                // its warning refreshed instead of disappearing on the first unrelated edit.
                _submitAdvisories = report.Issues
                    .Where(i => i.Severity != ValidationSeverity.Error)
                    .Select(issue => (Issue: issue, Field: Resolve(issue)))
                    .Where(x => _submitVisible.Contains(x.Field) || _advisoryVisible.Contains(x.Field))
                    .GroupBy(x => x.Field, x => x.Issue)
                    .ToDictionary(g => g.Key, g => g.ToList());

                // Re-key by MESSAGE within the entry's own severity channel, not by field: a field
                // can carry both a server-applied issue and an unrelated, independently-failing
                // client-sourced one at once (see ApplyServerIssues' own remarks), and adopting
                // everything the refresh wrote for a previously-applied field would sweep up that
                // unrelated client issue too, so the next apply would delete it — a worse bug than
                // the duplicate this guards against. Instead, for each issue the previous apply is
                // responsible for, adopt the refreshed issue on that field whose message and
                // severity match it (the duplicate this fixes is by definition an identical
                // message at an identical severity, so this still finds and replaces it) and drop
                // the bookkeeping entry when no refreshed issue matches — the server's contribution
                // is no longer part of what's shown, so there is nothing left to protect. Both
                // channels are rebuilt above before any of this runs, so an entry is matched
                // against the refreshed list it would actually have to stand in for.
                _appliedServerIssues = previouslyApplied
                    .Select(entry => (
                        entry.Field,
                        Issue: ChannelFor(entry.Issue).TryGetValue(entry.Field, out var current)
                            ? current.FirstOrDefault(i =>
                                i.Message == entry.Issue.Message && i.Severity == entry.Issue.Severity)
                            : null))
                    .Where(x => x.Issue is not null)
                    .Select(x => (x.Field, Issue: x.Issue!))
                    .ToList();
            }).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _disposed = true;
        EditContext.OnFieldChanged -= _fieldChangedHandler;
        _refreshTimer?.Dispose();
        _liveTimer?.Dispose();
        _passCts?.Cancel();
        _passCts?.Dispose();
        _probeCts.Cancel();
        _probeCts.Dispose();
        _store.Clear();
        EditContext.NotifyValidationStateChanged();
    }
}

/// <summary>
/// Which of the engine's three lifecycles a pass is running. The kind is what the engine's own
/// deference rules are written in — a refresh stands down for a live pass and for a submit, a live
/// pass stands down for a submit — and it is also what decides whether a validator's exception is
/// reported as form state or left to the caller awaiting the pass.
/// </summary>
internal enum PassKind
{
    /// <summary>One field's edit, revalidating the model under the live profile.</summary>
    Live,

    /// <summary>The form-wide pass a submit runs, under the submit profile.</summary>
    Submit,

    /// <summary>The debounced post-submit revalidation, also under the submit profile.</summary>
    Refresh,
}

/// <summary>
/// The identity a running pass carries: what it is, the version that decides whether it is still
/// the current pass, and the token it was started with. A pass is superseded — never waited for —
/// so every write it makes has to be gated on the version still being the engine's own.
/// </summary>
/// <param name="Kind">Which lifecycle this pass is running.</param>
/// <param name="Version">The engine version this pass took when it began.</param>
/// <param name="Token">The pass's cancellation token, linked to whatever the caller supplied.</param>
internal readonly record struct PassScope(PassKind Kind, int Version, CancellationToken Token);

/// <summary>
/// One rule's most recent answer, together with everything that decides whether a later pass may
/// serve it instead of running the rule again. Held as one value because the parts are
/// meaningless apart: issues with no idea which model state or which profile produced them
/// cannot be checked against anything.
/// </summary>
/// <param name="Issues">The issues the rule produced — empty when it passed, which is as much a
/// fact worth reusing as a failure is.</param>
/// <param name="EditStamp">The engine's edit count as the producing pass began — the model state
/// this verdict answers for.</param>
/// <param name="IsProfileScoped">Whether the execution consulted a child-scope decision that can
/// differ across profiles (see <see cref="RuleLevelResult.IsProfileScoped"/>) — when it did, the
/// verdict answers only for <paramref name="Profile"/> and an honest store re-runs the rule for
/// any other.</param>
/// <param name="Profile">The profile the producing pass ran under, remembered by reference
/// because the options holding the profiles are settable.</param>
internal readonly record struct RuleVerdict(
    IReadOnlyList<ValidationIssue> Issues,
    int EditStamp,
    bool IsProfileScoped,
    ValidationProfile Profile)
{
    /// <summary>
    /// Whether this verdict may be served to a pass that read <paramref name="editStamp"/> at
    /// its beginning and runs <paramref name="profile"/>: the stamps must agree, and a
    /// profile-scoped verdict additionally answers only for the very profile it ran under.
    /// </summary>
    /// <remarks>
    /// The stamp comparison says only that no field change has been notified since — which is
    /// what "the model is unchanged" means to an engine that is told about changes. A mutation
    /// made without one is invisible to it.
    /// </remarks>
    internal bool IsFreshFor(int editStamp, ValidationProfile profile) =>
        EditStamp == editStamp && (!IsProfileScoped || ReferenceEquals(Profile, profile));
}
