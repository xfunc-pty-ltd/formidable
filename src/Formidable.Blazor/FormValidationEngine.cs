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
/// Disclosure state follows a sources-plus-views shape. The sources — the live channel's
/// per-field verdicts, the submit channel's last submit-profile answer, the two reveal ledgers,
/// the server-applied issue store, the gate arming, the fault issue, HasSubmitted, and the store
/// and IsValidating beside them — mutate on the renderer's dispatcher and only from the
/// still-current pass, for every validation pass — and synchronously on the calling thread
/// within <see cref="ApplyServerIssues"/>, which is why that method (like
/// <see cref="ValidateForSubmitAsync"/>) documents that it must be called from the renderer's
/// synchronization context. What an issue read returns is COMPUTED from those sources at read
/// time — the live view is the live verdicts over the engaged set, the submit view is the last
/// submit-profile answer over the reveal ledgers merged with the server store and the
/// synthesized gate — and the <see cref="ValidationMessageStore"/> is a materialized projection
/// of the same views, rebuilt whenever a source moves. No channel is ever written except through
/// its sources, so a derived answer cannot be deleted piecemeal or left disagreeing with the
/// state it derives from. Pass bookkeeping (_version, _passCts, _currentPass,
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
/// _version gates the channel sources with, but the probe is not a pass, so it never touches
/// _currentPass, _passCts, or any of the pass bookkeeping above.
/// A fourth mechanism covers the per-rule verdict store: _editStamp mutates synchronously on the
/// caller's context as each field change arrives, while the store itself is read on the
/// dispatcher as a pass or a validity probe decides what is left to execute and written only in
/// a verdict landing — a pass's apply, version- and generation-gated, in the same dispatch as
/// the channel sources beside it, or the probe's own dispatch, generation-gated and skipped
/// when an edit has arrived since the probe began — and its generation mutates with the
/// rendered-field-set change, also on the
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

    // The live channel's verdict source: each engaged field's answer from the live pass that
    // most recently filed one — an empty list is a real answer (the field's rules passed), a
    // missing entry means no pass has answered the field yet. The live VIEW is this dictionary
    // read through the engaged set; nothing else ever filters it (see LiveIssuesFor).
    private readonly Dictionary<FieldIdentifier, List<ValidationIssue>> _liveVerdicts = [];

    private readonly HashSet<FieldIdentifier> _touched = [];

    // The fields the user has committed a change to and that are still on the page — fed by
    // HandleFieldChanged, pruned by OnRenderedFieldsChanged, never cleared by any pass. A live
    // pass's verdict answers exactly this set (snapshotted as the pass begins), which is what
    // lets a cross-field verdict clear, or appear, on a field the triggering edit never named.
    // Distinct from _touched: touched gates CSS state classes, engagement gates the live
    // channel — verdict application at a pass's apply, and disclosure at every read, since the
    // live view answers only for engaged fields.
    private readonly HashSet<FieldIdentifier> _engagedFields = [];

    private readonly HashSet<FieldIdentifier> _pendingRefreshFields = [];
    private readonly HashSet<FieldIdentifier> _pendingDebouncedLiveFields = [];

    // The submit channel's client verdict source: the last submit or refresh pass's whole-model
    // answer, resolved to fields — every error and every advisory, undisclosed ones included.
    // What the channel SHOWS is this source read through the reveal ledgers below; keeping the
    // full answer is what lets a ledger that grows mid-standing (a server apply reveals fields)
    // disclose an already-computed error without another pass. On a rule-capable validator the
    // published report is assembled from the per-rule verdict store, so this is the
    // submit-profile-selected verdicts resolved once per publish; a whole-profile fallback
    // validator publishes its last report here the same way, and every view reads identically.
    private Dictionary<FieldIdentifier, List<ValidationIssue>> _submitVerdictErrors = [];
    private Dictionary<FieldIdentifier, List<ValidationIssue>> _submitVerdictAdvisories = [];

    // The reveal ledgers: which fields a blocked submit (or a server apply) has disclosed as
    // error sites, and which as advisory sites. Reveal is FIELD-granular — one issue's yes
    // reveals its field, and a revealed field's answer discloses whole, per-issue answers
    // notwithstanding — and merges by UNION: once revealed, a field stays watched, so an error
    // that returns after being fixed rediscloses on the next refresh. Only a successful submit
    // resets them (errors un-reveal wholesale; advisories re-freeze to the fresh sites). Two
    // sets because the two channels reveal independently: a field can be an advisory site
    // without ever having been an error site.
    private readonly HashSet<FieldIdentifier> _revealedErrorFields = [];
    private readonly HashSet<FieldIdentifier> _revealedAdvisoryFields = [];

    // The server verdict source: what the most recent ApplyServerIssues call put on screen, per
    // field, per severity channel. An apply replaces it wholesale — the payload is the server's
    // CURRENT verdict, not an addition to its last one — and every submit and refresh clears it:
    // the server's answer is a snapshot of one round trip, and a newer whole-model answer
    // supersedes it (a matching client issue continues through the client view by construction).
    // Never mixed into the client sources above; the views merge the two at read time, client
    // copy first, which is what makes the client's copy the one that shows on identical text.
    private readonly Dictionary<FieldIdentifier, List<ValidationIssue>> _serverErrors = [];
    private readonly Dictionary<FieldIdentifier, List<ValidationIssue>> _serverAdvisories = [];

    // Armed by a blocked submit that disclosed no error at all — the all-suppressed case the
    // defensive gate exists for — and disarmed by any submit that disclosed something or passed.
    // Whether the gate actually SHOWS is the GateActive predicate over this and the sources
    // above; no refresh touches this flag, which is half of what makes the gate un-erasable.
    private bool _gateArmed;

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

    // Moves whenever a source the submit-coverage read derives from moves — a pass or probe
    // landing, the rendered-field-set clear — so the answer below can be cached per state
    // rather than recomputed per field per render. The edit stamp is the cache key's other
    // half; nothing else feeds the read.
    private int _coverageVersion;

    // The cached submit-coverage answer: whether every submit-selected rule has a current
    // verdict, and which fields those verdicts fail (resolved once per recompute — resolution
    // walks the model, and a per-read walk would put reflection behind every rendered field).
    // Stamps of -1 mean never computed; the profile is remembered by reference because the
    // options holding it are settable.
    private int _coverageCacheEditStamp = -1;
    private int _coverageCacheVersion = -1;
    private ValidationProfile? _coverageCacheProfile;
    private bool _coverageFresh;
    private HashSet<FieldIdentifier>? _coverageErrorFields;

    // The last coverage answer that came out fresh, and the two coordinates it answers for. They
    // are the cache key above minus exactly one member — the coverage version — and ignoring that
    // one member is the whole of what holding an answer means. A rendered-field-set change empties
    // the verdict store and moves that version, which leaves the walk below nothing to read about
    // a model the edit stamp says has not moved; the held answer covers that window, until the
    // re-answer the change arms lands or an edit strands it. The profile is part of the answer's
    // identity and not merely of the cache's: an answer selected under one submit profile says
    // nothing about the rules another selects. Every fresh answer is held, whichever branch
    // produced it, so the field means one thing throughout; only the rule walk has a use for one.
    // Held is the DERIVED answer alone, never a verdict: a stale verdict becomes a wrong message,
    // where a held vouch is a border the pass behind it corrects. And held is the last answer a
    // READ asked for, which is the only one worth holding — the Valid class IS that read, so a
    // field wearing green has necessarily asked for an answer at the stamp it wears it at, and a
    // stamp nothing ever asked about has no green to lose.
    private int _heldCoverageStamp = -1;
    private ValidationProfile? _heldCoverageProfile;
    private HashSet<FieldIdentifier>? _heldCoverageErrorFields;

    // The capability-less coverage source: the edit stamp at which the last COMPLETED
    // whole-model SubmitProfile evaluation — a submit, a refresh, or a fallback probe — began,
    // and the fields its report failed. A validator with no rule-level seam has no verdicts to
    // read, so "the submit answer is current" can only mean "that evaluation's begin stamp is
    // the current stamp"; -1 until one completes, which is what keeps a never-evaluated form
    // from wearing green it has not earned.
    private int _lastSubmitAnswerStamp = -1;
    private HashSet<FieldIdentifier> _lastSubmitAnswerErrorFields = [];

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
            HasInfos: hasInfos,
            WouldPassSubmit: WouldPassSubmit(field));
    }

    /// <summary>
    /// Whether the engine can vouch that a submit would not fail <paramref name="field"/> — the
    /// Valid class's <see cref="FieldState.WouldPassSubmit"/> conjunct: the submit-selected
    /// coverage is fresh at the current edit stamp AND carries no error-severity issue for the
    /// field, disclosed or not. Freshness is form-level, deliberately — which fields a PASSING
    /// rule speaks for is unknowable without per-rule field attribution the adapters cannot
    /// honestly provide, so one form-wide answer covers every field — while dirtiness is
    /// per-field, because a failing answer names its fields itself. What "coverage" means is
    /// the capability split: a rule-capable validator's coverage is the verdict store (every
    /// submit-selected rule fresh at the stamp, whichever pass or probe answered it); any other
    /// validator's coverage is the last completed whole-model SubmitProfile evaluation —
    /// submit, refresh, or probe — current exactly while its begin stamp is still the current
    /// edit stamp. The rule-capable coverage can also be a HELD answer: a rendered-field-set
    /// change empties the store while the edit stamp says the model those verdicts described has
    /// not moved, and <see cref="ServeHeldCoverage"/> covers that gap, so green describes the
    /// model rather than the page's registration churn. The fallback needs no cover of its own —
    /// its source is not the store, and a field-set change leaves it exactly as current as the
    /// edit stamp already found it.
    /// </summary>
    private bool WouldPassSubmit(FieldIdentifier field)
    {
        EnsureSubmitCoverageCurrent();
        return _coverageFresh
            && (_coverageErrorFields is null || !_coverageErrorFields.Contains(field));
    }

    /// <summary>
    /// Recomputes the cached submit-coverage answer when the edit stamp, a coverage source, or
    /// the submit profile has moved since it was last computed — once per state change rather
    /// than once per field per render, since the walk selects rules and resolves the failing
    /// verdicts' issues to fields. A selection that throws (a typo'd ruleset name, say) reads
    /// as stale coverage rather than taking the render down: the next pass surfaces the same
    /// exception through its own fault policy, which is where a configuration error belongs.
    /// Nothing held stands in for that one: a selection that cannot be walked leaves nothing
    /// able to vouch for anything.
    /// </summary>
    private void EnsureSubmitCoverageCurrent()
    {
        var profile = _options.SubmitProfile;
        if (_coverageCacheEditStamp == _editStamp
            && _coverageCacheVersion == _coverageVersion
            && ReferenceEquals(_coverageCacheProfile, profile))
        {
            return;
        }

        _coverageCacheEditStamp = _editStamp;
        _coverageCacheVersion = _coverageVersion;
        _coverageCacheProfile = profile;
        _coverageFresh = false;
        _coverageErrorFields = null;

        if (_validator is not IRuleLevelValidator<TModel> ruleLevel || !ruleLevel.CanValidateByRule)
        {
            if (_lastSubmitAnswerStamp == _editStamp)
            {
                _coverageFresh = true;
                _coverageErrorFields = _lastSubmitAnswerErrorFields.Count > 0
                    ? _lastSubmitAnswerErrorFields
                    : null;
                HoldCoverage(profile);
            }

            return;
        }

        HashSet<FieldIdentifier>? errorFields = null;
        try
        {
            foreach (var rule in ruleLevel.SelectRules(profile))
            {
                if (!_ruleVerdicts.TryGetValue(rule, out var verdict)
                    || !verdict.IsFreshFor(_editStamp, profile))
                {
                    // A rule with no current answer. The held answer stands in for it while it
                    // still answers for the model as it stands — a rendered-field-set change
                    // empties the store without moving the edit stamp, so an answer computed at
                    // that stamp is one nothing since has invalidated. Otherwise coverage is
                    // stale and its fields are moot.
                    ServeHeldCoverage(profile);
                    return;
                }

                foreach (var issue in verdict.Issues)
                {
                    if (issue.Severity == ValidationSeverity.Error)
                    {
                        (errorFields ??= []).Add(Resolve(issue));
                    }
                }
            }
        }
        catch (Exception)
        {
            return; // selection failed: nothing can vouch for anything — stale
        }

        _coverageFresh = true;
        _coverageErrorFields = errorFields;
        HoldCoverage(profile);
    }

    /// <summary>
    /// Holds the answer <see cref="EnsureSubmitCoverageCurrent"/> has just computed, together
    /// with the edit stamp and profile it answers for. Called only where the answer came out
    /// fresh: a stale read is not an answer, and holding one would be vouching for nothing.
    /// </summary>
    private void HoldCoverage(ValidationProfile profile)
    {
        _heldCoverageStamp = _editStamp;
        _heldCoverageProfile = profile;
        _heldCoverageErrorFields = _coverageErrorFields;
    }

    /// <summary>
    /// Serves the held answer in place of a recomputed one, for the single case in which the
    /// verdicts it was derived from are gone while the model it describes is not: a
    /// rendered-field-set change empties the store and never moves the edit stamp. Matching that
    /// stamp is therefore exactly the condition "nothing but the rendered field set has changed
    /// since this was computed", and the profile match keeps an answer about one selection of
    /// rules from vouching for another. Nothing else is asked, and nothing needs to be: the
    /// change that emptied the store also arms the pass that replaces the held answer with an
    /// earned one, and an edit before that lands moves the stamp past it. Between them they bound
    /// what a held answer can be wrong about: a field-set change that silently mutated the model
    /// leaves the vouch answering from the model as it was until that pass lands, which is the
    /// lag <see cref="IsFormValid"/> has always carried, on the same terms.
    /// </summary>
    private void ServeHeldCoverage(ValidationProfile profile)
    {
        if (_heldCoverageStamp != _editStamp || !ReferenceEquals(_heldCoverageProfile, profile))
        {
            return;
        }

        _coverageFresh = true;
        _coverageErrorFields = _heldCoverageErrorFields;
    }

    /// <summary>
    /// Walks every channel view that can hold an issue for <paramref name="field"/> — live,
    /// submit errors, submit advisories — stopping the moment an error, a warning, and an info
    /// have all been seen. <see cref="GetFieldState"/> and <see cref="IValidatingFieldReader.FieldAdvisories"/>
    /// both answer from this one walk rather than each re-reading the views their own way.
    /// </summary>
    private (bool HasErrors, bool HasWarnings, bool HasInfos) ScanFieldSeverities(FieldIdentifier field)
    {
        var hasErrors = false;
        var hasWarnings = false;
        var hasInfos = false;

        if (LiveIssuesFor(field) is { } live)
        {
            ScanSeverities(live, ref hasErrors, ref hasWarnings, ref hasInfos);
        }

        if (!(hasErrors && hasWarnings && hasInfos) && SubmitErrorsFor(field) is { } submit)
        {
            ScanSeverities(submit, ref hasErrors, ref hasWarnings, ref hasInfos);
        }

        if (!(hasErrors && hasWarnings && hasInfos) && SubmitAdvisoriesFor(field) is { } advisories)
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

    /// <inheritdoc cref="IValidatingFieldReader.WouldPassSubmit"/>
    bool IValidatingFieldReader.WouldPassSubmit(FieldIdentifier field) => WouldPassSubmit(field);

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
        var submit = SubmitErrorsFor(field);
        var advisories = SubmitAdvisoriesFor(field);
        var live = LiveIssuesFor(field);
        var fault = _faultIssue is not null && field.Equals(ModelLevelField) ? _faultIssue : null;

        if (submit is null && advisories is null && live is null && fault is null)
        {
            return NoIssues;
        }

        var result = new List<ValidationIssue>();
        var showing = new HashSet<string>(StringComparer.Ordinal);

        if (submit is not null)
        {
            AddShowing(result, showing, submit);
        }

        if (advisories is not null)
        {
            result.AddRange(ExceptShadowed(advisories, showing));
        }

        if (live is not null)
        {
            result.AddRange(ExceptShadowed(live, showing));
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
        // consult it — a summary showing submit errors alone builds nothing. Counted from the
        // sources rather than their filtered views, which errs only toward building a map that
        // then goes unconsulted.
        var showing = _liveVerdicts.Count > 0 || _submitVerdictAdvisories.Count > 0 || _serverAdvisories.Count > 0
            ? new Dictionary<FieldIdentifier, HashSet<string>>()
            : null;

        if (_faultIssue is not null)
        {
            result.Add(new VisibleIssue(ModelLevelField, _faultIssue));
            RecordShowing(showing, ModelLevelField, _faultIssue);
        }

        foreach (var (field, issues) in SubmitErrorEntries())
        {
            foreach (var issue in issues)
            {
                result.Add(new VisibleIssue(field, issue));
                RecordShowing(showing, field, issue);
            }
        }

        foreach (var (field, issues) in SubmitAdvisoryEntries())
        {
            foreach (var issue in ExceptShadowed(issues, ShowingFor(showing!, field)))
            {
                result.Add(new VisibleIssue(field, issue));
            }
        }

        if (showing is not null)
        {
            foreach (var (field, issues) in LiveEntries())
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

    /// <summary>The defensive gate's form-level issue, synthesized by the channel views whenever
    /// <see cref="GateActive"/> holds: a blocked submit disclosed nothing and nothing on screen
    /// explains the block, so this one model-level explanation stands in for the errors the user
    /// cannot see.</summary>
    private static readonly ValidationIssue GateIssue = new(
        string.Empty,
        "The form cannot be submitted because information that is not currently displayed is invalid.");

    /// <summary>
    /// Whether the defensive gate is showing. The gate is a predicate over source state rather
    /// than a stored entry, which is what makes it impossible for a refresh to delete: it stands,
    /// recomputed on every read, for as long as the submit that armed it stays the last word (a
    /// blocked submit that disclosed no error at all) and the submit-profile answer still carries
    /// errors none of which any surface shows. It dissolves the moment an error reaches the
    /// screen on either channel — one the reveal ledger discloses (which it can do mid-standing,
    /// since a server apply reveals fields), a server-declared one, or one an engaged field's
    /// live verdict carries — or when the answer comes back clean; a later submit re-decides the
    /// arming outright. Only an error dissolves it: a warning does not say why a submit was
    /// refused. The arming half matters: an error that starts failing on a never-revealed field
    /// AFTER a submit that disclosed everything it had raises no gate, because no blocked submit
    /// was ever short an explanation — the field stays quiet until the next submit, exactly as an
    /// undisclosed verdict always does.
    /// </summary>
    private bool GateActive
    {
        get
        {
            if (!_gateArmed || _serverErrors.Count > 0 || _submitVerdictErrors.Count == 0)
            {
                return false;
            }

            foreach (var errorField in _submitVerdictErrors.Keys)
            {
                if (_revealedErrorFields.Contains(errorField))
                {
                    return false;
                }
            }

            // The live channel explains a block just as well as the submit channel does, so it
            // is read here too — through LiveEntries, which applies the one LiveDisclosure
            // policy every live surface answers from, and never through a channel view, since
            // the views synthesize the gate issue from this very predicate. Error severity
            // alone dissolves it: a warning on screen does not say why a submit was refused.
            // Evaluated last because the arming test above is false on virtually every form, so
            // no form walks the engaged set unless a gate is actually standing.
            foreach (var (_, issues) in LiveEntries())
            {
                if (issues.Any(i => i.Severity == ValidationSeverity.Error))
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// The live channel's view for one field: the verdict the last live pass filed for it,
    /// disclosed only while the field is engaged. Under the default
    /// <see cref="LiveIssueDisclosure.Engaged"/> policy registration filters nothing here — an
    /// engaged field's live verdict discloses on every surface whether or not anything currently
    /// renders the field, which is the channel's contract (a field that LEAVES the page leaves
    /// the engaged set with it, which <see cref="HasDeparted"/> decides). The opt-in policy
    /// narrows that through <see cref="LiveViewOf"/>, the one gate every live surface shares.
    /// </summary>
    private List<ValidationIssue>? LiveIssuesFor(FieldIdentifier field) =>
        _engagedFields.Contains(field) && _liveVerdicts.TryGetValue(field, out var live)
            ? LiveViewOf(field, live)
            : null;

    /// <summary>
    /// The live channel's view across fields: every engaged field's filed verdict, each read
    /// through the same <see cref="LiveViewOf"/> gate the single-field view applies.
    /// </summary>
    private IEnumerable<(FieldIdentifier Field, List<ValidationIssue> Issues)> LiveEntries()
    {
        foreach (var (field, issues) in _liveVerdicts)
        {
            if (_engagedFields.Contains(field))
            {
                yield return (field, LiveViewOf(field, issues));
            }
        }
    }

    /// <summary>
    /// Applies <see cref="FormidableOptions.LiveDisclosure"/> to one engaged field's filed
    /// verdict — the single place the policy exists, so every surface that shows the live
    /// channel (the issue reads, the severity scan, the visible-issue collection, and the
    /// message-store projection through them) answers identically by construction. The default
    /// returns the verdict untouched. The opt-in filters per issue on the same override-aware
    /// <see cref="IsVisible"/> the submit channel's reveal consults — per issue because the
    /// override is per issue: one forced visible still shows from a field nothing renders.
    /// Read from the options at every evaluation, since options mutate in place.
    /// </summary>
    private List<ValidationIssue> LiveViewOf(FieldIdentifier field, List<ValidationIssue> filed)
    {
        if (_options.LiveDisclosure == LiveIssueDisclosure.Engaged || filed.Count == 0)
        {
            return filed;
        }

        List<ValidationIssue>? visible = null;
        for (var i = 0; i < filed.Count; i++)
        {
            if (IsVisible(filed[i], field))
            {
                visible?.Add(filed[i]);
            }
            else if (visible is null)
            {
                visible = new List<ValidationIssue>(filed.Count - 1);
                for (var kept = 0; kept < i; kept++)
                {
                    visible.Add(filed[kept]);
                }
            }
        }

        return visible ?? filed;
    }

    /// <summary>
    /// The submit channel's error view for one field: the client's last submit-profile answer
    /// where the reveal ledger discloses it, then the server's errors the client is not already
    /// showing — the same message at the same severity collapses to the client's copy, since the
    /// client merges first — then the synthesized gate issue on the model-level field. Returns
    /// <see langword="null"/> when the channel has nothing for the field, so the absent case and
    /// an answered-clean case read apart.
    /// </summary>
    private List<ValidationIssue>? SubmitErrorsFor(FieldIdentifier field)
    {
        var client = _revealedErrorFields.Contains(field)
            && _submitVerdictErrors.TryGetValue(field, out var revealed)
                ? revealed
                : null;

        List<ValidationIssue>? merged = null;
        if (_serverErrors.TryGetValue(field, out var server))
        {
            foreach (var issue in server)
            {
                if (client is null || !client.Any(i => i.Message == issue.Message && i.Severity == issue.Severity))
                {
                    merged ??= client is null ? [] : [.. client];
                    merged.Add(issue);
                }
            }
        }

        if (field.Equals(ModelLevelField) && GateActive)
        {
            merged ??= client is null ? [] : [.. client];
            merged.Add(GateIssue);
        }

        return merged ?? client;
    }

    /// <summary>
    /// The submit channel's advisory view for one field: the client's last submit-profile
    /// advisories where either reveal ledger discloses the field — an error site keeps a warning
    /// it also picked up, and an advisory site keeps its own — then the server's advisories the
    /// client is not already showing, collapsed on message AND severity, since the channel holds
    /// every non-error severity in one list and the same sentence can exist at two of them.
    /// </summary>
    private List<ValidationIssue>? SubmitAdvisoriesFor(FieldIdentifier field)
    {
        var client = (_revealedErrorFields.Contains(field) || _revealedAdvisoryFields.Contains(field))
            && _submitVerdictAdvisories.TryGetValue(field, out var revealed)
                ? revealed
                : null;

        List<ValidationIssue>? merged = null;
        if (_serverAdvisories.TryGetValue(field, out var server))
        {
            foreach (var issue in server)
            {
                if (client is null || !client.Any(i => i.Message == issue.Message && i.Severity == issue.Severity))
                {
                    merged ??= client is null ? [] : [.. client];
                    merged.Add(issue);
                }
            }
        }

        return merged ?? client;
    }

    /// <summary>
    /// Every field the submit channel's error view has entries for, paired with its merged view:
    /// ledger-revealed fields in the order the report produced them, then fields only the server
    /// speaks for in the order the apply produced them, then the model-level gate when the
    /// predicate holds. <see cref="GetVisibleIssues"/> and <see cref="RebuildStore"/> both walk
    /// this one enumeration, so the two surfaces cannot disagree about what the channel holds.
    /// </summary>
    private IEnumerable<(FieldIdentifier Field, List<ValidationIssue> Issues)> SubmitErrorEntries()
    {
        foreach (var field in _submitVerdictErrors.Keys)
        {
            if (_revealedErrorFields.Contains(field))
            {
                yield return (field, SubmitErrorsFor(field)!);
            }
        }

        foreach (var field in _serverErrors.Keys)
        {
            // A field the first loop already yielded had its server entries merged there.
            if (!(_revealedErrorFields.Contains(field) && _submitVerdictErrors.ContainsKey(field)))
            {
                yield return (field, SubmitErrorsFor(field)!);
            }
        }

        if (GateActive)
        {
            yield return (ModelLevelField, new List<ValidationIssue> { GateIssue });
        }
    }

    /// <summary>
    /// The advisory sibling of <see cref="SubmitErrorEntries"/>: every field the submit channel's
    /// advisory view has entries for, paired with its merged view.
    /// </summary>
    private IEnumerable<(FieldIdentifier Field, List<ValidationIssue> Issues)> SubmitAdvisoryEntries()
    {
        foreach (var field in _submitVerdictAdvisories.Keys)
        {
            if (_revealedErrorFields.Contains(field) || _revealedAdvisoryFields.Contains(field))
            {
                yield return (field, SubmitAdvisoriesFor(field)!);
            }
        }

        foreach (var field in _serverAdvisories.Keys)
        {
            var clientShows = (_revealedErrorFields.Contains(field) || _revealedAdvisoryFields.Contains(field))
                && _submitVerdictAdvisories.ContainsKey(field);
            if (!clientShows)
            {
                yield return (field, SubmitAdvisoriesFor(field)!);
            }
        }
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
    /// Five things follow, in that order. A field that has LEFT the page leaves the engaged
    /// set: the live channel disclosed its verdict only while it was engaged, so disengaging it
    /// takes the issue off every surface at once — the engine's own reads and the message store
    /// alike, since the store is a projection of the same view, rebuilt here when a filed
    /// verdict left with its field. Having left is <see cref="HasDeparted"/>'s question and not
    /// simply being unregistered: a field that never registered at all has not left, and under
    /// <see cref="LiveIssueDisclosure.Engaged"/> keeping its verdict is the bridge contract
    /// itself. A field held by keep-registered has not left either, which is what
    /// lets a virtualized row scroll out of view without losing its messages. A departed field
    /// also stops being one a live pass in flight will answer for: the pass intersects its
    /// begin-time engaged snapshot with the set as it stands at apply, so the verdict it is
    /// about to write cannot re-file the entry the prune just dropped. A field waiting only on
    /// an open <see cref="FormidableOptions.LiveDebounce"/> window — no pass in flight yet —
    /// leaves the same accumulator emptied of it, so the window's own eventual fire finds
    /// nothing left to answer for once every field it opened for has gone; a fire that finds
    /// nothing at all starts no pass. The stored rule verdicts go too: the stamp they are
    /// checked against counts edits, a rendered-field-set move is not one, so a verdict taken
    /// before the move would read as fresh while answering for a page — and, when a collection
    /// row was what left, a model — that no longer exists. Then a refresh is scheduled, whatever
    /// the form's history: it is the one pass that recomputes the submit channel's answer
    /// against the model as it stands, and it is owed twice over — once for that channel, and
    /// once because the emptied store leaves the Valid class's vouch nothing of its own to read.
    /// That channel still speaks only for the revealed-field ledgers, never for what is
    /// rendered, so what a refresh drops is whatever the rules stop producing: a removed row's
    /// entry goes because its rule no longer fires, not because the row left the page — and an
    /// entry for a field a collapsed section took away survives, because the rule still fails.
    /// On a form that has never been submitted the ledgers are empty and the gate unarmed, so
    /// the same pass reports nothing at all: it answers for coverage and
    /// <see cref="IsFormValid"/> alone, and the empty edited set it is armed with scopes its
    /// pending indicator to no field, so nothing on the page so much as flickers.
    /// A model whose CONTENTS changed needs a field-change notification of its own regardless.
    /// Dropping issues is all this can do, and a rule that must START failing because a row left —
    /// a collection that requires at least one entry — produces an issue no pass has computed yet.
    /// </remarks>
    internal void OnRenderedFieldsChanged()
    {
        // Departure alone: DisclosureOverride is not consulted, so an issue an override forces
        // visible still goes once its field departs. The override decides whether an unrendered
        // field's issue may be SHOWN on the submit channel; engagement is the live channel's only
        // predicate, and re-deciding disclosure here — per issue, over a set keyed by field —
        // would suppress live issues the engine otherwise reports. A field that never rendered
        // can be engaged too, since a consumer may notify a change for one, and it is precisely
        // the field this must not drop: it never arrived, so it cannot have left, and every
        // surface the default discloses it on — the store a native ValidationMessage reads above
        // all — is one it reaches without a registration of its own.
        //
        // Collected first: removing from the set while enumerating it throws, and in the common
        // case (a page whose churn is rows arriving, or a virtualized one whose rows stay
        // registered) nothing leaves and there is no list to allocate. The filed live verdict
        // goes with the engagement — the view would hide it anyway, and an unreadable entry is
        // only a leak — and a republish is owed exactly when one was dropped: the store carried
        // that verdict's errors, and dropping one without the other leaves an engine read and an
        // EditContext read disagreeing, the store still offering a message whose field has no
        // element left to focus. A departure that had no verdict filed changed nothing any
        // surface shows, so it publishes nothing — the shape MarkTouched already notifies with,
        // and what keeps a churning page from paying a render round per registration change.
        List<FieldIdentifier>? departed = null;
        foreach (var field in _engagedFields)
        {
            if (HasDeparted(field))
            {
                (departed ??= []).Add(field);
            }
        }

        var republished = false;
        if (departed is not null)
        {
            var republish = false;
            foreach (var field in departed)
            {
                _engagedFields.Remove(field);
                republish |= _liveVerdicts.Remove(field);
            }

            if (republish)
            {
                RebuildStore();
                republished = true;
            }
        }

        // Under the opt-in live-disclosure policy the registered field set is one of the live
        // view's own inputs — a field REGISTERING can disclose a live verdict the store was not
        // projecting, exactly as a departure can retract one — so a field-set change with filed
        // verdicts standing owes a republish in that mode. The default policy never consults
        // registration, which is what keeps the default's churn cost at the departure-only
        // republish above.
        if (!republished
            && _liveVerdicts.Count > 0
            && _options.LiveDisclosure == LiveIssueDisclosure.EngagedAndVisible)
        {
            RebuildStore();
        }

        // One step earlier: a field an open live-debounce window has only accumulated is not yet
        // pending any pass's verdict, only the window's own fire. Left in, that fire would hand
        // it to RunLivePassAsync for a field the window opened for that no longer has anywhere
        // to answer — the pass's own engaged intersect would drop the verdict, but the pass
        // would still have run for nothing. The same departure test as the engagement prune
        // above, and necessarily so: the accumulator holds a live answer owed to an engaged
        // field, so dropping an entry the engaged set keeps would leave that field waiting on a
        // window whose fire no longer speaks for it.
        _pendingDebouncedLiveFields.RemoveWhere(HasDeparted);

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
        // The emptied store answers for nothing, so the coverage read re-derives — or, while the
        // edit stamp says the model it described still stands, holds the answer it last gave.
        _coverageVersion++;

        // Unconditional, because what is owed here is owed by a form at any point in its life.
        // The submit channel needs reconciling only once it has disclosed something, but the
        // coverage the Valid class rests on was just emptied on every form alike, and this is
        // the pass that refills it. Before a submit it is the quietest pass the engine runs: no
        // ledger admits its findings and no field is flagged pending for it, so answering is
        // all it does.
        ScheduleRefresh();
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
                        plan = BuildRulePlan(ruleLevel, profile, kind == PassKind.Submit, editStamp);
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

                // Coverage bookkeeping, after the apply so the submit channel's source is the
                // one this pass just rebuilt. A submit or refresh IS a completed whole-model
                // SubmitProfile evaluation, so its begin stamp and its resolved error fields
                // become the capability-less coverage source — the apply resolved every error,
                // undisclosed ones included, which is exactly what "would fail submit" needs.
                // The coverage version moves for every landing, live passes included: any
                // landing can have written verdicts the coverage read derives from.
                if (kind != PassKind.Live)
                {
                    _lastSubmitAnswerStamp = editStamp;
                    _lastSubmitAnswerErrorFields = [.. _submitVerdictErrors.Keys];
                }

                _coverageVersion++;

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
    /// Decides, rule by rule, what a capability evaluation has left to execute: the profile's
    /// whole selection in declaration order, each rule paired with its fresh stored verdict
    /// where one exists — or with nothing, meaning the caller must run it. A submit sets
    /// <paramref name="executeAll"/> and pairs every rule with nothing by fiat: it is the
    /// disclosure event, and its full run is also what repopulates the store so everything
    /// behind it starts from answered rules; the live and refresh passes, and the validity
    /// probe, all consult freshness. Runs on the dispatcher (the caller marshals), because the
    /// store is read here and mutates only there.
    /// </summary>
    private List<(RuleIdentity Rule, RuleVerdict? Fresh)> BuildRulePlan(
        IRuleLevelValidator<TModel> ruleLevel,
        ValidationProfile profile,
        bool executeAll,
        int editStamp)
    {
        var selection = ruleLevel.SelectRules(profile);
        var plan = new List<(RuleIdentity, RuleVerdict?)>(selection.Count);

        foreach (var rule in selection)
        {
            var fresh = !executeAll
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
        // pass ran under is only knowable by remembering it here. Unset, the live channel runs
        // the submit profile itself — the STORED instance, never a copy of it: the verdict
        // store's freshness check and the submit-coverage cache both key on the profile by
        // reference, and an equal-but-distinct object would silently defeat each of them.
        var liveProfile = _options.LiveProfile ?? _options.SubmitProfile;

        await RunPassAsync(
            PassKind.Live,
            liveProfile,
            CancellationToken.None,
            () => new HashSet<FieldIdentifier>(triggeringFields),
            report =>
            {
                // Every engaged field, not just the fields that triggered this pass: each live
                // pass answers the whole model under the same resolved profile — executing what
                // is stale, assembling the rest from the store — so this report is a
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
                        _liveVerdicts[field] = byField.TryGetValue(field, out var forField) ? forField : [];
                    }
                }
            }).ConfigureAwait(false);
    }

    /// <summary>
    /// The whole-form validity probe behind <see cref="FormidableOptions.TrackFormValidity"/>: a
    /// standalone <see cref="FormidableOptions.SubmitProfile"/> evaluation, not an engine pass —
    /// it never calls <see cref="BeginPass"/>, discloses nothing, and never touches the pending
    /// indicator. On a rule-capable validator the probe reads and feeds the verdict store: only
    /// the submit-selected rules with no fresh verdict at the probe's begin stamp execute — per
    /// rule, under <c>_probeCts</c> — and what ran lands back into the store on the dispatcher,
    /// generation-gated exactly as a pass's store write is, and skipped outright when an edit
    /// has arrived since the probe began: verdicts stamped for a model state that is no longer
    /// current would never be served, and writing them could only displace fresher entries a
    /// pass landed meanwhile. A probe whose every selected rule is already answered executes
    /// nothing and is a pure read. Any other validator gets the whole profile in one call —
    /// correct, unoptimised — and its completed report is recorded as the capability-less
    /// coverage source under the same begin stamp. Either way, a landing that moved a coverage
    /// source publishes one notification round, engine and EditContext both: the coverage is
    /// what the Valid state class reads, so the landing can change a rendered class with no
    /// pass anywhere to publish for it.
    /// <see cref="IsFormValid"/> itself is written only when the computed value differs from the
    /// current one (a flip, not every probe) and only while <c>stamp</c> is still the most
    /// recently taken one — a probe a newer probe has already superseded discards its own answer
    /// rather than overwrite a fresher one, the same last-write-wins discipline every pass
    /// verdict already follows via <c>_version</c>. A submit orders itself ahead of every probe
    /// the same way: its own verdict apply calls <see cref="AdoptFormValidity"/>, which bumps
    /// this same stamp, so a probe that started before the submit began cannot land after it and
    /// overwrite its answer — see <see cref="AdoptFormValidity"/> for why submit (and refresh)
    /// can adopt directly instead of merely invalidating. A probe never starts while a submit is
    /// already in flight, for the same reason a live pass never does (see
    /// <see cref="RunLivePassAsync"/>): submit is about to compute this exact quantity itself
    /// moments from now, so racing it buys nothing.
    /// A probe that faults reports the only way a fire-and-forget evaluation can: through
    /// <see cref="ValidationFaulted"/>, exactly as a live or refresh pass's own fault does — never
    /// a form-level fault issue, which would disclose something an invisible probe promises never
    /// to. Without this, a validator that throws on the submit profile — which a narrowed
    /// <see cref="FormidableOptions.LiveProfile"/> can keep a live pass from ever selecting, so
    /// the two can genuinely disagree on whether a rule throws — would freeze
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

        // Captured synchronously, mirroring a pass's own begin: the edit stamp names the model
        // state this probe's answer — and any verdicts it lands — speaks for, the generation
        // names the rendered field set they were computed against, and the profile is
        // remembered because the options holding it are settable.
        var editStamp = _editStamp;
        var generation = _storeGeneration;
        var profile = _options.SubmitProfile;

        var ruleLevel = _validator as IRuleLevelValidator<TModel>;
        var ruleCapable = ruleLevel is not null && ruleLevel.CanValidateByRule;

        ValidationReport report;
        Dictionary<RuleIdentity, RuleVerdict>? executed = null;
        try
        {
            if (ruleCapable)
            {
                // The same dispatch discipline the pass skeleton uses: the store is read on the
                // dispatcher, where it mutates. A selection error surfaces through the fault
                // policy below, exactly as a whole-profile validation would surface it.
                List<(RuleIdentity Rule, RuleVerdict? Fresh)> plan = null!;
                await _renderDispatch(() =>
                {
                    plan = BuildRulePlan(ruleLevel!, profile, executeAll: false, editStamp);
                    return Task.CompletedTask;
                }).ConfigureAwait(false);

                (report, executed) = await ExecuteRulePlanAsync(ruleLevel!, profile, plan, editStamp, _probeCts.Token)
                    .ConfigureAwait(false);
            }
            else
            {
                report = await _validator.ValidateAsync(_model, profile, _probeCts.Token)
                    .ConfigureAwait(false);
            }
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
            if (_disposed)
            {
                return Task.CompletedTask;
            }

            var movedCoverage = false;

            if (executed is { Count: > 0 } && generation == _storeGeneration && editStamp == _editStamp)
            {
                foreach (var (rule, verdict) in executed)
                {
                    _ruleVerdicts[rule] = verdict;
                }

                movedCoverage = true;
            }

            if (stamp == _formValidityStamp)
            {
                if (!ruleCapable)
                {
                    // The completed whole-profile answer is the capability-less coverage
                    // source; its errors resolve to fields here, once per probe, so the
                    // per-field coverage read never resolves per render. Deliberately no
                    // editStamp guard on this record, unlike the store write above: a record
                    // whose stamp trails the edit counter can never compare fresh, and any
                    // fresher writer bumped _formValidityStamp first — the gate this branch
                    // already sits behind.
                    var errorFields = new HashSet<FieldIdentifier>();
                    foreach (var issue in report.Errors)
                    {
                        errorFields.Add(Resolve(issue));
                    }

                    _lastSubmitAnswerStamp = editStamp;
                    _lastSubmitAnswerErrorFields = errorFields;
                    movedCoverage = true;
                }

                var isFormValid = report.IsValid;
                if (isFormValid != IsFormValid)
                {
                    IsFormValid = isFormValid;
                    NotifyStateChanged();
                }
            }

            if (movedCoverage)
            {
                _coverageVersion++;
                NotifyStateChanged();
                EditContext.NotifyValidationStateChanged();
            }

            return Task.CompletedTask;
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Adopts a whole-model <see cref="FormidableOptions.SubmitProfile"/> report's validity
    /// directly into <see cref="IsFormValid"/> — called from the submit and refresh verdict
    /// applies, both of which already compute exactly this quantity as part of their own pass, so
    /// there is nothing left for a separate probe to add. On a rule-capable validator the
    /// adopted report is assembled from the verdict store the pass just repopulated, so this IS
    /// the store read landing: every submit-selected rule is fresh at the pass's stamp the
    /// moment this runs, and the value written is what those verdicts say. Bumps
    /// <c>_formValidityStamp</c>
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
    /// Whether the field has LEFT the page: something registered it once, and nothing does now.
    /// The engagement lifetime's own test, and deliberately not <see cref="IsRendered"/>'s
    /// negation — a field nothing has ever registered has not departed, it has never arrived,
    /// and under <see cref="LiveIssueDisclosure.Engaged"/> its live verdict is exactly what the
    /// bridge contract keeps: an anchor-free native input registers nothing at any point, so a
    /// prune that read the two states as one would retract its error the first time anything
    /// ELSE on the page registered or unregistered. Disclosure asks a different question and goes
    /// on asking <see cref="IsVisible"/>: not whether the field ever arrived, but whether the page
    /// is showing somewhere its issue could be read right now.
    /// </summary>
    private bool HasDeparted(FieldIdentifier field) =>
        Registry.HasEverRegistered(field) && !IsRendered(field);

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
        IEnumerable<ValidationIssue> issues)
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

    /// <summary>
    /// Rebuilds the <see cref="ValidationMessageStore"/> as a materialized projection of the
    /// channel views and publishes the result. The EditContext API takes writes, so this runs at
    /// every publish point — a pass's verdict apply, a server apply, a departure that dropped a
    /// filed live verdict — and the store between rebuilds is exactly what the views said the
    /// last time a source moved.
    /// </summary>
    private void RebuildStore()
    {
        _store.Clear();

        if (_faultIssue is not null)
        {
            _store.Add(ModelLevelField, _faultIssue.Message);
        }

        foreach (var (field, issues) in SubmitErrorEntries())
        {
            foreach (var issue in issues.Where(i => i.Severity == ValidationSeverity.Error))
            {
                _store.Add(field, issue.Message);
            }
        }

        foreach (var (field, issues) in LiveEntries())
        {
            // Deliberately not the shadow rule the issue reads share: the against-list here is this
            // field's submit issues alone and never grows, so two live errors carrying the same
            // message both reach the store, where ExceptShadowed would collapse them to one. The
            // store is the interop surface a native ValidationMessage/ValidationSummary renders
            // straight out, so narrowing the merge here would change what those components show
            // rather than what an engine read returns.
            var existing = SubmitErrorsFor(field);
            foreach (var issue in issues.Where(i => i.Severity == ValidationSeverity.Error))
            {
                if (existing is null || !existing.Any(s => s.Message == issue.Message))
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
                _liveVerdicts.Clear();

                // The server verdict is a snapshot of one round trip, and this pass is a newer
                // whole-model answer: whatever the server said is superseded, both branches
                // alike. A client rule that fails the same way keeps its message showing through
                // the client's own answer.
                _serverErrors.Clear();
                _serverAdvisories.Clear();

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
                    // error to pair them with. A successful submit is also the one event that
                    // resets the reveal ledgers: errors un-reveal wholesale, advisories re-freeze
                    // to the fresh sites.
                    canProceed = true;
                    _submitVerdictErrors = [];
                    _revealedErrorFields.Clear();
                    _gateArmed = false;

                    _submitVerdictAdvisories = ResolveVisibleAdvisories(report);
                    _revealedAdvisoryFields.Clear();
                    _revealedAdvisoryFields.UnionWith(_submitVerdictAdvisories.Keys);
                }
                else
                {
                    var resolvedErrors = report.Errors
                        .Select(issue => (Issue: issue, Field: Resolve(issue)))
                        .ToList();

                    // Reveal is field-granular, and the ledger unions first so everything below
                    // answers from the state the views will actually read. An issue whose own
                    // visibility answer is yes — registration, or an override saying so —
                    // reveals its FIELD; a field an earlier blocked submit or a server apply
                    // revealed is watched already. A revealed field's errors then disclose
                    // whole: the view carries no per-issue filter, so an issue an override
                    // answered no for still shows beside the sibling that revealed their shared
                    // field. The override's authority is over revealing, not over filtering a
                    // revealed field's answer.
                    _revealedErrorFields.UnionWith(
                        resolvedErrors.Where(x => IsVisible(x.Issue, x.Field)).Select(x => x.Field));

                    var disclosed = resolvedErrors
                        .Where(x => _revealedErrorFields.Contains(x.Field))
                        .ToList();

                    // Suppressed means the views will not show it — the same post-union ledger
                    // read they answer from, so the diagnostic can never name an issue that is
                    // in fact on screen. Once per suppressed issue per submit, as ever.
                    foreach (var suppressed in resolvedErrors
                        .Where(x => !_revealedErrorFields.Contains(x.Field)))
                    {
                        ReportSuppressed(suppressed.Issue);
                    }

                    // The full resolved answer is the channel's source; the ledger is what the
                    // view reads it through. Arming follows the disclosure count: a blocked
                    // submit that disclosed nothing is the case the defensive gate explains, and
                    // the views synthesize its form-level issue for as long as GateActive holds —
                    // there is no entry to write, so there is no entry a refresh can delete.
                    _submitVerdictErrors = resolvedErrors
                        .GroupBy(x => x.Field, x => x.Issue)
                        .ToDictionary(g => g.Key, g => g.ToList());
                    _gateArmed = disclosed.Count == 0;

                    // Advisory sites are not necessarily error sites: a visible field can carry a
                    // warning while passing every error rule. The advisory ledger unions the same
                    // way the error ledger does, so a field once shown a warning keeps that
                    // warning refreshed across later submits that found it momentarily clean.
                    _submitVerdictAdvisories = ResolveVisibleAdvisories(report);
                    _revealedAdvisoryFields.UnionWith(_submitVerdictAdvisories.Keys);

                    summary = disclosed.Count > 0
                        ? disclosed
                            .Select(x => x.Issue.DisplayName ?? x.Issue.Path)
                            .Select(name => name.Length == 0 ? "This form" : name)
                            .Distinct()
                            .ToList()
                        : ["This form"]; // the gate's own model-level entry is what the summary points at
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

        // The payload is the server's CURRENT verdict, not an addition to its last one: replacing
        // it is swapping the server source wholesale. The client's own answer lives in its own
        // source and is untouched — an identical client message keeps showing through the client
        // view, because the views merge client-first and drop the server's same-text copy.
        _serverErrors.Clear();
        _serverAdvisories.Clear();

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
                // is not shown, the diagnostic names the registration that would have shown it —
                // once per suppressed issue per apply, since a suppressed advisory is never
                // stored — and no defensive gate stands in for it either: that gate exists
                // because a hidden error would otherwise fail a submit silently, and an advisory
                // fails nothing.
                ReportSuppressed(issue);
                continue;
            }

            // A payload can carry the same sentence twice at the same severity; a reader has no
            // use for it twice, so the second copy folds into the first exactly as the views fold
            // a server copy into a client one.
            var channel = issue.Severity == ValidationSeverity.Error ? _serverErrors : _serverAdvisories;
            if (!channel.TryGetValue(field, out var forField))
            {
                channel[field] = forField = [];
            }

            if (!forField.Any(i => i.Message == issue.Message && i.Severity == issue.Severity))
            {
                forField.Add(issue);
            }

            // A server apply is a disclosure event: reveal state never un-reveals a field, so the
            // ledger unions the field in and the views watch it from here on — the client's own
            // last answer for it included. The two sets are separate because the two channels
            // reveal independently: a field can be an advisory site without ever having been an
            // error site, and keeps its advisory refreshed either way.
            (issue.Severity == ValidationSeverity.Error ? _revealedErrorFields : _revealedAdvisoryFields)
                .Add(field);
        }

        RebuildStore();
    }

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

                // Landing the verdict is the whole apply: the fresh whole-model answer replaces
                // the submit channel's source, and the server source clears — the server's answer
                // was a snapshot of one round trip, and this pass supersedes it (a client rule
                // failing the same way keeps the message showing through the client view). What
                // the channel SHOWS is the views' business: the reveal ledgers decide which of
                // these entries surface — fixed fields clear because the rules stopped producing
                // them, fields revealed after submit stay quiet until the next submit because no
                // ledger watches them — and the gate needs nothing here to survive, because it
                // was never an entry a rebuild could drop.
                _serverErrors.Clear();
                _serverAdvisories.Clear();
                _submitVerdictErrors = GroupByResolvedField(report.Errors);
                _submitVerdictAdvisories = GroupByResolvedField(
                    report.Issues.Where(i => i.Severity != ValidationSeverity.Error));
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
