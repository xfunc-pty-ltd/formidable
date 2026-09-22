using Formidable.Introspection;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Logging;

namespace Formidable.Blazor;

/// <summary>The validation engine behind one form: it runs the passes, answers every field and issue read from its sources, and keeps the <see cref="EditContext"/>'s message store current.</summary>
/// <typeparam name="TModel">The form's model type.</typeparam>
/// <remarks>
/// <para>
/// The sources: the live channel's per-field verdicts, the engaged set, the last submit-profile
/// answer, the two reveal ledgers, the server-issue store, the gate latch, the fault issue and
/// <see cref="HasSubmitted"/>, with the per-set verdict store and the submit-coverage vouch
/// beside them. Every issue read computes from them; the message store is a projection rebuilt
/// whenever a source moves.
/// </para>
/// <para>
/// The passes: live, submit, refresh and load; the <see cref="FormidableOptions.TrackFormValidity"/>
/// probe is not one. Their order, supersession and deferral are the engine page's subject
/// (<c>docs/how-the-engine-works.md</c>), which the members cite by section.
/// </para>
/// </remarks>
public sealed class FormidableEngine<TModel> : IFormidableEngine, IValidatingFieldReader, IStaleRegistrationReporter, IDisposable
    where TModel : class
{
    // Thread discipline, in one place. Every source above mutates on the renderer's dispatcher
    // and only from the still-current pass, except that ApplyServerIssues writes synchronously
    // on the calling thread, which is why it and ValidateForSubmitAsync ask to be called from
    // the renderer's synchronization context. Pass bookkeeping (_version, _passCts,
    // _currentPass, _touched, _engagedFields, _pendingRefreshFields,
    // _pendingDebouncedLiveFields) mutates synchronously on the caller's context, with these
    // exceptions on the dispatcher: _pendingRefreshFields when a refresh snapshots and clears
    // it at begin; _engagedFields when a rendered-field-set change prunes departed fields (a
    // live pass snapshots the set at begin and intersects at apply, but no pass removes an
    // entry, and a submit leaves the set standing); _touched and _engagedFields when the load's
    // apply adopts fields; _pendingDebouncedLiveFields when the live timer fires and when a
    // field-set change prunes it; and _currentPass, which the pass that recorded it clears
    // beside IsValidating.
    //
    // IsFormValid has its own gate: _formValidityStamp moves synchronously on the caller's
    // context as a probe starts, and IsFormValid is written on the dispatcher only while that
    // stamp is still current, the last-write-wins shape _version gives the sources. The probe is
    // not a pass and never touches the pass bookkeeping. _editStamp moves synchronously on the
    // caller's context as each field change arrives and as DiscloseLoadedValuesAsync begins, and
    // reaches the verdict store only as an argument; the store is planned against, filed into
    // and cleared only on the dispatcher, its writes generation-gated inside TryFile with the
    // version gate staying in the pass's dispatch here. On the coverage tracker, reading is the
    // mutation (a render-path GetFieldState ask recomputes its cached answer in place) and it
    // carries no locking or dispatch of its own; its two edit-shaped writes (NoteEdit on a field
    // change, Abandon at the load's opening) ride the caller's context beside the stamp moves
    // they belong to, and every other write happens on the dispatcher.
    //
    // An await calls ConfigureAwait(false) exactly when its continuation is off the dispatcher
    // and needs neither the renderer's context nor the state above. Every await in this file and
    // both of core's satisfies that, re-entering the dispatcher through _renderDispatch wherever
    // the continuation goes on to touch that state, except the one at the tail of
    // DiscloseLoadedValuesAsync, which already sits inside a _renderDispatch delegate and stays
    // on the context that delegate re-entered. The kit components and the JS-backed services keep
    // the renderer's context through their own awaits, because those continuations run in or
    // beside component code that reads it.
    private readonly TModel _model;
    private readonly IModelValidator<TModel> _validator;
    private readonly IModelIntrospector _introspector;
    private readonly FormidableOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly Func<Func<Task>, Task> _renderDispatch;
    private readonly ILogger? _logger;
    private readonly ValidationMessageStore _store;

    // The live channel's verdict source: each engaged field's answer from the live pass that
    // most recently filed one — an empty list is a real answer (the field's rules passed), a
    // missing entry means no pass has answered the field yet. The live VIEW is this dictionary
    // read through the engaged set; nothing else ever filters it (see LiveIssuesFor).
    private readonly Dictionary<FieldIdentifier, List<ValidationIssue>> _liveVerdicts = [];

    private readonly HashSet<FieldIdentifier> _touched = [];

    // The fields the live channel answers for, and that are still on the page — fed by
    // HandleFieldChanged for a committed change and by AdoptLoadedValues for a value a load
    // decided for, pruned by OnRenderedFieldsChanged, never cleared by any pass. A live
    // pass's verdict answers exactly this set (snapshotted as the pass begins), which is what
    // lets a cross-field verdict clear, or appear, on a field the triggering edit never named.
    // Distinct from _touched: touched gates CSS state classes, engagement gates the live
    // channel — verdict application at a pass's apply, and disclosure at every read, since the
    // live view answers only for engaged fields.
    private readonly HashSet<FieldIdentifier> _engagedFields = [];

    private readonly HashSet<FieldIdentifier> _pendingRefreshFields = [];
    private readonly HashSet<FieldIdentifier> _pendingDebouncedLiveFields = [];

    // The submit channel's client verdict source: the last whole-model answer a submit, a refresh,
    // a load, or a live pass that ran the submit profile itself produced, resolved to fields —
    // every error and every advisory, undisclosed ones included.
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
    // that returns after being fixed rediscloses at the next pass to answer the submit profile,
    // with no further submit. Only a successful submit resets them (errors un-reveal wholesale;
    // advisories re-freeze to the fresh sites). Two sets because the two channels reveal
    // independently: a field can be an advisory site without ever having been an error site.
    private readonly HashSet<FieldIdentifier> _revealedErrorFields = [];
    private readonly HashSet<FieldIdentifier> _revealedAdvisoryFields = [];

    // The server verdict source: what the most recent ApplyServerIssues call put on screen, per
    // field, per severity channel. An apply replaces it wholesale — the payload is the server's
    // CURRENT verdict, not an addition to its last one — and every submit, refresh and load
    // clears it:
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

    /// <summary>The empty list every issue read returns for a field with nothing to say.</summary>
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
    // can see unaided — the rendered field set moving — and DiscloseLoadedValuesAsync moves
    // this counter itself, because a page calling it is saying outright that the model now
    // holds values nothing notified for. Nothing covers the rest.
    private int _editStamp;

    // The per-rule verdict store — every rule's most recent answer, held per executed set. The
    // store doctrine lives on the type itself; the engine keeps the caller-side halves: every
    // touch rides the renderer's dispatcher, and a pass's apply gates on _version before
    // filing. Not _store: that name is the ValidationMessageStore's.
    private readonly SetVerdictStore _verdictStore = new();

    // The submit-coverage vouch behind the Valid state class, held whole on one type: the derived
    // answer, its cache, the held answer and the capability-less source live on the tracker,
    // whose own doc carries the doctrine. The engine keeps the seams the tracker deliberately
    // never holds — the scheduler's on-its-way answer (ReAnswerOnItsWay) and issue
    // resolution — and hands the per-ask coordinates in at WouldPassSubmit.
    private readonly SubmitCoverageTracker _submitCoverage;

    // The rule-selection half of the submit-coverage read, built once from the validator the
    // engine holds: the method group cannot change, where a lambda at the ask site would
    // allocate per read — and the read runs per rendered field per render. Whether the
    // selection is OFFERED to the tracker stays a per-ask capability test in WouldPassSubmit,
    // so this field spares an allocation without freezing an answer.
    private readonly Func<ValidationProfile, IReadOnlyList<RuleIdentity>>? _selectSubmitRules;

    // The submit profile's presence demands, resolved to fields (see Requirements). Null until
    // first asked, and dropped whenever the profile instance or the rendered field set moves.
    private Dictionary<FieldIdentifier, FieldRequirement>? _requirements;
    private ValidationProfile? _requirementsProfile;

    // The pass in flight, or null when none is. One descriptor rather than a flag per kind so it
    // cannot go stale: every pass records itself here as it begins, a newer pass overwrites that
    // record outright, and only a pass that is still the current one ever clears it. A pass that
    // was superseded and therefore declined to write state — it is no longer current, so it must
    // not — cannot leave the engine deferring to a pass that ended long ago.
    private PassScope? _currentPass;

    /// <summary>Creates the engine for one model and edit context, with the system clock, inline dispatch and no logger for whichever of those is omitted.</summary>
    /// <param name="model">The model the form edits.</param>
    /// <param name="editContext">The edit context whose field-changed notifications drive the live channel and whose message store the engine writes.</param>
    /// <param name="validator">The validator every pass runs; its optional seams are probed at each use.</param>
    /// <param name="introspector">Resolves an issue's or a rule's path to the object and member it names.</param>
    /// <param name="options">The options, read at each use rather than copied.</param>
    /// <param name="timeProvider">The clock behind the two debounce timers and the held-vouch bound; <see cref="TimeProvider.System"/> when omitted.</param>
    /// <param name="renderDispatch">Runs a delegate on the renderer's dispatcher; the delegate runs inline when omitted.</param>
    /// <param name="logger">Receives the diagnostics the engine also writes to Trace; none when omitted, and the Trace lines and option callbacks still fire.</param>
    /// <exception cref="ArgumentNullException"><paramref name="model"/>, <paramref name="editContext"/>, <paramref name="validator"/>, <paramref name="introspector"/> or <paramref name="options"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// <see cref="FormidableForm{TModel}"/> and <see cref="FormidableValidator{TModel}"/> are the
    /// only supported hosts. An engine built directly, as a test does, never hears of a move in
    /// the rendered field set, because only a root calls <see cref="OnRenderedFieldsChanged"/>
    /// and <see cref="SetFieldOrder"/>: a departed field keeps its live verdict, the stored
    /// verdicts are never dropped, and issues list in validator order. With
    /// <see cref="FormidableOptions.TrackFormValidity"/> on, construction runs the first probe.
    /// </remarks>
    public FormidableEngine(
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

        // Asked before the edit context is subscribed to or given a class provider, so a
        // consumer capability tester that throws cannot leave this context wired to a
        // half-built engine. The report reads nothing but the validator and the logger.
        if (_validator is not IRuleInspectingValidator<TModel> { CanInspectRules: true })
        {
            ReportInspectionUnavailable();
        }

        _selectSubmitRules = validator is IRuleLevelValidator<TModel> ruleLevel
            ? ruleLevel.SelectRules
            : null;
        _submitCoverage = new SubmitCoverageTracker(_verdictStore, ReAnswerOnItsWay, Resolve);

        _store = new ValidationMessageStore(editContext);
        editContext.OnFieldChanged += HandleFieldChanged;
        editContext.SetFieldCssClassProvider(new FormidableFieldCssClassProvider(this));
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
    public event EventHandler<FormidableStateChangedEventArgs>? StateChanged;

    /// <inheritdoc />
    public event EventHandler<FormidableValidationFaultedEventArgs>? ValidationFaulted;

    /// <inheritdoc />
    /// <param name="field">The field.</param>
    /// <returns>The field's state, its would-pass-submit flag read from the coverage vouch through <see cref="WouldPassSubmit"/>.</returns>
    public FieldState GetFieldState(FieldIdentifier field)
    {
        var (hasErrors, hasWarnings, hasInfos) = ScanFieldSeverities(field);

        return new FieldState
        {
            IsTouched = _touched.Contains(field),
            IsModified = EditContext.IsModified(field),
            IsValidating = IsFieldValidating(field),
            HasErrors = hasErrors,
            HasWarnings = hasWarnings,
            HasInfos = hasInfos,
            WouldPassSubmit = WouldPassSubmit(field)
        };
    }

    /// <inheritdoc />
    /// <param name="field">The field.</param>
    /// <returns><see cref="FormidableOptions.RequiredOverride"/>'s answer when it gives one, else the field's entry in <see cref="Requirements"/>, else <see cref="FieldRequirement.NotRequired"/>.</returns>
    public FieldRequirement GetFieldRequirement(FieldIdentifier field)
    {
        // The override is asked first and on every read: it is a declaration a consumer makes
        // about their own form, so it outranks whatever the rules can be read to say, and it is
        // the only part of this answer that can change without the validator or the profile
        // changing — caching it would freeze whatever it happened to say on the first render.
        if (_options.RequiredOverride?.Invoke(field) is { } declared)
        {
            return declared;
        }

        return Requirements().TryGetValue(field, out var requirement)
            ? requirement
            : FieldRequirement.NotRequired;
    }

    /// <summary>The submit profile's presence demands resolved to fields, built on first ask and kept until the profile instance changes or the rendered field set moves.</summary>
    /// <returns>Each demanding field and its requirement; a lookup miss means <see cref="FieldRequirement.NotRequired"/>, and a validator that cannot be inspected or a selection that throws yields an empty map.</returns>
    // Built once for the form rather than once per field per render: every bound component asks
    // on every render, and each ask of the validator walks its declared rules. Keys resolve
    // through the introspector that puts an issue on a field, so a demand lands on exactly the
    // field the failure it describes lands on, which is the point of resolving rather than
    // comparing names. The map reads the model graph, so a rendered-field-set move drops it for
    // the reason the verdict store empties: what is on the page can change the objects behind
    // it with no field-changed notification anywhere. Only the map is kept current by that: a
    // component asks with the identifier it resolved at bind, so a nested member re-filed under
    // a replaced owner leaves an unmoved component asking under the old one, the limit
    // IFormidableEngine.GetFieldRequirement states, erring towards claiming nothing. A selection
    // that throws (a mistyped ruleset name) answers "nothing found" rather than taking the render
    // down, the choice SubmitCoverageTracker makes at its own walk: the next pass surfaces the
    // exception through its fault policy, where a configuration error belongs.
    private Dictionary<FieldIdentifier, FieldRequirement> Requirements()
    {
        var profile = _options.SubmitProfile;
        if (_requirements is not null && ReferenceEquals(_requirementsProfile, profile))
        {
            return _requirements;
        }

        var map = new Dictionary<FieldIdentifier, FieldRequirement>();
        if (_validator is IRuleInspectingValidator<TModel> inspector && inspector.CanInspectRules)
        {
            try
            {
                var expanded = new List<string>();
                foreach (var path in inspector.GetDeclaredFieldPaths(profile))
                {
                    var requirement = inspector.GetFieldRequirement(path, profile);
                    if (requirement == FieldRequirement.NotRequired)
                    {
                        continue;
                    }

                    // A templated path (Attendees[].Name) names a shape, and the fields that
                    // shape demands are the rows the model holds when this map is derived — so
                    // it is expanded against the model, one entry per row, through the same
                    // expansion the load's green enumeration uses; a scalar path passes through
                    // it unchanged. The inspector is asked once per DECLARED path, above,
                    // because a requirement is a property of the template — a condition
                    // attaches to the declaration, so no row can carry a different answer —
                    // and per-row work stays expansion plus resolution, on a map that rebuilds
                    // on every rendered-field-set move, which a virtualized panel delivers in
                    // scroll bursts. Row churn keeps the entries current by the same signal
                    // that drops them: a row's fields registering or unregistering is exactly
                    // such a move.
                    expanded.Clear();
                    ExpandTemplate(path, expanded);
                    foreach (var rowPath in expanded)
                    {
                        map[ResolvePath(rowPath)] = requirement;
                    }
                }
            }
            catch (Exception)
            {
                map.Clear(); // selection failed: nothing can be read, so nothing is claimed
            }
        }

        _requirementsProfile = profile;
        return _requirements = map;
    }

    /// <summary>Asks the coverage tracker whether <paramref name="field"/> would pass submit, with the edit stamp, the submit profile and the rule selection read at this ask.</summary>
    /// <param name="field">The field.</param>
    /// <returns><see langword="true"/> when the engine can vouch for the field, on the terms <see cref="SubmitCoverageTracker.WouldPassSubmit"/> states.</returns>
    // The selection is offered only while CanValidateByRule holds, and both it and the profile
    // are read at each ask: the capability is a live read (a cascade-mode flip changes it with no
    // engine event anywhere) and the options are read at each use. Freezing either at
    // construction would leave the vouch answering for a configuration the form no longer runs.
    private bool WouldPassSubmit(FieldIdentifier field) =>
        _submitCoverage.WouldPassSubmit(
            field,
            _editStamp,
            _options.SubmitProfile,
            _validator is IRuleLevelValidator<TModel> ruleLevel && ruleLevel.CanValidateByRule
                ? _selectSubmitRules
                : null);

    /// <summary>The profile the live channel runs: <see cref="FormidableOptions.LiveProfile"/> when set, otherwise the submit profile instance itself.</summary>
    // The stored instance and never a copy: the verdict store's freshness check and the coverage
    // cache both key on the profile by reference, and an equal-but-distinct object would defeat
    // each of them. Resolved at each ask because LiveProfile holds a mutable instance a consumer
    // may swap at any moment.
    private ValidationProfile ResolvedLiveProfile => _options.LiveProfile ?? _options.SubmitProfile;

    /// <summary>Whether a pass in flight or an armed timer is demonstrably about to re-answer the submit-selected rules, which is what lets the held vouch serve across an edit.</summary>
    /// <returns><see langword="true"/> for a pass younger than <see cref="SubmitCoverageTracker.HeldVouchBound"/> whose landing answers the submit selection, or for a refresh armed by an edit or a submit-running live window, each behind a debounce that can fire; a pass past the bound answers <see langword="false"/> whatever is armed behind it.</returns>
    // The options are read here at each ask, per their read-at-each-use contract. The probe is
    // deliberately not consulted: it is fire-and-forget, with no descriptor to read a start time
    // from. The serve condition itself, and the doctrine behind the age test, the narrowed-live
    // fall-through and the arms, is on the engine page under the submit-coverage vouch.
    private bool ReAnswerOnItsWay()
    {
        var liveAnswersSubmit = ReferenceEquals(ResolvedLiveProfile, _options.SubmitProfile);

        if (_currentPass is { } pass)
        {
            // A pass past the bound stops vouching whatever is scheduled behind it: a hung
            // submit, live or load pass defers every armed refresh indefinitely
            // (RunRefreshPassAsync re-arms behind exactly those kinds), and a hung refresh is
            // displaced by the next fire rather than deferred to, beginning a pass with a bound
            // of its own — so at this read the arms below cannot stand in for the pass past it.
            if (_timeProvider.GetElapsedTime(pass.StartedAt)
                >= SubmitCoverageTracker.HeldVouchBound)
            {
                return false;
            }

            // A pass whose landing answers the submit selection is itself the re-answer.
            if (pass.Kind != PassKind.Live || liveAnswersSubmit)
            {
                return true;
            }

            // A narrowed live pass cannot answer the submit selection, but a submit-profile
            // refresh armed behind it will, the moment this pass ends — deferred, not absent.
            // Fall through to the arms. Only the refresh arm can answer there: the window arm
            // carries the same profile conjunct this branch just failed, so however many fields
            // an edit during this flight puts back in the window, it promises another narrowed
            // pass and counts for nothing.
        }

        return (_pendingDebouncedLiveFields.Count > 0
                && liveAnswersSubmit
                && _options.LiveDebounce is { } liveDebounce
                && liveDebounce != Timeout.InfiniteTimeSpan)
            || (_pendingRefreshFields.Count > 0
                && _options.RefreshDebounce != Timeout.InfiniteTimeSpan);
    }

    /// <summary>Whether the issues showing for <paramref name="field"/> include an error, a warning and an info, read across the live and submit views in one walk.</summary>
    /// <param name="field">The field.</param>
    /// <returns>The three flags; the walk stops once all three are set.</returns>
    // GetFieldState and IValidatingFieldReader.FieldAdvisories both answer from this one walk
    // rather than each re-reading the views their own way.
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

    /// <summary>Whether the pass in flight covers <paramref name="field"/>, which the pass's kind decides through its pending scope.</summary>
    /// <param name="field">The field.</param>
    /// <returns><see langword="true"/> for any field under a submit, a triggering field under a live pass, a field edited within the window under a refresh, and never under a load.</returns>
    // GetFieldState folds this into its own read; IValidatingFieldReader exposes it alone for the
    // css class provider, which wants only this without the cost of a whole FieldState.
    private bool IsFieldValidating(FieldIdentifier field) =>
        IsValidating && (_validatingScope is null || _validatingScope.Contains(field));

    /// <inheritdoc cref="IValidatingFieldReader.IsFieldValidating"/>
    /// <param name="field">The field.</param>
    /// <returns>What <see cref="IsFieldValidating"/> answers.</returns>
    bool IValidatingFieldReader.IsFieldValidating(FieldIdentifier field) => IsFieldValidating(field);

    /// <inheritdoc cref="IValidatingFieldReader.IsFieldTouched"/>
    /// <param name="field">The field.</param>
    /// <returns><see langword="true"/> once <see cref="MarkTouched"/> or a load has marked it.</returns>
    bool IValidatingFieldReader.IsFieldTouched(FieldIdentifier field) => _touched.Contains(field);

    /// <inheritdoc cref="IValidatingFieldReader.FieldAdvisories"/>
    /// <param name="field">The field.</param>
    /// <returns>The two flags from <see cref="ScanFieldSeverities"/>, the error flag dropped.</returns>
    (bool HasWarnings, bool HasInfos) IValidatingFieldReader.FieldAdvisories(FieldIdentifier field)
    {
        var (_, hasWarnings, hasInfos) = ScanFieldSeverities(field);
        return (hasWarnings, hasInfos);
    }

    /// <inheritdoc cref="IValidatingFieldReader.WouldPassSubmit"/>
    /// <param name="field">The field.</param>
    /// <returns>What <see cref="WouldPassSubmit"/> answers.</returns>
    bool IValidatingFieldReader.WouldPassSubmit(FieldIdentifier field) => WouldPassSubmit(field);

    /// <inheritdoc cref="IValidatingFieldReader.InlineMessageLive"/>
    string? IValidatingFieldReader.InlineMessageLive => _options.InlineMessageLive;

    /// <inheritdoc />
    /// <param name="field">The field.</param>
    /// <returns>The field's issues in the order <see cref="IFormidableEngine.GetIssues"/> states, or one shared empty list when it has none.</returns>
    // The fault trails the field's own verdict because it is about the pass rather than the field.
    // Everything after the submit errors is filtered against what is already showing, so a
    // message a later channel repeats (a server reply echoing an advisory the client disclosed, a
    // live rule failing the same way twice) reads once, where the first channel to say it put it.
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
    /// <returns>Every showing issue with its field, collected channel by channel as <see cref="IFormidableEngine.GetVisibleIssues"/> states and sorted by the order <see cref="SetFieldOrder"/> supplied while one is in force.</returns>
    // Collecting channel by channel decides which issues are in the list at all; the sort decides
    // only their order, which is what makes a summary's order the page's rather than the
    // validator's.
    public IReadOnlyList<VisibleIssue> GetVisibleIssues()
    {
        var result = new List<VisibleIssue>();

        // Only the filtered phases consult the shadow map, so it exists only when there is one to
        // consult it — a summary showing submit errors alone builds nothing. Counted from the
        // sources rather than their filtered views, which errs only toward building a map that
        // then goes unconsulted.
        var showing = _liveVerdicts.Count > 0 || _submitVerdictAdvisories.Count > 0 || _serverAdvisories.Count > 0
            ? new HashSet<(FieldIdentifier Field, string Message)>()
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
            // The live loop below runs the same test, and that copy is the reachable one:
            // _liveVerdicts holds an empty list for every engaged field with nothing to say, and
            // ExceptShadowed is an iterator, so skipping it saves a state machine per such field.
            // Neither advisory source yields an empty list — ResolveVisibleAdvisories groups, so
            // its buckets are non-empty, and a server bucket takes its issue as it is created —
            // so this copy guards a case the advisory channel cannot present. It is kept for the
            // symmetry, for the single integer test it costs, and because the live store is proof
            // that a per-field issue store can carry an empty-list path.
            if (issues.Count == 0)
            {
                continue;
            }

            foreach (var issue in ExceptShadowed(issues, field, showing!))
            {
                result.Add(new VisibleIssue(field, issue));
            }
        }

        if (showing is not null)
        {
            foreach (var (field, issues) in LiveEntries())
            {
                if (issues.Count == 0)
                {
                    continue;
                }

                foreach (var issue in ExceptShadowed(issues, field, showing))
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

    /// <summary>The gate's model-level issue, built from <see cref="FormidableOptions.DefensiveGateMessage"/> at each read, which the submit views synthesize while <see cref="GateActive"/> holds.</summary>
    private ValidationIssue GateIssue => new(string.Empty, _options.DefensiveGateMessage);

    /// <summary>Whether the gate shows: a blocked submit that disclosed nothing armed it, no server error stands, the submit answer carries errors no revealed field discloses, and the live view carries no error.</summary>
    // A predicate over the sources rather than a stored entry, recomputed on every read, which
    // is what makes it impossible for a refresh to delete. Only an error dissolves it: a warning
    // does not say why a submit was refused. The arming half matters too: an error that starts
    // failing on a never-revealed field after a submit that disclosed everything it had raises
    // no gate, because no blocked submit was ever short an explanation.
    private bool GateActive
    {
        get
        {
            if (!_gateArmed || _serverErrors.Count > 0 || _submitVerdictErrors.Count == 0)
            {
                return false;
            }

            if (_revealedErrorFields.Overlaps(_submitVerdictErrors.Keys))
            {
                return false;
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

    /// <summary>One field's live view: the verdict the last live pass filed for it while the field is engaged, read through <see cref="LiveViewOf"/>.</summary>
    /// <param name="field">The field.</param>
    /// <returns>The issues the view shows, empty when the field's rules passed; <see langword="null"/> when the field is not engaged or no live pass has answered it.</returns>
    // Under the default policy registration filters nothing here: an engaged field's verdict
    // discloses on every surface whether or not anything renders the field, which is the
    // native-interop bridge's contract; a field that leaves the page leaves the engaged set
    // (HasDeparted decides that). The opt-in policy narrows the view through LiveViewOf.
    private List<ValidationIssue>? LiveIssuesFor(FieldIdentifier field) =>
        _engagedFields.Contains(field) && _liveVerdicts.TryGetValue(field, out var live)
            ? LiveViewOf(field, live)
            : null;

    /// <summary>Every engaged field's live view.</summary>
    /// <returns>Each engaged field that has a filed verdict, paired with the view <see cref="LiveViewOf"/> gives it.</returns>
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

    /// <summary>Applies <see cref="FormidableOptions.LiveDisclosure"/> to one engaged field's filed verdict: untouched by default, filtered per issue on <see cref="IsVisible"/> under <see cref="LiveIssueDisclosure.EngagedAndVisible"/>.</summary>
    /// <param name="field">The field.</param>
    /// <param name="filed">The verdict the last live pass filed for it.</param>
    /// <returns>The issues that may show; the filed list itself when nothing is filtered out.</returns>
    // The one place the policy exists, so every live surface (the issue reads, the severity
    // scan, the visible-issue collection and the store projection through them) answers alike.
    // Per issue because the override is per issue: an issue forced visible still shows from a
    // field nothing renders. Read from the options at every evaluation, because options mutate in
    // place.
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

    /// <summary>One field's submit-error view: its client errors where the reveal ledger discloses them, the server's errors not already showing, and, while the gate shows, the gate issue on the model-level field.</summary>
    /// <param name="field">The field.</param>
    /// <returns>The errors the view shows, or <see langword="null"/> when the channel has nothing for the field.</returns>
    private List<ValidationIssue>? SubmitErrorsFor(FieldIdentifier field)
    {
        var client = _revealedErrorFields.Contains(field)
            && _submitVerdictErrors.TryGetValue(field, out var revealed)
                ? revealed
                : null;

        var merged = MergeServer(client, _serverErrors.GetValueOrDefault(field));

        if (field.Equals(ModelLevelField) && GateActive)
        {
            merged ??= client is null ? [] : [.. client];
            merged.Add(GateIssue);
        }

        return merged ?? client;
    }

    /// <summary>One field's submit-advisory view: its client advisories where either reveal ledger holds the field, plus the server's advisories not already showing at the same severity.</summary>
    /// <param name="field">The field.</param>
    /// <returns>The advisories the view shows, or <see langword="null"/> when there are none.</returns>
    private List<ValidationIssue>? SubmitAdvisoriesFor(FieldIdentifier field)
    {
        var client = (_revealedErrorFields.Contains(field) || _revealedAdvisoryFields.Contains(field))
            && _submitVerdictAdvisories.TryGetValue(field, out var revealed)
                ? revealed
                : null;

        return MergeServer(client, _serverAdvisories.GetValueOrDefault(field)) ?? client;
    }

    /// <summary>Appends the server's issues the client is not already showing at the same severity to a copy of the client's list.</summary>
    /// <param name="client">The client's issues for the field, never mutated; <see langword="null"/> when it has none.</param>
    /// <param name="server">The server's issues for the field; <see langword="null"/> when it has none.</param>
    /// <returns>The merged copy, or <see langword="null"/> when the server adds nothing.</returns>
    // A null return tells a caller with more to append that no copy has been taken yet; the
    // views hand the client's own list straight back when nothing merges.
    private static List<ValidationIssue>? MergeServer(
        List<ValidationIssue>? client,
        List<ValidationIssue>? server)
    {
        if (server is null)
        {
            return null;
        }

        List<ValidationIssue>? merged = null;
        foreach (var issue in server)
        {
            if (client is null || !client.Any(i => SameMessageAndSeverity(i, issue)))
            {
                merged ??= client is null ? [] : [.. client];
                merged.Add(issue);
            }
        }

        return merged;
    }

    /// <summary>Whether two issues carry the same message at the same severity, the test behind the views' server fold and the apply's duplicate fold.</summary>
    /// <param name="left">One issue.</param>
    /// <param name="right">The other.</param>
    /// <returns><see langword="true"/> when both match.</returns>
    // The severity half earns its place on the advisory side, where one list holds every
    // non-error severity and the same sentence can sit at two of them; in an error list it is
    // constant. The live-issue merge inside RebuildStore tests the message alone, for the reason
    // stated there.
    private static bool SameMessageAndSeverity(ValidationIssue left, ValidationIssue right) =>
        left.Message == right.Message && left.Severity == right.Severity;

    /// <summary>Every field the submit-error view has entries for, with its merged view: revealed fields in report order, server-only fields in apply order, then the gate while it shows.</summary>
    /// <returns>The fields and their views; <see cref="GetVisibleIssues"/> and <see cref="RebuildStore"/> both walk this one enumeration.</returns>
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

    /// <summary>Every field the submit-advisory view has entries for, with its merged view.</summary>
    /// <returns>The fields and their views, client-revealed fields first and server-only fields after.</returns>
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

    /// <summary>Sets the flag for each severity <paramref name="issues"/> carries, stopping once all three are set.</summary>
    /// <param name="issues">The issues to scan.</param>
    /// <param name="hasErrors">Set when an error is seen.</param>
    /// <param name="hasWarnings">Set when a warning is seen.</param>
    /// <param name="hasInfos">Set when an info is seen.</param>
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

    /// <summary>Adds every issue to <paramref name="result"/> and records each message as showing for the field.</summary>
    /// <param name="result">The list being built.</param>
    /// <param name="showing">The messages already showing for the field, which later channels are filtered against.</param>
    /// <param name="issues">The issues to add.</param>
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

    /// <summary>Yields the issues whose message is not already showing for the field, recording each one it yields.</summary>
    /// <param name="issues">One channel's issues for the field.</param>
    /// <param name="showing">The messages already showing; it grows as issues pass, so a message repeated within one channel yields once.</param>
    /// <returns>The issues that add a message the field is not already showing.</returns>
    // The one shadow rule behind every merged issue read: a rule that fails in more than one
    // channel reads as one message, in the position the first channel to say it gave it.
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
    /// <param name="showing">The form-wide shadow map, or <see langword="null"/> when none is kept.</param>
    /// <param name="field">The field.</param>
    /// <param name="issue">The issue whose message is showing.</param>
    private static void RecordShowing(
        HashSet<(FieldIdentifier Field, string Message)>? showing,
        FieldIdentifier field,
        ValidationIssue issue) => showing?.Add((field, issue.Message));

    /// <summary>The field-keyed form of <see cref="ExceptShadowed(List{ValidationIssue}, HashSet{string})"/> for the whole-form read, where one set spans every field.</summary>
    /// <param name="issues">One channel's issues for the field.</param>
    /// <param name="field">The field the issues belong to.</param>
    /// <param name="showing">The form-wide shadow map; it grows as issues pass.</param>
    /// <returns>The issues that add a message the field is not already showing.</returns>
    private static IEnumerable<ValidationIssue> ExceptShadowed(
        List<ValidationIssue> issues,
        FieldIdentifier field,
        HashSet<(FieldIdentifier Field, string Message)> showing)
    {
        foreach (var issue in issues)
        {
            if (showing.Add((field, issue.Message)))
            {
                yield return issue;
            }
        }
    }

    /// <inheritdoc />
    /// <param name="field">The field.</param>
    /// <remarks>Raises <see cref="StateChanged"/> only on a first touch.</remarks>
    public void MarkTouched(FieldIdentifier field)
    {
        if (_touched.Add(field))
        {
            NotifyStateChanged();
        }
    }

    /// <summary>The model-level identifier: the root model with an empty field name, which an issue with an empty path resolves to.</summary>
    internal FieldIdentifier ModelLevelField => new(_model, string.Empty);

    /// <summary>Sets the order <see cref="GetVisibleIssues"/> lists by; an order equal to the one in force raises nothing, and <see langword="null"/> restores validator order.</summary>
    /// <param name="order">Each rendered field's ordinal in document order, as the host resolved it, or <see langword="null"/>.</param>
    /// <remarks>
    /// A field absent from the map sorts after every mapped field. <see cref="FormidableForm{TModel}"/>
    /// offers the model-level field with the rest, and its element, the form itself, puts it
    /// first in document order. Only an order that differs raises <see cref="StateChanged"/>.
    /// </remarks>
    // A field absent from the map sorts last because an unrendered field cannot be scrolled to,
    // so where it lands does not matter.
    // Notifying on every resolve would put a render round behind every registration change, a
    // steady stream on a page whose registered set churns as it scrolls (a virtualized
    // collection), and one that can re-order a summary out from under a click. Staying quiet
    // about a change is not an option either: a resolve lands after the render that produced
    // the elements it measured, and a reorder that registers nothing (rows moved under @key)
    // raises no pass, no edit and no server apply for the new order to ride on. Gating on
    // difference also settles the sequence a host watching the DOM for those moves sets off: the
    // re-render a changed order provokes resolves once more, that answer matches, and the
    // sequence stops.
    internal void SetFieldOrder(IReadOnlyDictionary<FieldIdentifier, int>? order)
    {
        if (SameFieldOrder(_fieldOrder, order))
        {
            return;
        }

        _fieldOrder = order;
        NotifyStateChanged();
    }

    /// <summary>Whether two resolved orders sort visible issues identically; <see langword="null"/> is validator order and differs from any map.</summary>
    /// <param name="current">The order in force.</param>
    /// <param name="replacement">The order offered.</param>
    /// <returns><see langword="true"/> when both are the same reference, or hold the same fields at the same ordinals.</returns>
    // Count first, then every ordinal: both maps hold one entry per rendered field, so this is a
    // handful of dictionary probes and cheaper by far than the render it decides against.
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

    /// <summary>Tells the engine the rendered field set moved: departed fields leave the live channel, the stored verdicts and the presence map (<see cref="Requirements"/>) are dropped, and a refresh is armed.</summary>
    /// <remarks>
    /// Departed means registered once and no longer rendered, so a field nothing ever
    /// registered keeps its verdict, and a row held by keep-registered keeps its messages.
    /// Dropping is all this does: an issue a departure causes shows only once a later pass
    /// computes it, by default the refresh this arms, and on a form never submitted that refresh
    /// discloses nothing. A model whose contents changed still owes a field-changed notification
    /// of its own.
    /// </remarks>
    // Nothing announces a row removed, a section collapsed or a branch swapped as a field change,
    // so this call is the engine's only word that the page its verdicts describe is not the page
    // on screen. The refresh is owed twice over: once for the submit channel's answer, and once
    // because the emptied verdict store leaves the valid class's vouch nothing of its own to
    // read. What that refresh drops is whatever the rules stop producing: a removed row's entry
    // goes because its rule no longer fires, not because the row left, and an entry for a field
    // a collapsed section hid survives because the rule still fails.
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

        var dropped = false;
        if (departed is not null)
        {
            foreach (var field in departed)
            {
                _engagedFields.Remove(field);
                dropped |= _liveVerdicts.Remove(field);
            }

            if (dropped)
            {
                RebuildStore();
            }
        }

        // Under the opt-in live-disclosure policy the registered field set is one of the live
        // view's own inputs — a field REGISTERING can disclose a live verdict the store was not
        // projecting, exactly as a departure can retract one — so a field-set change with filed
        // verdicts standing owes a republish in that mode. The default policy never consults
        // registration, which is what keeps the default's churn cost at the departure-only
        // republish above.
        if (!dropped
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
        _verdictStore.Clear();
        // The emptied store answers for nothing, so the coverage read re-derives — or, while the
        // edit stamp says the model it described still stands, holds the answer it last gave.
        _submitCoverage.MoveVersion();

        // The presence demands are keyed by fields resolved against the model graph, and a move
        // in what the page renders is the one signal the engine has that the graph behind it may
        // have moved too — the same argument the store clear above rests on, since the edit
        // counter cannot see either. Dropping it is all that happens here: the next ask rebuilds
        // it — a walk of the declared rules, not a pass — against the graph that ask finds. Which
        // is not the same as making every ASK current: a component holds the identifier it
        // resolved at bind, so re-filing a nested member under a replaced owner is what strands
        // one (see Requirements).
        _requirements = null;

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
        // would let that pass's verdicts claim to answer for an edit they never saw. The field
        // joins the edited-past-hold record in the same breath: if the held coverage answer is
        // served across this edit, this is the one field it must not vouch for.
        _editStamp++;
        _submitCoverage.NoteEdit(e.FieldIdentifier);

        MarkTouched(e.FieldIdentifier);

        // A notification is a committed change, and a committed change engages the field: from
        // here on every live pass answers it — its issue can clear, or appear, because of an
        // edit elsewhere — until it leaves the rendered page. Deliberately not folded into
        // MarkTouched: touched is CSS disclosure a component may grant on a bare blur, while
        // engagement is the engine's record of the fields the live channel answers for. A
        // committed change is one way in — including one that empties the field, which is the
        // whole of how a cleared box starts speaking; DiscloseLoadedValuesAsync is the other,
        // for values a page loaded rather than the visitor typed.
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

    /// <summary>Whether the pass in flight is a live pass, which a refresh fire stands down for.</summary>
    private bool LiveInFlight => _currentPass?.Kind == PassKind.Live;

    /// <summary>Whether the pass in flight is a submit, which every live pass, the refresh fire and the probe stand down for.</summary>
    // The load never reads it: a caller starts and awaits a load as it does a submit, so two of
    // them overlapping resolve the way two submits do, the later one taking the descriptor.
    private bool SubmitInFlight => _currentPass?.Kind == PassKind.Submit;

    /// <summary>Whether the pass in flight is a refresh, which only a debounced live fire stands down for; an immediate live pass supersedes it instead.</summary>
    // Read only by RunDebouncedLivePassAsync. An immediate live pass may supersede a refresh
    // because of what re-arms it: after a submit the same edit re-arms it through
    // ScheduleRefresh, and before a submit the refresh belonged to a field-set change that
    // nothing re-arms, so the submit-selected coverage waits for whatever next answers that
    // profile (the live pass itself where LiveProfile resolves to the submit profile, the probe
    // under TrackFormValidity, else a submit, a load or the next field-set change). A debounced fire must
    // defer: the edit that opened its window already happened, so nothing would re-arm a refresh
    // it cancelled, and the refresh's verdict would go stale for good.
    private bool RefreshInFlight => _currentPass?.Kind == PassKind.Refresh;

    /// <summary>Whether the pass in flight is the load <see cref="DiscloseLoadedValuesAsync"/> runs, which every live pass, the refresh fire and the probe stand down for.</summary>
    // On the grounds a submit is stood down for: the load is awaited by its caller, and its
    // verdict decides disclosure rather than refreshing it, so a pass that superseded it would
    // leave the loaded values neither vouched for nor spoken about.
    private bool LoadInFlight => _currentPass?.Kind == PassKind.Load;

    /// <summary>Supersedes any pass in flight, moves the engine's version and records the new pass as current.</summary>
    /// <param name="kind">The new pass's kind.</param>
    /// <param name="external">The caller's token, linked into the pass's own; <see cref="CancellationToken.None"/> for a live or refresh pass.</param>
    /// <returns>The new pass's descriptor.</returns>
    private PassScope BeginPass(PassKind kind, CancellationToken external)
    {
        _passCts?.Cancel();
        _passCts?.Dispose();
        _passCts = CancellationTokenSource.CreateLinkedTokenSource(external);
        _version++;
        var pass = new PassScope(kind, _version, _passCts.Token, _timeProvider.GetTimestamp());
        _currentPass = pass;
        return pass;
    }

    /// <summary>Retires the current pass: clears <see cref="IsValidating"/>, its scope and the descriptor together, and moves the coverage version.</summary>
    /// <remarks>
    /// The caller establishes that the pass is still current: the verdict dispatch through its
    /// own version guard, <see cref="EndPassWithoutLandingAsync"/> through the one
    /// <see cref="PublishPassStateAsync"/> applies.
    /// </remarks>
    // The three clear together so nothing that runs afterwards can read a pass that has ended.
    // The coverage version moves whatever the outcome: a landing can have written verdicts the
    // coverage read derives from, live passes included, and a pass that ends without landing is
    // no longer the re-answer a held vouch was served on the promise of. Either way the next
    // coverage read must re-decide rather than stand on a cache that predates the end.
    private void EndPass()
    {
        IsValidating = false;
        _validatingScope = null;
        _currentPass = null;
        _submitCoverage.MoveVersion();
    }

    /// <summary>Publishes that <paramref name="pass"/> began: sets <see cref="IsValidating"/> and narrows the checking indicator to <paramref name="fields"/>.</summary>
    /// <param name="pass">The pass, which must still be current for the write to land.</param>
    /// <param name="fields">The pending scope: a live pass's triggering fields, a refresh's fields edited within its window, an empty set for a load, and <see langword="null"/> for a submit, which covers every field.</param>
    private Task BeginValidatingAsync(PassScope pass, HashSet<FieldIdentifier>? fields) =>
        PublishPassStateAsync(pass, () =>
        {
            IsValidating = true;
            _validatingScope = fields;
        });

    /// <summary>Retires a pass that ended without landing (a fault or a caller's cancellation) and abandons the held coverage answer, when the pass is still current.</summary>
    /// <param name="pass">The pass that ended.</param>
    // Retraction is immediate rather than bound-delayed: EndPass moves the coverage version out
    // from under the cache, and abandoning the held answer keeps the serve route from handing it
    // straight back for a still-armed window or the bound's remainder. A landed pass ended at its
    // verdict dispatch and never reaches here, and a superseded pass fails the version gate
    // PublishPassStateAsync applies, so neither costs a hold anything.
    private Task EndPassWithoutLandingAsync(PassScope pass) =>
        PublishPassStateAsync(pass, () =>
        {
            _submitCoverage.Abandon();
            EndPass();
        });

    /// <summary>Applies a pass-state change on the renderer's dispatcher and raises both the engine's and the <see cref="EditContext"/>'s notifications, when <paramref name="pass"/> is still current.</summary>
    /// <param name="pass">The pass the change belongs to; a superseded pass's change is skipped rather than written over a newer pass's state.</param>
    /// <param name="change">The state write.</param>
    // The EditContext's own notification is what re-renders a native InputBase, which does not
    // subscribe to StateChanged: without it the pending class a native input takes through
    // FormidableFieldCssClassProvider would light at the next store rebuild and have no later
    // trigger to clear it once the pass ends.
    private Task PublishPassStateAsync(PassScope pass, Action change) =>
        _renderDispatch(() =>
        {
            if (pass.Version == _version)
            {
                change();
                NotifyStateChanged();
                EditContext.NotifyValidationStateChanged();
            }
            return Task.CompletedTask;
        });

    /// <summary>Runs one pass: begins it, validates under <paramref name="profile"/>, applies the verdict only while the pass is still current, and ends it exactly once however it left.</summary>
    /// <param name="kind">The pass kind, which decides the fault policy and whether stored verdicts are served; a submit executes its whole selection.</param>
    /// <param name="profile">The profile the model is validated under.</param>
    /// <param name="external">The caller's token; only a submit and a load carry one, so a cancellation with none requested means supersession.</param>
    /// <param name="beginScope">Produces the pending scope once the pass has begun; a refresh's is a snapshot-and-clear of its accumulator, so when it is taken matters.</param>
    /// <param name="applyVerdict">Writes the verdict into engine state, on the dispatcher, in the same dispatch as the store rebuild that publishes it.</param>
    /// <returns>The report, or <see langword="null"/> when the validation was cancelled by supersession or faulted; a pass superseded at its verdict dispatch returns the report it never applied.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="external"/> was cancelled during a submit or a load; a validator's exception under either kind propagates as well.</exception>
    // One skeleton for every kind: the kinds differ in what they hand in, not in how they run,
    // and a copy hand-rolled per kind is one where a single copy can quietly stop raising a
    // notification, or stop clearing a flag, that the others still do.
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
        var generation = _verdictStore.Generation;

        try
        {
            await BeginValidatingAsync(pass, beginScope()).ConfigureAwait(false);

            ValidationReport report;
            List<SetVerdict>? executed;
            try
            {
                (report, executed, _) = await EvaluateAsync(
                    profile,
                    executeAll: kind == PassKind.Submit,
                    editStamp,
                    pass.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!external.IsCancellationRequested)
            {
                return null; // superseded by a newer pass, rather than cancelled by the caller
            }
            catch (Exception exception) when (kind is not (PassKind.Submit or PassKind.Load))
            {
                // A submit and a load are the passes someone is awaiting, so a validator that
                // throws under either has somewhere to surface: the caller's own try/catch. A
                // live or refresh pass is fire-and-forget, so its fault has to become form state
                // and an event instead.
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
                _verdictStore.TryFile(executed, generation);

                applyVerdict(report);

                // Coverage bookkeeping, after the apply so the submit channel's source is the
                // one this pass just rebuilt. A submit, a refresh and a load each run the submit
                // profile itself, so any of them landing is a completed whole-model SubmitProfile
                // evaluation whose begin stamp and resolved error fields become the
                // capability-less coverage source — the apply resolved every error, undisclosed
                // ones included, which is exactly what "would fail submit" needs. A live pass is
                // excluded by kind rather than by the profile it ran: LiveProfile decides that,
                // and a narrowed one answers for fewer rules than a submit would.
                // The coverage version moves with EndPass below, for every landing, live
                // passes included: any landing can have written verdicts the coverage read
                // derives from.
                if (kind != PassKind.Live)
                {
                    _submitCoverage.RecordWholeModelAnswer(
                        editStamp, [.. _submitVerdictErrors.Keys]);
                }

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
                await EndPassWithoutLandingAsync(pass).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Validates the model under <paramref name="profile"/>, rule by rule against the verdict store when the validator allows it and as the whole profile in one call otherwise; writes nothing.</summary>
    /// <param name="profile">The profile.</param>
    /// <param name="executeAll">Whether to serve nothing from the store and execute the whole selection, as a submit does.</param>
    /// <param name="editStamp">The edit stamp the evaluation began at, which the executed verdicts carry.</param>
    /// <param name="token">Cancels the validation.</param>
    /// <returns>The report, always a whole-profile answer; the sets executed, <see langword="null"/> when none ran; and whether the rule-level path ran, which the verdict list alone cannot say.</returns>
    // Nothing is caught and nothing is written, because the callers' fault policies differ: a
    // pass tells supersession from its caller's cancellation and reports or rethrows by kind,
    // where a probe nobody awaits has only the event. Each caller files the verdicts through its
    // own version- and generation-gated apply.
    private async Task<(ValidationReport Report, List<SetVerdict>? Executed, bool RuleCapable)> EvaluateAsync(
        ValidationProfile profile,
        bool executeAll,
        int editStamp,
        CancellationToken token)
    {
        if (_validator is IRuleLevelValidator<TModel> ruleLevel && ruleLevel.CanValidateByRule)
        {
            // The store is only ever touched on the dispatcher, so the decision about what is
            // left to execute rides one dispatch of its own — the same channel every other
            // store mutation uses — rather than racing a clear or a write from off it. A
            // selection error (a typo'd ruleset name, say) surfaces here exactly as a
            // whole-profile validation surfaces it, through the caller's fault policy.
            RulePlan plan = null!;
            await _renderDispatch(() =>
            {
                plan = _verdictStore.Plan(ruleLevel.SelectRules(profile), executeAll, editStamp, profile);
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            var (report, executed) = await ExecuteRulePlanAsync(ruleLevel, profile, plan, editStamp, token)
                .ConfigureAwait(false);
            return (report, executed, true);
        }

        var wholeProfile = await _validator.ValidateAsync(_model, profile, token).ConfigureAwait(false);
        return (wholeProfile, null, false);
    }

    /// <summary>Executes the plan's remainder one validator call per selection class and assembles the report from the sets served and the sets just run.</summary>
    /// <param name="ruleLevel">The validator's rule-level seam.</param>
    /// <param name="profile">The profile the rules run under.</param>
    /// <param name="plan">The sets to serve and the rules to execute.</param>
    /// <param name="editStamp">The edit stamp the executed verdicts are stamped with.</param>
    /// <param name="token">Cancels the execution.</param>
    /// <returns>The assembled report and the executed sets, <see langword="null"/> when the remainder was empty; nothing here touches the store, so a superseded or faulted pass's work goes with its locals.</returns>
    /// <exception cref="InvalidOperationException">The validator's groups do not hold the remainder's rules exactly once, by count.</exception>
    // Per selection class rather than the whole remainder as one set: a verdict for a set that
    // mixes in rules a narrower profile does not select could never serve that profile, so the
    // two passes over one edit would each run the rules they share. The partition costs one
    // validator call per class and keeps a set servable to whichever selection contains it. The
    // count check compares totals, so a drop and a repeat that cancel out pass it; only the drop
    // is a wrong answer, a repeat costing the second execution alone.
    private async Task<(ValidationReport Report, List<SetVerdict>? Executed)> ExecuteRulePlanAsync(
        IRuleLevelValidator<TModel> ruleLevel,
        ValidationProfile profile,
        RulePlan plan,
        int editStamp,
        CancellationToken token)
    {
        List<ValidationIssue>? issues = null;

        foreach (var stored in plan.Reused)
        {
            if (stored.Issues.Count > 0)
            {
                (issues ??= []).AddRange(stored.Issues);
            }
        }

        List<SetVerdict>? executed = null;
        if (plan.Remainder.Count > 0)
        {
            var grouped = 0;
            foreach (var group in ruleLevel.GroupBySelectionClass(plan.Remainder))
            {
                grouped += group.Count;
                if (group.Count == 0)
                {
                    continue;
                }

                var result = await ruleLevel.ValidateRulesAsync(_model, profile, group, token).ConfigureAwait(false);
                (executed ??= []).Add(new SetVerdict(
                    [.. group], result.Report.Issues, editStamp, result.IsProfileScoped, profile));
                if (result.Report.Issues.Count > 0)
                {
                    (issues ??= []).AddRange(result.Report.Issues);
                }
            }

            // One addition per group, against the failure the assembled report cannot show: a
            // rule left out of every group is a rule this pass never runs, and its issues are
            // missing from a report that looks complete. An equality rather than a floor, so a
            // grouping that repeats a rule is caught by the same count.
            if (grouped != plan.Remainder.Count)
            {
                throw new InvalidOperationException(
                    $"GroupBySelectionClass on this validator " +
                    $"('{ruleLevel.GetType().Name}') returned {grouped} rules for a set of " +
                    $"{plan.Remainder.Count} — the groups must hold each of the set's rules " +
                    "exactly once, because they are the only thing a caller runs.");
            }
        }

        return (issues is null ? ValidationReport.Empty : new ValidationReport(issues), executed);
    }

    /// <summary>Files the form-level fault issue while <paramref name="pass"/> is still current, then raises <see cref="ValidationFaulted"/> either way.</summary>
    /// <param name="pass">The pass that faulted; a superseded pass's exception still happened, so the event is raised for it too.</param>
    /// <param name="exception">What the validator threw.</param>
    // The sentence is read from ValidationFaultMessage at the one moment it is filed, so the
    // stored issue keeps the wording in force when it was written.
    private async Task ReportFaultAsync(PassScope pass, Exception exception)
    {
        await _renderDispatch(() =>
        {
            if (pass.Version == _version)
            {
                _faultIssue = new ValidationIssue(string.Empty, _options.ValidationFaultMessage);
                RebuildStore();
            }
            return Task.CompletedTask;
        }).ConfigureAwait(false);

        ValidationFaulted?.Invoke(this, new FormidableValidationFaultedEventArgs(exception));
    }

    /// <summary>Runs a live pass for <paramref name="triggeringFields"/> that answers every engaged field as snapshotted at begin, unless a submit or a load is in flight.</summary>
    /// <param name="triggeringFields">The field an edit committed, or every field an open debounce window accumulated; they scope the checking indicator, while the verdict covers the engaged set.</param>
    // The indicator shows where the user acted and the verdict covers every field the user has
    // committed a change to, which is how a cross-field issue clears, or appears, on a field the
    // triggering edit never named.
    private async Task RunLivePassAsync(IReadOnlyCollection<FieldIdentifier> triggeringFields)
    {
        if (SubmitInFlight || LoadInFlight)
        {
            // Submit is the higher-intent operation; live/refresh passes never supersede it. A
            // load pass is stood down for on the same terms — see LoadInFlight.
            return;
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
        var liveProfile = ResolvedLiveProfile;

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

                // When the live channel ran the submit profile itself — the default — this
                // report IS a completed whole-model submit-profile answer, indistinguishable from
                // the one a refresh produces, so it rebuilds the submit channel's source too. The
                // server source is NOT cleared here, unlike a refresh: a live pass is an edit's
                // own answer, not the debounced settling point the server snapshot yields to.
                // Both projections split byField rather than re-walking the report: a severity
                // filter and a field filter commute over one fixed issue order, so splitting the
                // grouping already built above reproduces what grouping report.Errors and the
                // non-error remainder separately would, at one Resolve call per issue instead of
                // three.
                if (ReferenceEquals(liveProfile, _options.SubmitProfile))
                {
                    (_submitVerdictErrors, _submitVerdictAdvisories) = SplitBySeverity(byField);
                }
            }).ConfigureAwait(false);
    }

    /// <summary>Evaluates the submit profile as a standalone probe for <see cref="FormidableOptions.TrackFormValidity"/>, writing <see cref="IsFormValid"/> only when it flips and only while this probe is the latest.</summary>
    /// <remarks>
    /// Not a pass: it never calls <see cref="BeginPass"/>, discloses nothing, touches no
    /// indicator, and stands down for a submit or a load in flight. Its store filing and its
    /// sharing with the passes are on the engine page under the <c>TrackFormValidity</c> probe.
    /// </remarks>
    private async Task ProbeFormValidityAsync()
    {
        if (SubmitInFlight || LoadInFlight)
        {
            return;
        }

        var stamp = ++_formValidityStamp;

        // Captured synchronously, mirroring a pass's own begin: the edit stamp names the model
        // state this probe's answer — and any verdicts it lands — speaks for, the generation
        // names the rendered field set they were computed against, and the profile is
        // remembered because the options holding it are settable.
        var editStamp = _editStamp;
        var generation = _verdictStore.Generation;
        var profile = _options.SubmitProfile;

        ValidationReport report;
        List<SetVerdict>? executed;
        bool ruleCapable;
        try
        {
            (report, executed, ruleCapable) = await EvaluateAsync(
                profile,
                executeAll: false,
                editStamp,
                _probeCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return; // disposed mid-probe - nothing left to write into
        }
        catch (Exception exception)
        {
            // The event and nothing else: a form-level fault issue would disclose something an
            // invisible probe promises never to. Without the event, a validator that throws on
            // the submit profile (a narrowed LiveProfile can keep a live pass from ever selecting
            // the throwing rule) would freeze IsFormValid at its last value with no diagnostic
            // anywhere, stranding a disable-submit button in whatever state it was last in.
            ValidationFaulted?.Invoke(this, new FormidableValidationFaultedEventArgs(exception));
            return;
        }

        await _renderDispatch(() =>
        {
            if (_disposed)
            {
                return Task.CompletedTask;
            }

            // The probe's stricter write gate is the store's four-argument overload:
            // generation-checked like a pass's filing, and additionally refused when an edit
            // has arrived since the probe began — a probe has no version for a fresher
            // landing to supersede it through.
            var movedCoverage = _verdictStore.TryFile(executed, generation, editStamp, _editStamp);

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

                    _submitCoverage.RecordWholeModelAnswer(editStamp, errorFields);
                    movedCoverage = true;
                }

                SetFormValidity(report.IsValid);
            }

            if (movedCoverage)
            {
                _submitCoverage.MoveVersion();
                NotifyStateChanged();
                EditContext.NotifyValidationStateChanged();
            }

            return Task.CompletedTask;
        }).ConfigureAwait(false);
    }

    /// <summary>Adopts a whole-model submit-profile report's validity into <see cref="IsFormValid"/>, ahead of any probe still in flight; a no-op when <see cref="FormidableOptions.TrackFormValidity"/> is off.</summary>
    /// <param name="report">The report of the submit, refresh or load that just landed.</param>
    // Each of those passes computes exactly this quantity, so there is nothing for a probe to
    // add, and the stamp moves whether or not the value changes: a probe that started earlier,
    // or one already reaching its write-back, discards its answer rather than land after this
    // one. On a rule-capable validator the adopted report is assembled from the store the pass
    // just repopulated, so this is the store's own reading landing. With tracking off no probe
    // can be in flight and nothing reads IsFormValid.
    private void AdoptFormValidity(ValidationReport report)
    {
        if (!_options.TrackFormValidity)
        {
            return;
        }

        _formValidityStamp++;

        SetFormValidity(report.IsValid);
    }

    /// <summary>Writes <see cref="IsFormValid"/> and raises <see cref="StateChanged"/> only when the value changes.</summary>
    /// <param name="value">The answer a probe or an adoption computed.</param>
    // Both writers recompute at their own cadence (the probe on every field-changed notification
    // with no LiveDebounce, once per window with one), and the answer mostly comes back the
    // same, so an unconditional notify would publish a render round for a value nothing on the
    // page could tell from the last.
    private void SetFormValidity(bool value)
    {
        if (value != IsFormValid)
        {
            IsFormValid = value;
            NotifyStateChanged();
        }
    }

    private FieldIdentifier Resolve(ValidationIssue issue) => ResolvePath(issue.Path);

    /// <summary>Resolves a path against the model through the introspector and turns the result into a <see cref="FieldIdentifier"/>.</summary>
    /// <param name="path">A path a validator declared or an issue reported.</param>
    /// <returns>The identifier of the object and member the path names, keyed on the root model and the whole path where the walk ends on a struct.</returns>
    // The one place an issue's path and a rule's declared path become the same kind of answer,
    // so a demand and the failure it describes cannot land on different fields.
    private FieldIdentifier ResolvePath(string path) =>
        _introspector.Resolve(_model, path).ToFieldIdentifier(_model, path);

    /// <summary>Writes the one Information-level diagnostic for a validator that cannot report its rules: no required marks from the rules, and no load confirmations.</summary>
    // Information rather than Warning, because an uninspectable validator is a supported
    // configuration and the form validates correctly through it; what it costs is invisible on
    // the page, which is the reason for saying it somewhere. The line names the state rather
    // than the mistake, because the capability test reads the same for a wrapper that dropped
    // the capability as for a validator that never had one.
    private void ReportInspectionUnavailable()
    {
        var model = FriendlyTypeName.Of(typeof(TModel));
        var validator = FriendlyTypeName.Of(_validator.GetType());
        System.Diagnostics.Trace.WriteLine(
            $"Formidable: the validator for {model} ('{validator}') cannot report its own rules. " +
            "Validation is unaffected, but nothing is marked required from the rules, so a " +
            "required indicator and aria-required reach only the fields " +
            "FormidableOptions.RequiredOverride declares required, and a " +
            "DiscloseLoadedValuesAsync load confirms nothing. A validator that wraps another " +
            "keeps what the validator underneath has by deriving from DelegatingModelValidator " +
            "rather than implementing IModelValidator alone; one that is not a FluentValidation " +
            "AbstractValidator has no rules Formidable can read.");
        _logger?.LogInformation(
            "Formidable: the validator for {Model} ('{Validator}') cannot report its own rules. " +
            "Validation is unaffected, but nothing is marked required from the rules, so a " +
            "required indicator and aria-required reach only the fields " +
            "FormidableOptions.RequiredOverride declares required, and a " +
            "DiscloseLoadedValuesAsync load confirms nothing. A validator that wraps another " +
            "keeps what the validator underneath has by deriving from DelegatingModelValidator " +
            "rather than implementing IModelValidator alone; one that is not a FluentValidation " +
            "AbstractValidator has no rules Formidable can read.",
            model, validator);
    }

    /// <summary>Reports one suppressed issue: a Trace line, a logged warning, <see cref="FormidableOptions.SuppressedIssueDiagnostic"/>, and <see cref="FormidableOptions.NeverRegisteredFieldDiagnostic"/> for a field nothing ever registered.</summary>
    /// <param name="issue">The issue that will not show.</param>
    /// <remarks>
    /// Two sites call it: a submit, for the errors its reveal ledger leaves undisclosed, and
    /// <see cref="ApplyServerIssues"/>, for an advisory its visibility answer hides. Other drops
    /// are silent: a submit's non-visible advisories, the live issues <see cref="LiveViewOf"/>
    /// filters, and a server error <see cref="FormidableOptions.DisclosureOverride"/> hides.
    /// </remarks>
    // One place for both sites keeps the three channels from drifting apart. The logged warning
    // needs no wiring on WebAssembly, whose default provider is the browser console.
    private void ReportSuppressed(ValidationIssue issue)
    {
        var path = DiagnosticPathSanitizer.ForDiagnostic(issue.Path);
        System.Diagnostics.Trace.WriteLine(
            $"Formidable: issue at '{path}' is suppressed - nothing renders its field, or a disclosure override answered no.");
        _logger?.LogWarning(
            "Formidable: issue at '{Path}' is suppressed - nothing renders its field, or a disclosure override answered no.",
            path);

        // The callbacks receive the issue as received. The sanitized path above is for the two
        // text channels the library writes itself; a callback that writes text of its own owes
        // the path the same treatment, and the options' documentation says so.
        _options.SuppressedIssueDiagnostic?.Invoke(issue);

        if (_options.NeverRegisteredFieldDiagnostic is not null && !Registry.HasEverRegistered(Resolve(issue)))
        {
            _options.NeverRegisteredFieldDiagnostic(issue);
        }
    }

    /// <summary>Writes the stale-registration diagnostic for <see cref="FormidableOptions.ReportStaleRegistrations"/>: a Trace line, a logged warning and <see cref="FormidableOptions.StaleRegistrationDiagnostic"/>, in that order.</summary>
    /// <param name="report">The component and the two fields its accessor named, described through <see cref="FormidableComponentBase.DescribeChange"/> as the row-key throw describes them.</param>
    // Beside the other diagnostics, behind one seam every bound component routes through, so the
    // channels cannot drift apart. The names echoed come from the component's own accessor
    // expression (compiler-written member and type names, never a payload's strings), so unlike
    // the suppressed-issue report's path there is nothing for DiagnosticPathSanitizer to
    // neutralize.
    void IStaleRegistrationReporter.Report(StaleRegistrationReport report)
    {
        var component = FriendlyTypeName.Of(report.ComponentType);
        var change = FormidableComponentBase.DescribeChange(report.RegisteredField, report.CurrentField);
        System.Diagnostics.Trace.WriteLine(
            $"Formidable: {component} {change}, without having been rebuilt in between - its " +
            "registration, element id, aria attributes and messages stay with the field it " +
            "registered. Key the component by the owning object (@key=\"item\" on the element " +
            "the loop renders) so a replacement rebuilds it. (Reported by " +
            "FormidableOptions.ReportStaleRegistrations; FormidableOptions.VerifyRowKeys throws " +
            "for this instead.)");
        _logger?.LogWarning(
            "Formidable: {Component} {Change}, without having been rebuilt in between - its " +
            "registration, element id, aria attributes and messages stay with the field it " +
            "registered. Key the component by the owning object (@key=\"item\" on the element " +
            "the loop renders) so a replacement rebuilds it. (Reported by " +
            "FormidableOptions.ReportStaleRegistrations; FormidableOptions.VerifyRowKeys throws " +
            "for this instead.)",
            component, change);

        // The callback receives the identifiers as the component holds them; the composed text
        // above is the library's own two channels. A page rendering them encodes as any Blazor
        // render does.
        _options.StaleRegistrationDiagnostic?.Invoke(report);
    }

    /// <summary>Whether <paramref name="issue"/> may show for <paramref name="field"/>: <see cref="FormidableOptions.DisclosureOverride"/>'s answer when it gives one, asked per issue at every read, else whether the field is rendered.</summary>
    /// <param name="issue">The issue.</param>
    /// <param name="field">The field it resolved to.</param>
    /// <returns><see langword="true"/> when the override says so, or says nothing and <see cref="IsRendered"/> holds.</returns>
    private bool IsVisible(ValidationIssue issue, FieldIdentifier field)
    {
        var overridden = _options.DisclosureOverride?.Invoke(issue);
        if (overridden is not null)
        {
            return overridden.Value;
        }

        return IsRendered(field);
    }

    /// <summary>Whether <paramref name="field"/> is the model-level field or is registered, kept registrations included.</summary>
    /// <param name="field">The field.</param>
    /// <returns><see langword="true"/> when the page shows somewhere the field's issues could be read.</returns>
    // The registration half of IsVisible, shared with OnRenderedFieldsChanged through HasDeparted
    // so the two cannot disagree about what having left the page means. The model-level field's
    // element is the form's own, on the page as long as the form is, and it never registers.
    private bool IsRendered(FieldIdentifier field) =>
        field.Equals(ModelLevelField) || Registry.IsRegistered(field);

    /// <summary>Whether <paramref name="field"/> has left the page: something registered it once and nothing renders it any longer.</summary>
    /// <param name="field">The field.</param>
    /// <returns><see langword="true"/> for a departed field; <see langword="false"/> for one nothing ever registered, which has not left because it never arrived.</returns>
    // The engagement lifetime's own test, and deliberately not IsRendered's negation: under the
    // default policy a never-registered field's live verdict is what the native-interop bridge
    // keeps, and an anchor-free native input registers nothing at any point, so a prune reading
    // the two states as one would retract its error the first time anything else on the page
    // registered or unregistered. Disclosure asks IsVisible instead: not whether the field ever
    // arrived, but whether the page shows somewhere its issue could be read at this moment.
    private bool HasDeparted(FieldIdentifier field) =>
        Registry.HasEverRegistered(field) && !IsRendered(field);

    /// <summary>The report's advisories whose field may show, resolved and grouped by field.</summary>
    /// <param name="report">A submit's report, with or without errors; both branches of <see cref="ValidateForSubmitAsync"/> use it alike.</param>
    /// <returns>Each visible field's advisories, in report order.</returns>
    private Dictionary<FieldIdentifier, List<ValidationIssue>> ResolveVisibleAdvisories(ValidationReport report) =>
        GroupByResolvedField(
            report.Advisories
                .Select(i => (Issue: i, Field: Resolve(i)))
                .Where(x => IsVisible(x.Issue, x.Field)));

    /// <summary>Resolves each issue to its field once and groups the issues by field.</summary>
    /// <param name="issues">The issues to group.</param>
    /// <returns>Each field's issues, in arrival order.</returns>
    // One path parse and object walk per issue, rather than one per issue for every field
    // waiting on the verdict.
    private Dictionary<FieldIdentifier, List<ValidationIssue>> GroupByResolvedField(
        IEnumerable<ValidationIssue> issues) =>
        GroupByResolvedField(issues.Select(issue => (Issue: issue, Field: Resolve(issue))));

    /// <summary>Groups issues whose field the caller has already resolved, keeping each field's arrival order.</summary>
    /// <param name="resolved">The issues paired with their fields.</param>
    /// <returns>Each field's issues.</returns>
    // The caller keeps the pairs because it has its own use for them (a visibility filter, a
    // reveal-ledger union), and resolving again here would be the cost the other overload avoids.
    private static Dictionary<FieldIdentifier, List<ValidationIssue>> GroupByResolvedField(
        IEnumerable<(ValidationIssue Issue, FieldIdentifier Field)> resolved)
    {
        var grouped = new Dictionary<FieldIdentifier, List<ValidationIssue>>();

        foreach (var (issue, field) in resolved)
        {
            if (!grouped.TryGetValue(field, out var forField))
            {
                grouped[field] = forField = [];
            }

            forField.Add(issue);
        }

        return grouped;
    }

    /// <summary>Splits field-grouped issues into their error and advisory projections, keeping each field's order.</summary>
    /// <param name="byField">The issues grouped by field.</param>
    /// <returns>The errors and the advisories, each grouped by field; a field with none of a kind has no entry on that side.</returns>
    // A severity filter and a field filter commute over one fixed issue order, so this equals
    // grouping the report's errors and its remainder separately without resolving any issue
    // twice.
    private static (Dictionary<FieldIdentifier, List<ValidationIssue>> Errors, Dictionary<FieldIdentifier, List<ValidationIssue>> Advisories)
        SplitBySeverity(Dictionary<FieldIdentifier, List<ValidationIssue>> byField)
    {
        var errors = new Dictionary<FieldIdentifier, List<ValidationIssue>>();
        var advisories = new Dictionary<FieldIdentifier, List<ValidationIssue>>();

        foreach (var (field, issues) in byField)
        {
            List<ValidationIssue>? fieldErrors = null;
            List<ValidationIssue>? fieldAdvisories = null;
            foreach (var issue in issues)
            {
                if (issue.Severity == ValidationSeverity.Error)
                {
                    (fieldErrors ??= []).Add(issue);
                }
                else
                {
                    (fieldAdvisories ??= []).Add(issue);
                }
            }

            if (fieldErrors is not null)
            {
                errors[field] = fieldErrors;
            }

            if (fieldAdvisories is not null)
            {
                advisories[field] = fieldAdvisories;
            }
        }

        return (errors, advisories);
    }

    /// <summary>Rebuilds the <see cref="ValidationMessageStore"/> from the fault issue, the submit-error view and the live errors not already showing, then notifies.</summary>
    // The EditContext API takes writes, so the projection runs at every publish point (a pass's
    // verdict apply, a server apply, a fault report, a departure that dropped a filed verdict,
    // and under EngagedAndVisible a field-set change with verdicts standing), and the store
    // between rebuilds is what the views said the last time a source moved.
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

    private void NotifyStateChanged() => StateChanged?.Invoke(this, FormidableStateChangedEventArgs.Empty);

    /// <inheritdoc />
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>The outcome; a pass a newer submit or load displaced reports blocked with an empty summary, its report empty when the validator honoured the cancellation and its own otherwise.</returns>
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
                // this report's. The engaged set itself stands: a submit neither engages a field
                // nor disengages one, whichever route put it there — so the first post-submit
                // live pass re-answers every engaged field.
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
                    _submitVerdictErrors = GroupByResolvedField(resolvedErrors);
                    _gateArmed = disclosed.Count == 0;

                    // Advisory sites are not necessarily error sites: a visible field can carry a
                    // warning while passing every error rule. The advisory ledger unions the same
                    // way the error ledger does, so a field once shown a warning keeps that
                    // warning refreshed across later submits that found it momentarily clean.
                    _submitVerdictAdvisories = ResolveVisibleAdvisories(report);
                    _revealedAdvisoryFields.UnionWith(_submitVerdictAdvisories.Keys);

                    // Both arms name the model level the same way, because both are naming the
                    // same nameless thing: an issue that resolved to no field of its own.
                    var modelLevelName = _options.ModelLevelDisplayName;
                    summary = disclosed.Count > 0
                        ? disclosed
                            .Select(x => x.Issue.DisplayName ?? x.Issue.Path)
                            .Select(name => name.Length == 0 ? modelLevelName : name)
                            .Distinct()
                            .ToList()
                        : [modelLevelName]; // the gate's own model-level entry is what the summary points at
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
    /// <param name="issues">The server's current issues.</param>
    public void ApplyServerIssues(IEnumerable<ValidationIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(issues);

        HasSubmitted = true;

        // A verdict has arrived, so the caveat that the last one was incomplete has nothing left
        // to qualify. Unconditional on purpose, and it is one of only two places the fault issue
        // is cleared: the client may still be faulting, and a pass may never run again on a form
        // the server alone judges, so waiting for a clean pass would leave the caveat standing
        // over an answer that superseded it.
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

            if (!forField.Any(i => SameMessageAndSeverity(i, issue)))
            {
                forField.Add(issue);
            }

            // A server apply is a disclosure event: it only ever unions, so the ledger takes the
            // field in and the views watch it from here until the form passes or resets — the
            // client's own last answer for it included. The two sets are separate because the two
            // channels reveal independently: a field can be an advisory site without ever having
            // been an error site, and keeps its advisory refreshed either way.
            (issue.Severity == ValidationSeverity.Error ? _revealedErrorFields : _revealedAdvisoryFields)
                .Add(field);
        }

        RebuildStore();
    }

    /// <inheritdoc />
    /// <param name="cancellationToken">Cancels the load.</param>
    /// <returns>Completes when the load pass and the live pass that follows it, while anything is engaged, have ended.</returns>
    public async Task DiscloseLoadedValuesAsync(CancellationToken cancellationToken = default)
    {
        // The model has moved and nothing told the engine so, which is this call's whole
        // premise: values written straight onto the model raise no field-changed
        // notification, so every verdict the store holds describes a model that is no longer
        // there. Moving the edit stamp before the pass reads it is what strands those
        // verdicts and leaves the pass owing an answer for every rule it selects. Without it a
        // form something has already answered for — the reconciling refresh a first render
        // arms, say — assembles its whole report from the store and reports on values the
        // visitor never had.
        _editStamp++;

        // The held coverage answer describes the values this call just declared gone, and the
        // serve routes would otherwise keep it standing — this very pass is a re-answer on its
        // way. Nothing between here and the landing may vouch from values the load replaces;
        // the pass re-answers and re-holds.
        _submitCoverage.Abandon();

        var profile = _options.SubmitProfile;
        HashSet<FieldIdentifier>? adopted = null;

        await RunPassAsync(
            PassKind.Load,
            profile,
            cancellationToken,
            // Empty, not form-wide. The pass answers for the whole model, so IsValidating says
            // so and a page-level spinner works; but no field is waiting on it in the sense the
            // pending indicator means — nobody asked, and the visitor has done nothing. A
            // form-wide scope lights every input on the page, a field carrying no rules at all
            // included, before the form has said anything about what it loaded.
            () => [],
            report =>
            {
                // The refresh apply, for the reason a refresh makes it: this IS a completed
                // whole-model submit-profile answer, so it becomes the submit channel's client
                // source and supersedes whatever a server round trip left behind. Nothing is
                // revealed by it — a load is not a submit — so on a form that has never
                // submitted the ledgers stay empty and that channel shows nothing at all.
                AdoptFormValidity(report);
                _serverErrors.Clear();
                _serverAdvisories.Clear();
                _submitVerdictErrors = GroupByResolvedField(report.Errors);
                _submitVerdictAdvisories = GroupByResolvedField(report.Advisories);

                adopted = AdoptLoadedValues(_submitVerdictErrors, profile);
            }).ConfigureAwait(false);

        // Back onto the renderer's dispatcher before engine state is touched again. The pass
        // above resumes on whatever thread its last rule completed on, and what follows reads
        // _engagedFields and then, through the live pass it starts, takes the pass bookkeeping
        // that every pass start holds on the dispatcher alone. The two debounce timers marshal
        // here for the same reason. The await above does not hold the caller's context, so where
        // the dispatcher queues this is a real hop rather than a free one; it costs the delegate
        // alone wherever the dispatcher runs inline, which a single-threaded WASM host, an
        // unsupplied one, and a pass that completed without leaving the dispatcher all do.
        await _renderDispatch(async () =>
        {
            if (adopted is not null && _engagedFields.Count > 0)
            {
                // The disclosure itself. Engagement alone shows nothing — the live view is the
                // filed verdicts read THROUGH the engaged set — so the fields just engaged need
                // a live pass to file one, and an ordinary live pass is what files it: under the
                // live channel's own profile, so what a load discloses is exactly what that
                // channel goes on disclosing rather than something the next edit would quietly
                // replace. Its cost is the pass skeleton and nothing else wherever the two
                // profiles resolve to the same instance, which is the default: the rules were
                // answered a moment ago at this same edit stamp, so the plan finds every one of
                // them fresh and executes none.
                //
                // The condition is the whole engaged set rather than the fields just adopted,
                // because that is what the pass answers for. A load can replace a value the
                // visitor had already engaged — and can adopt nothing at all while doing it, if
                // what it loaded leaves every readable field merely unfilled — and skipping the
                // pass there would leave that field's filed verdict describing the values the
                // load overwrote. A null adopted set is the other case: the pass was superseded,
                // so the pass that took it over owns what happens next.
                await RunLivePassAsync(adopted);
            }
        }).ConfigureAwait(false);
    }

    /// <summary>Marks touched and engaged every field whose loaded value is non-empty, among the load's error sites and the fields the validator declares rules for.</summary>
    /// <param name="errors">The load's errors grouped by field, the same grouping the submit channel's source holds.</param>
    /// <param name="profile">The profile whose declared fields are the vouching half's universe, expanded against the rows the model holds.</param>
    /// <returns>The adopted fields, for the live pass that discloses them to answer.</returns>
    /// <remarks>
    /// Empty means what <c>NotEmpty()</c> means against the member's declared type
    /// (<see cref="IsEmptyValue"/>), so <see langword="false"/> in a <see langword="bool"/> is
    /// empty and in a <c>bool?</c> is an answer. A value that cannot be read (a null
    /// intermediate, a model-level failure, a path naming no member) claims nothing. Only an
    /// error makes a value wrong: a field carrying an advisory is adopted on the same terms as a
    /// passing one, and the advisory shows through the live channel.
    /// </remarks>
    // The split between wrong and not-reached-yet is the value, never which kind of rule failed:
    // presence written as Must(s => !string.IsNullOrWhiteSpace(s)) is indistinguishable from a
    // range check, and reading it as one paints an untouched field red the moment a form loads.
    // Where the rules are FluentValidation's own the reading is theirs; a validator written
    // another way is judged by the same definition without being asked to agree. An empty field
    // is left alone because nobody has reached it, and nagging about every unfilled required
    // field at load is what engagement gating prevents; an unreadable value takes the same
    // direction, silence, which is what the form does anyway until this is called.
    private HashSet<FieldIdentifier> AdoptLoadedValues(
        Dictionary<FieldIdentifier, List<ValidationIssue>> errors, ValidationProfile profile)
    {
        var adopted = new HashSet<FieldIdentifier>();
        var considered = new HashSet<FieldIdentifier>();

        foreach (var field in errors.Keys)
        {
            Consider(field);
        }

        foreach (var path in DeclaredFieldPaths(profile))
        {
            Consider(ResolvePath(path));
        }

        foreach (var field in adopted)
        {
            // Both, and for the reason a committed change does both: touched is what lets a state
            // class paint at all, engagement is what makes the live channel speak. Touching alone
            // would put green on the good fields and leave the bad one silent among them, which
            // reads as "not filled in yet" rather than "this is wrong".
            _touched.Add(field);
            _engagedFields.Add(field);
        }

        return adopted;

        void Consider(FieldIdentifier field)
        {
            if (!considered.Add(field))
            {
                return;
            }

            if (_introspector.TryReadValue(field.Model, field.FieldName, out var value, out var declaredType)
                && !IsEmptyValue(value, declaredType))
            {
                adopted.Add(field);
            }
        }
    }

    /// <summary>The paths the validator declares for <paramref name="profile"/>, each collection template expanded to one path per row the model holds.</summary>
    /// <param name="profile">The profile.</param>
    /// <returns>The expanded paths (<c>Attendees[].Name</c> becomes one per attendee, none for an empty list); empty when the validator cannot be inspected or its selection throws.</returns>
    // A selection that throws (a mistyped ruleset name) answers with nothing rather than taking
    // the load down: the pass beside this one surfaces the exception through its own fault
    // policy, which is where a configuration error belongs.
    private List<string> DeclaredFieldPaths(ValidationProfile profile)
    {
        var expanded = new List<string>();
        if (_validator is not IRuleInspectingValidator<TModel> inspector || !inspector.CanInspectRules)
        {
            return expanded;
        }

        IReadOnlySet<string> declared;
        try
        {
            declared = inspector.GetDeclaredFieldPaths(profile);
        }
        catch (Exception)
        {
            return expanded;
        }

        foreach (var template in declared)
        {
            ExpandTemplate(template, expanded);
        }

        return expanded;
    }

    /// <summary>Expands the first open index in <paramref name="template"/> against the model's rows and recurses for the rest, so <c>Teams[].Members[].Alias</c> yields one path per member of every team.</summary>
    /// <param name="template">A declared path, with <c>[]</c> at each collection.</param>
    /// <param name="into">Receives one path per row, or the template itself when it has no open index.</param>
    private void ExpandTemplate(string template, List<string> into)
    {
        var open = template.IndexOf("[]", StringComparison.Ordinal);
        if (open < 0)
        {
            into.Add(template);
            return;
        }

        var head = template[..open];
        var tail = template[(open + 2)..];
        var rows = CollectionCount(head);

        for (var index = 0; index < rows; index++)
        {
            ExpandTemplate($"{head}[{index}]{tail}", into);
        }
    }

    /// <summary>The element count of the collection at <paramref name="path"/>.</summary>
    /// <param name="path">The collection's path.</param>
    /// <returns>The count; zero when the path resolves to nothing, to a non-collection or to an empty collection, which all mean no rows to expand into.</returns>
    private int CollectionCount(string path)
    {
        var resolved = _introspector.Resolve(_model, path);
        if (!_introspector.TryReadValue(resolved.Owner, resolved.PropertyName, out var value, out _))
        {
            return 0;
        }

        switch (value)
        {
            case System.Collections.ICollection collection:
                return collection.Count;
            case System.Collections.IEnumerable sequence:
                var count = 0;
                foreach (var _ in sequence)
                {
                    count++;
                }

                return count;
            default:
                return 0;
        }
    }

    /// <summary>Whether <paramref name="value"/> is empty as FluentValidation's <c>NotEmpty()</c> reads it: null, a blank string, an empty sequence, or the default of <paramref name="declaredType"/>.</summary>
    /// <param name="value">The value read from the model.</param>
    /// <param name="declaredType">The member's declared type, which is what makes the reading faithful; <see langword="null"/> reads as empty.</param>
    /// <returns><see langword="true"/> for an absence; a sequence that throws on enumeration reads as empty too.</returns>
    /// <remarks>
    /// A reference type or a <c>Nullable&lt;T&gt;</c> holding anything is an answer. A
    /// non-nullable value type holding its default reads as empty (<see langword="false"/> in a
    /// <see langword="bool"/>, zero in an <see langword="int"/>, an enum's zero value), so model
    /// an optional value as <c>T?</c> for a load to read it exactly.
    /// </remarks>
    // The non-nullable value types are exactly the ambiguous set, and every one of them errs
    // towards absence, the direction that stays silent rather than claiming something.
    private static bool IsEmptyValue(object? value, Type? declaredType)
    {
        if (value is null || declaredType is null)
        {
            return true;
        }

        if (value is string text)
        {
            return string.IsNullOrWhiteSpace(text);
        }

        if (value is System.Collections.IEnumerable sequence)
        {
            System.Collections.IEnumerator? enumerator = null;
            try
            {
                enumerator = sequence.GetEnumerator();
                return !enumerator.MoveNext();
            }
            catch
            {
                // A sequence that cannot even be asked whether it is empty — an uninitialised
                // ImmutableArray is the reachable one — is a value that cannot be read, and an
                // unreadable value claims nothing rather than being claimed either way.
                return true;
            }
            finally
            {
                (enumerator as IDisposable)?.Dispose();
            }
        }

        if (!declaredType.IsValueType || Nullable.GetUnderlyingType(declaredType) is not null)
        {
            // A reference type holding anything, and a Nullable<T> holding anything at all, are
            // both answers: the only absence either can express is null, and that is gone.
            return false;
        }

        // The default of the declared type, obtained without asking for a constructor: a
        // one-element array of it is zero-initialised, and its single element is that default
        // boxed. Building it from the declared type keeps the comparison the one NotEmpty()
        // makes, for a struct nobody here has to know about.
        return value.Equals(Array.CreateInstance(declaredType, 1).GetValue(0));
    }

    /// <summary>Arms or re-arms the refresh timer at <see cref="FormidableOptions.RefreshDebounce"/>, from every arm site alike; a disposed engine arms nothing.</summary>
    // A refresh that comes due while a live window is still open is not a race worth scheduling
    // around: whichever pass runs first executes the stale rules and stamps their verdicts, and
    // the other finds nothing left to do.
    private void ScheduleRefresh()
    {
        if (_disposed)
        {
            // A refresh pass whose dispatch was still queued when the owning component went away
            // re-arms the timer from its own deferral branch; a disposed engine arms no timer.
            return;
        }

        ArmTimer(ref _refreshTimer, RunRefreshPassAsync, _options.RefreshDebounce);
    }

    /// <summary>Arms or re-arms the one timer behind <see cref="FormidableOptions.LiveDebounce"/>, shared by every field that changes while the window is open; a disposed engine arms nothing.</summary>
    /// <param name="debounce">The window's width.</param>
    private void ScheduleLiveDebounce(TimeSpan debounce)
    {
        if (_disposed)
        {
            // A debounced live pass whose dispatch was still queued when the owning component
            // went away re-arms nothing on its own, but a field change notification racing
            // Dispose could still reach here; a disposed engine arms no timer.
            return;
        }

        ArmTimer(ref _liveTimer, RunDebouncedLivePassAsync, debounce);
    }

    /// <summary>Creates <paramref name="timer"/> on first use and arms it to run <paramref name="fire"/> once on the renderer's dispatcher after <paramref name="due"/>.</summary>
    /// <param name="timer">The timer field, created here on its first arming.</param>
    /// <param name="fire">The handler.</param>
    /// <param name="due">How long until it fires; <see cref="Timeout.InfiniteTimeSpan"/> arms a timer that never does.</param>
    // Created with no due time and no period, so arming is entirely the Change: a timer that
    // fires only when asked, and once per ask, is what makes every debounce a sliding window
    // rather than a repeating tick. The caller decides whether arming is safe at all, because how
    // a call can still arrive after disposal differs per caller.
    private void ArmTimer(ref ITimer? timer, Func<Task> fire, TimeSpan due)
    {
        timer ??= _timeProvider.CreateTimer(
            _ => _ = _renderDispatch(fire),
            state: null,
            dueTime: Timeout.InfiniteTimeSpan,
            period: Timeout.InfiniteTimeSpan);
        timer.Change(due, Timeout.InfiniteTimeSpan);
    }

    /// <summary>The live timer's handler: stands down for a submit, refresh or load in flight, else runs one live pass for the fields accumulated while the window was open.</summary>
    /// <remarks>
    /// Standing down re-arms the timer while <see cref="FormidableOptions.LiveDebounce"/> is still
    /// set; with the option cleared mid-window the window closes and the accumulator empties. A
    /// snapshot left empty, every accumulated field having departed, starts no pass; the
    /// <see cref="FormidableOptions.TrackFormValidity"/> probe still runs, because it answers for
    /// the model rather than for any field.
    /// </remarks>
    private Task RunDebouncedLivePassAsync()
    {
        if (_disposed)
        {
            // A dispatched fire can still run after the owning component went away; there is
            // nothing left here to validate against.
            return Task.CompletedTask;
        }

        if (SubmitInFlight || RefreshInFlight || LoadInFlight)
        {
            if (_options.LiveDebounce is { } liveDebounce)
            {
                // Re-arm and try again once the pass in flight finishes, touching neither the
                // accumulator nor a pass. Snapshotting here regardless (the shape every other fire
                // handler in this file uses) would still lose the fields: RunLivePassAsync's own
                // stand-down guard bails without writing them anywhere, and starting a live pass
                // against an in-flight refresh would cancel it via BeginPass without anything left to
                // re-arm it — the edit that would normally do that (see RefreshInFlight's remarks)
                // already happened when this window opened, so the refresh's own verdict would go
                // stale with no edit left to fix it. A load pass is deferred to on the grounds
                // LoadInFlight gives, which are the submit case's. LiveInFlight is deliberately
                // not checked: one live pass superseding another is the existing, correct
                // contract, and the winner's verdict answers every engaged field — the
                // superseded pass's fields among them.
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

    /// <summary>The refresh timer's handler: re-arms behind a submit, live pass or load in flight, else re-checks the whole model under the submit profile with the indicator narrowed to the fields edited within the window.</summary>
    private async Task RunRefreshPassAsync()
    {
        if (_disposed)
        {
            // A dispatched fire can still run after the owning component went away; there is
            // nothing left here to validate against, and BeginPass would cancel a _passCts that
            // Dispose already disposed.
            return;
        }

        if (SubmitInFlight || LiveInFlight || LoadInFlight)
        {
            // Defer and re-arm — the edit must still be revalidated once the pass in flight
            // finishes. Submit is the higher-intent operation and no refresh ever supersedes it,
            // and a load pass is stood down for on the same grounds (see LoadInFlight); a live
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
                _submitVerdictAdvisories = GroupByResolvedField(report.Advisories);
            }).ConfigureAwait(false);
    }

    /// <summary>Unsubscribes from the edit context, stops both timers, cancels the pass and any probe in flight, clears the message store and notifies once; later calls do nothing.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        EditContext.OnFieldChanged -= HandleFieldChanged;
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

/// <summary>The kind of a running pass, which decides what stands down for it and whether a validator's exception becomes form state or reaches the caller awaiting the pass.</summary>
internal enum PassKind
{
    /// <summary>A live pass: an edit's own check under the live profile, answering every engaged field.</summary>
    Live,

    /// <summary>A submit: the form-wide pass under the submit profile.</summary>
    Submit,

    /// <summary>A refresh: the debounced whole-model re-check under the submit profile.</summary>
    Refresh,

    /// <summary>A load: the whole-model submit-profile check <see cref="FormidableEngine{TModel}.DiscloseLoadedValuesAsync"/> runs, which only a submit or another load may supersede.</summary>
    // Its own kind rather than a refresh, because the deference rules are written in kinds:
    // nothing an edit starts may supersede it, because its verdict is the only thing that engages
    // the fields it speaks for. A pass a caller starts and awaits still can, the way two submits
    // resolve (see SubmitInFlight).
    Load,
}

/// <summary>The identity of a running pass: its kind, the version that says whether it is still current, its token and when it began.</summary>
/// <param name="Kind">The pass kind.</param>
/// <param name="Version">The engine version the pass took at begin; every write it makes is gated on this still being the engine's own, because a pass is superseded, never waited for.</param>
/// <param name="Token">The pass's token, linked to the caller's where it supplied one.</param>
/// <param name="StartedAt">The <see cref="TimeProvider"/> timestamp the pass began at, which bounds how long a held coverage vouch may count the pass as a re-answer on its way.</param>
internal readonly record struct PassScope(
    PassKind Kind, int Version, CancellationToken Token, long StartedAt);
