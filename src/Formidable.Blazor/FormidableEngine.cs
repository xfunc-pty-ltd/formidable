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
/// verdict lands, but no pass ever removes an entry, and submit leaves the set standing; both
/// _touched and _engagedFields, when the load pass's apply adopts the fields a page's freshly
/// loaded values have earned;
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
/// caller's context as each field change arrives — and as DiscloseLoadedValuesAsync begins,
/// which is a page stating that the model moved without one — while the store itself is read
/// on the dispatcher as a pass or a validity probe decides what is left to execute and written
/// only in a verdict landing — a pass's apply, version- and generation-gated, in the same
/// dispatch as the channel sources beside it, or the probe's own dispatch, generation-gated and
/// skipped when an edit has arrived since the probe began — and its generation mutates with the
/// rendered-field-set change, also on the
/// dispatcher. A pass in flight across such a change can therefore neither read a store that is
/// mutating under it nor write verdicts computed against a page that has since moved.
/// One thread-discipline fact spans every mechanism above: an await calls
/// ConfigureAwait(false) exactly when its own continuation is off the dispatcher, needing
/// neither the renderer's context nor any of the state above back. Every await in this file
/// and both of core's satisfies that except one, each re-entering the dispatcher explicitly
/// through _renderDispatch wherever its continuation goes on to touch that state. The one
/// exception sits inside a _renderDispatch delegate already, at the tail of
/// <see cref="DiscloseLoadedValuesAsync"/> — so it is not an await made off the dispatcher,
/// only one whose continuation stays on the context _renderDispatch just re-entered, which is
/// the point rather than an oversight. Blazor's kit components and its JS-backed services
/// keep the renderer's context throughout their own awaits instead, since those continuations
/// run in or beside component code that reads it.
/// </remarks>
public sealed class FormidableEngine<TModel> : IFormidableEngine, IValidatingFieldReader, IDisposable
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
    // can see unaided — the rendered field set moving — and DiscloseLoadedValuesAsync moves
    // this counter itself, because a page calling it is saying outright that the model now
    // holds values nothing notified for. Nothing covers the rest.
    private int _editStamp;

    // Every rule's most recent verdict, held one entry per executed SET rather than one per
    // rule: a pass runs the stale remainder of its selection in as few validator calls as its
    // rules' selection classes allow, and the issues one call produced answer for exactly the
    // set it was given. _ruleToSet indexes each rule to the set that answered it, so the
    // per-rule questions the engine asks — is this rule covered, and is that answer fresh —
    // stay a dictionary read. Reuse stays bidirectional and order-independent for a rule:
    // whichever pass ran it last at the current stamp has answered it for every later pass at
    // that stamp, whatever profile either ran, for as long as the whole set it was run in sits
    // within that later pass's own selection — which is what a selection-class partition
    // guarantees, since no profile can take part of a class. Filing a set drops every stored set
    // answering for a different model state and every one sharing a rule with it, and a
    // rendered-field-set change empties both structures, so their size stays bounded by rule
    // count.
    private readonly List<SetVerdict> _setVerdicts = [];
    private readonly Dictionary<RuleIdentity, SetVerdict> _ruleToSet = [];

    // Names the rendered field set the stored verdicts were computed against, the one staleness
    // the edit counter cannot see. OnRenderedFieldsChanged bumps it as it clears the store, and
    // a pass writes verdicts only while the generation it captured at its own begin still
    // stands — a pass in flight across a field-set change would otherwise put back, verbatim,
    // exactly what the clear just removed.
    private int _storeGeneration;

    // Moves whenever a source the submit-coverage read derives from moves — a pass ending
    // however it ends (see EndPass), a probe landing, the rendered-field-set clear — so the
    // answer below can be cached per state rather than recomputed per field per render. The
    // edit stamp is the cache key's other half; nothing else feeds the read.
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

    // The submit profile's presence demands, resolved to fields (see Requirements). Null until
    // first asked, and dropped whenever the profile instance or the rendered field set moves.
    private Dictionary<FieldIdentifier, FieldRequirement>? _requirements;
    private ValidationProfile? _requirementsProfile;

    // The last coverage answer that came out fresh, and the two coordinates it answers for. They
    // are the cache key above minus exactly one member — the coverage version — and ignoring that
    // one member is the whole of what holding an answer means. A rendered-field-set change empties
    // the verdict store and moves that version, which leaves the walk below nothing to read about
    // a model the edit stamp says has not moved; the held answer covers that window, until the
    // re-answer the change arms lands. An edit strands it only conditionally: while a re-answer is
    // demonstrably on its way (see ServeHeldCoverage's second route) the answer keeps serving the
    // fields the edit did not touch, and it stops the moment nothing is coming any more — the
    // serve condition is re-checked on every read — or the pass carrying the promise ends without
    // landing, which abandons it outright. A superseded pass is not itself a drop — it fails the
    // version guard the abandon sits behind — and the answer then stands or falls on whether its
    // displacer, or an arm, still promises a re-answer. The profile is part of the answer's
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

    // Whether the cached answer above was served from the held answer ACROSS an edit —
    // ServeHeldCoverage's second route. A marked answer needs two things an earned one does not:
    // the cache short-circuit re-checks on every read that a re-answer is still on its way, and
    // WouldPassSubmit withholds the vouch from the fields in _editedPastHold. Every recompute
    // clears it, so an earned answer is never re-checked.
    private bool _coverageServedAcrossEdit;

    // The fields edited since the held answer was computed — added beside the edit stamp as each
    // field change arrives, cleared whenever a fresh compute holds a new answer. While an answer
    // is served across an edit these are the fields it cannot speak for: the edits invalidated
    // exactly their values, so they paint as they would with no hold anywhere — which is what
    // keeps a just-emptied required field from wearing green on the strength of a value it no
    // longer holds. Never pruned on field departure, deliberately: excluding a departed field
    // affects nothing rendered, an identity that returns was genuinely edited and stays honestly
    // excluded, and every fresh hold clears the set whole anyway.
    private readonly HashSet<FieldIdentifier> _editedPastHold = [];

    // How long a pass in flight counts as "a re-answer on its way". A backstop rather than a
    // knob: it only ever decides anything on a form whose pass has hung — where the held field
    // shows no pending indicator, since the indicator scopes to the edited fields — and a hung
    // form should lose its confirmation borders rather than keep them for ever. Generous on
    // purpose: a rule slower than this loses the held green early, the conservative direction.
    // The bound is the current pass's age, never the held answer's: each edit against a
    // validator that hangs again starts a fresh pass, so the same, ever-staler answer can be
    // re-served for another bound per edit — broken-form territory by design, and the edited
    // fields themselves are excluded throughout.
    private static readonly TimeSpan HeldVouchBound = TimeSpan.FromSeconds(30);

    // The capability-less coverage source: the edit stamp at which the last whole-model
    // SubmitProfile evaluation this source takes — a COMPLETED submit, refresh or load pass, or a
    // fallback probe — began, and the fields its report failed. A live pass is excluded by kind
    // rather than by the profile it ran: LiveProfile decides that, and a narrowed one answers for
    // fewer rules than a submit would. A validator with no rule-level seam has no verdicts to
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
    /// <remarks>
    /// The two shipped roots — <see cref="FormidableForm{TModel}"/> and
    /// <see cref="FormidableValidator{TModel}"/> — are the only supported hosts. Direct
    /// construction is supported for tests against the engine alone: a hand-assembled root
    /// never learns of rendered-field-set changes, because the reconciliation and ordering
    /// seams are internal, wired by the shipped hosts, and this constructor subscribes only to
    /// the edit context's field-changed notification — so verdicts for a removed row outlive
    /// it, departed fields are never pruned, and issues keep validator order.
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

        _store = new ValidationMessageStore(editContext);
        _fieldChangedHandler = HandleFieldChanged;
        editContext.OnFieldChanged += _fieldChangedHandler;
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

    /// <summary>
    /// The submit profile's presence demands, resolved to fields — built on first ask and kept
    /// until the submit profile instance changes or the rendered field set moves. Asking the
    /// validator for one field's answer costs a walk of its declared rules, and this is asked
    /// once per bound component per render, so the walk happens once for the form instead of
    /// once per field per render.
    /// <para>
    /// Keys are resolved through the same introspector that puts an ISSUE on a field, so a
    /// demand lands on exactly the field the failure it describes would land on — which is the
    /// whole point of resolving rather than comparing names. A templated path resolves per ROW —
    /// expanded against the collection the model holds before resolution, so each row's field
    /// carries its own entry under the row's own identifier. That resolution reads the model
    /// graph, so it is dropped when the rendered field set moves, for the same reason the
    /// verdict store is: a change to what is on the page can carry a change to the objects
    /// behind it, with no field-changed notification anywhere. What that keeps current is this
    /// map and nothing else — a component asks with the identifier it resolved when it last
    /// bound, so a rebuild that re-files a nested member under a replaced owner leaves an
    /// unmoved component asking under the old one. That divergence is the documented limit on
    /// <see cref="IFormidableEngine.GetFieldRequirement"/>, and it errs towards claiming
    /// nothing.
    /// </para>
    /// <para>
    /// A selection that throws (a typo'd ruleset name) answers "nothing found" rather than
    /// taking the render down — the same call <see cref="EnsureSubmitCoverageCurrent"/> makes
    /// one screen up, and for the same reason: the next pass surfaces that exception through
    /// its own fault policy, which is where a configuration error belongs. Only entries that
    /// demand something are kept, so a lookup miss and
    /// <see cref="FieldRequirement.NotRequired"/> are the same answer.
    /// </para>
    /// </summary>
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
    /// validator's coverage is the last whole-model SubmitProfile evaluation a submit, a refresh,
    /// a load or a probe completed, current exactly while its begin stamp is still the current
    /// edit stamp — a live pass is excluded by kind, whatever profile it ran. The rule-capable
    /// coverage can also be a HELD answer: a rendered-field-set change empties the store while the
    /// edit stamp says the model those verdicts described has not moved, and
    /// <see cref="ServeHeldCoverage"/> covers that gap, so green describes the model rather than
    /// the page's registration churn — and it covers the gap an edit itself opens, while the pass
    /// re-answering that edit is demonstrably on its way, so one field's edit does not withdraw
    /// every other field's confirmation for the debounce window plus the rules' flight. An
    /// answer served across an edit cannot speak for the edited fields themselves — the edits
    /// invalidated exactly their values — so those are excluded here and paint as they would
    /// with no hold anywhere. The fallback needs no cover of its own — its source is not
    /// the store, and a field-set change leaves it exactly as current as the edit stamp already
    /// found it.
    /// </summary>
    private bool WouldPassSubmit(FieldIdentifier field)
    {
        EnsureSubmitCoverageCurrent();
        return _coverageFresh
            && (_coverageErrorFields is null || !_coverageErrorFields.Contains(field))
            && (!_coverageServedAcrossEdit || !_editedPastHold.Contains(field));
    }

    /// <summary>
    /// Recomputes the cached submit-coverage answer when the edit stamp, a coverage source, or
    /// the submit profile has moved since it was last computed — or when the cached answer was
    /// served across an edit and the re-answer it stands on is no longer on its way — once per
    /// state change rather than once per field per render, since the walk selects rules and
    /// resolves the failing verdicts' issues to fields. A selection that throws (a typo'd
    /// ruleset name, say) reads as stale coverage rather than taking the render down: the next
    /// pass surfaces the same exception through its own fault policy, which is where a
    /// configuration error belongs.
    /// Nothing held stands in for that one: a selection that cannot be walked leaves nothing
    /// able to vouch for anything.
    /// </summary>
    private void EnsureSubmitCoverageCurrent()
    {
        var profile = _options.SubmitProfile;
        if (_coverageCacheEditStamp == _editStamp
            && _coverageCacheVersion == _coverageVersion
            && ReferenceEquals(_coverageCacheProfile, profile)
            // An answer served across an edit stands only while the re-answer it was served on
            // the promise of is still on its way; nothing re-keys this cache while a pass merely
            // hangs, so the promise is re-checked at the read itself. Once a recompute lands the
            // flag is clear and the short-circuit is unconditional again.
            && (!_coverageServedAcrossEdit || ReAnswerOnItsWay()))
        {
            return;
        }

        _coverageCacheEditStamp = _editStamp;
        _coverageCacheVersion = _coverageVersion;
        _coverageCacheProfile = profile;
        _coverageFresh = false;
        _coverageErrorFields = null;
        _coverageServedAcrossEdit = false;

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
            var plan = BuildRulePlan(ruleLevel, profile, executeAll: false, _editStamp);
            if (plan.Remainder.Count > 0)
            {
                // A rule with no current answer. The held answer stands in for it on either of
                // two grounds — a rendered-field-set change emptied the store without moving
                // the edit stamp, so an answer computed at that stamp is one nothing since has
                // invalidated; or an edit moved the stamp while the pass re-answering it is
                // demonstrably on its way, in which case the held answer serves every field
                // the edit did not touch. Otherwise coverage is stale and its fields are moot.
                ServeHeldCoverage(profile);
                return;
            }

            foreach (var stored in plan.Reused)
            {
                foreach (var issue in stored.Issues)
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
    /// Holding also empties the edited-field record: the answer being held is current, so no
    /// field has been edited past it yet.
    /// </summary>
    private void HoldCoverage(ValidationProfile profile)
    {
        _heldCoverageStamp = _editStamp;
        _heldCoverageProfile = profile;
        _heldCoverageErrorFields = _coverageErrorFields;
        _editedPastHold.Clear();
    }

    /// <summary>
    /// Serves the held answer in place of a recomputed one, on either of two grounds. The first:
    /// the verdicts it was derived from are gone while the model it describes is not — a
    /// rendered-field-set change empties the store and never moves the edit stamp, so matching
    /// that stamp is exactly the condition "nothing but the rendered field set has changed since
    /// this was computed", and the change that emptied the store also arms the pass that replaces
    /// the held answer with an earned one. The second: an edit moved the stamp past the held
    /// answer while a re-answer is demonstrably on its way (<see cref="ReAnswerOnItsWay"/>) —
    /// without this, one field's edit withdraws every other field's confirmation for the whole
    /// gap between the edit and the pass's landing, a debounce window plus the rules' flight. An
    /// answer served on the second ground is marked as such, and two things keep the mark honest:
    /// <see cref="WouldPassSubmit"/> excludes the fields edited since the hold, so the edited
    /// field itself paints exactly as it would with no hold anywhere, and
    /// <see cref="EnsureSubmitCoverageCurrent"/> re-checks the serve condition on every read of
    /// the marked answer, so a pass that hangs past <see cref="HeldVouchBound"/> — or a promise
    /// that evaporates — loses the vouch at the next read rather than keeping it for ever. The
    /// profile match guards both grounds: an answer about one selection of rules never vouches
    /// for another. What a served answer can be wrong about is bounded the same way it always
    /// was: it answers from the model state it was computed against until the pass behind it
    /// lands, the lag <see cref="IsFormValid"/> has always carried, on the same terms.
    /// </summary>
    private void ServeHeldCoverage(ValidationProfile profile)
    {
        if (!ReferenceEquals(_heldCoverageProfile, profile))
        {
            return;
        }

        if (_heldCoverageStamp == _editStamp)
        {
            _coverageFresh = true;
            _coverageErrorFields = _heldCoverageErrorFields;
            _coverageServedAcrossEdit = false;
            return;
        }

        if (!ReAnswerOnItsWay())
        {
            return;
        }

        _coverageFresh = true;
        _coverageErrorFields = _heldCoverageErrorFields;
        _coverageServedAcrossEdit = true;
    }

    /// <summary>
    /// Whether a re-answer of the submit-selected coverage is demonstrably on its way. A pass in
    /// flight is asked first, and its age before anything else: past <see cref="HeldVouchBound"/>
    /// nothing counts, not even what is armed behind it. A hung submit, live or load pass defers
    /// every armed refresh for as long as it hangs, and a hung refresh is not deferred to but
    /// displaced by the next refresh fire, which begins a pass with a bound of its own; either
    /// way, what is armed cannot stand in for a pass past the bound. Within the bound, a pass
    /// whose landing answers the submit selection is the re-answer itself — submit, refresh and
    /// load passes run that profile by construction, and a live pass does when the live channel
    /// resolves to the submit profile instance, compared by reference like every other consumer
    /// of that resolution. A live pass under a narrowed channel answers for fewer rules, so it is
    /// not the re-answer; it defers instead to whatever is armed behind it, which is how a
    /// post-submit refresh sitting behind a narrow pass keeps the vouch through that pass's
    /// flight and then fires the moment it ends. With no pass in flight — or a narrowed one
    /// deferring — the scheduled arms decide: an open live-debounce window counts only when
    /// the live channel resolves to the submit profile, and an armed post-submit refresh counts
    /// on its own, running the submit profile by construction. Both count only under a debounce
    /// that can actually fire: <see cref="Timeout.InfiniteTimeSpan"/> arms a timer that never
    /// does — the documented spelling for turning the refresh off, and the live window's
    /// degenerate never-closing width — so an accumulator standing behind one promises nothing,
    /// however many fields it holds. The options are read here, at each ask, per their
    /// read-at-each-use contract. This is the serve-across-an-edit condition
    /// <see cref="ServeHeldCoverage"/> asks, and the standing condition
    /// <see cref="EnsureSubmitCoverageCurrent"/> re-asks on every read of an answer so served.
    /// The validity probe is deliberately not consulted — it
    /// is fire-and-forget, with no descriptor to read a start time from — which leaves one
    /// configuration blinking on an edit exactly as a form with nothing scheduled does: a
    /// narrowed live channel with <see cref="FormidableOptions.TrackFormValidity"/> on, before
    /// any submit, re-answers only through the probe, and its vouch waits for that landing.
    /// </summary>
    private bool ReAnswerOnItsWay()
    {
        var liveAnswersSubmit = ReferenceEquals(
            _options.LiveProfile ?? _options.SubmitProfile, _options.SubmitProfile);

        if (_currentPass is { } pass)
        {
            // A pass past the bound stops vouching whatever is scheduled behind it: a hung
            // submit, live or load pass defers every armed refresh indefinitely
            // (RunRefreshPassAsync re-arms behind exactly those kinds), and a hung refresh is
            // displaced by the next fire rather than deferred to, beginning a pass with a bound
            // of its own — so at this read the arms below cannot stand in for the pass past it.
            if (_timeProvider.GetElapsedTime(pass.StartedAt) >= HeldVouchBound)
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

    /// <summary>
    /// Drops the held coverage answer outright, so nothing serves it again until a fresh compute
    /// holds a new one. Two things reach for it: a current pass ending without landing — the
    /// re-answer any served vouch was standing on the promise of is gone, and retraction must
    /// not wait out <see cref="HeldVouchBound"/> or a still-armed window — and the whole-model
    /// adoption <see cref="DiscloseLoadedValuesAsync"/> opens with, which declares the model
    /// moved out from under everything the hold describes; its own pass re-answers and re-holds.
    /// Nulling the profile is what closes both serve routes: a stamp of -1 never matches, and
    /// no profile ever compares equal to none.
    /// </summary>
    private void AbandonHeldCoverage()
    {
        _heldCoverageStamp = -1;
        _heldCoverageProfile = null;
        _heldCoverageErrorFields = null;
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
    /// for a submit, the one pass the visitor asked for; scoped to the field(s) that triggered a
    /// live pass (one for an immediate
    /// edit, every field an open <see cref="FormidableOptions.LiveDebounce"/> window accumulated
    /// for a debounced one), scoped to the fields edited within the debounce window for a refresh
    /// pass, and scoped to nothing at all for the pass
    /// <see cref="DiscloseLoadedValuesAsync"/> runs, which no field is waiting on.
    /// <see cref="GetFieldState"/> folds this into
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

    /// <inheritdoc cref="IValidatingFieldReader.InlineMessageLive"/>
    string? IValidatingFieldReader.InlineMessageLive => _options.InlineMessageLive;

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

    /// <summary>The defensive gate's form-level issue, synthesized by the channel views whenever
    /// <see cref="GateActive"/> holds: a blocked submit disclosed nothing and nothing on screen
    /// explains the block, so this one model-level explanation stands in for the errors the user
    /// cannot see. Built afresh from <see cref="FormidableOptions.DefensiveGateMessage"/> at each
    /// read, which is what lets a page change the sentence between one surface's read and the
    /// next.</summary>
    private ValidationIssue GateIssue => new(string.Empty, _options.DefensiveGateMessage);

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
        HashSet<(FieldIdentifier Field, string Message)>? showing,
        FieldIdentifier field,
        ValidationIssue issue) => showing?.Add((field, issue.Message));

    /// <summary>Field-keyed sibling of <see cref="ExceptShadowed(List{ValidationIssue}, HashSet{string})"/> for the whole-form read,
    /// where one set spans every field rather than one set existing per field.</summary>
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
    /// the form's history: it recomputes the submit channel's answer against the model as it
    /// stands, and is the only pass a field-set change can start for itself, and it is owed twice
    /// over — once for that channel, and once because the emptied store leaves the Valid class's
    /// vouch nothing of its own to read.
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
        _setVerdicts.Clear();
        _ruleToSet.Clear();
        _storeGeneration++;
        // The emptied store answers for nothing, so the coverage read re-derives — or, while the
        // edit stamp says the model it described still stands, holds the answer it last gave.
        _coverageVersion++;

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
        _editedPastHold.Add(e.FieldIdentifier);

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

    /// <summary>
    /// Whether the pass currently in flight is a live pass — true from the moment a live pass
    /// becomes the current one until it ends, and false again as soon as any newer pass takes that
    /// place from it.
    /// </summary>
    private bool LiveInFlight => _currentPass?.Kind == PassKind.Live;

    /// <summary>
    /// Whether the pass currently in flight is a submit. Live and refresh passes both read this and
    /// stand down — submit is the higher-intent operation, and neither ever supersedes it. The
    /// load pass is the one kind that does not read it: like a submit it is started by a caller and
    /// awaited, so two of them overlapping resolve the way two submits do, with the later one
    /// taking the descriptor.
    /// </summary>
    private bool SubmitInFlight => _currentPass?.Kind == PassKind.Submit;

    /// <summary>
    /// Whether the pass currently in flight is a refresh — read only by
    /// <see cref="RunDebouncedLivePassAsync"/>, which stands down for it. An IMMEDIATE live pass
    /// never reads this, and what it supersedes decides why. Once a submit has run, the edit that
    /// causes the live pass also re-arms the refresh <see cref="ScheduleRefresh"/> schedules, so
    /// cancelling an in-flight refresh costs nothing there. Before a submit the refresh in flight
    /// belongs to a field-set change and no edit re-arms it, so what refills the submit-selected
    /// coverage the Valid class rests on is whatever answers that profile next: the live pass
    /// itself wherever <see cref="FormidableOptions.LiveProfile"/> resolves to the submit profile,
    /// selecting exactly those rules, or the probe beside it under
    /// <see cref="FormidableOptions.TrackFormValidity"/>. Narrow the one and leave the other off
    /// and nothing here refills it: that coverage waits for a submit or the next field-set change,
    /// and the Valid class waits with it. A DEBOUNCED live pass is different — the edit that will
    /// eventually supersede the refresh already happened when the debounce window opened, so
    /// nothing else re-arms it if this cancels it outright; deferring here is what keeps the
    /// refresh's own verdict from going permanently stale.
    /// </summary>
    private bool RefreshInFlight => _currentPass?.Kind == PassKind.Refresh;

    /// <summary>
    /// Whether the pass currently in flight is the one
    /// <see cref="DiscloseLoadedValuesAsync"/> runs. Live and refresh passes stand down for it
    /// on the same grounds they stand down for a submit: it is awaited by the caller, and its
    /// verdict decides disclosure rather than merely refreshing it, so a pass that superseded it
    /// would leave the values a page just loaded neither vouched for nor spoken about.
    /// </summary>
    private bool LoadInFlight => _currentPass?.Kind == PassKind.Load;

    /// <summary>
    /// Cancels and disposes any in-flight pass's <see cref="CancellationTokenSource"/>, then starts a
    /// new one linked to <paramref name="external"/> and records the new pass as the current one.
    /// Only one pass, whatever its kind, is ever in flight at a time — starting a new one
    /// supersedes whatever came before, which is exactly what taking over the descriptor means.
    /// </summary>
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

    /// <summary>
    /// Retires the pass in flight: the validating flag, the field scope narrowing it, and the
    /// descriptor naming the pass all clear together, so nothing that runs afterwards can read a
    /// pass that has already ended. The caller establishes that the pass is still the current one —
    /// the verdict dispatch does that with its own version guard, <see cref="SetValidating"/> with
    /// its. Ending a pass also moves the coverage version, whatever the outcome: a landing can
    /// have written verdicts the coverage read derives from, live passes included, and a pass
    /// that ends WITHOUT landing can no longer be the re-answer a held vouch was being served on
    /// the promise of — either way the next coverage read must re-decide rather than stand on a
    /// cache that predates the end.
    /// </summary>
    private void EndPass()
    {
        IsValidating = false;
        _validatingScope = null;
        _currentPass = null;
        _coverageVersion++;
    }

    /// <summary>
    /// Flips <see cref="IsValidating"/> and notifies, marshaled through <c>_renderDispatch</c> so
    /// the flip lands on the renderer's dispatcher rather than on whatever thread completed the pass.
    /// The write is skipped when <paramref name="pass"/> is no longer the current one —
    /// a superseded pass must not stomp a newer pass's state. <paramref name="fields"/> narrows which
    /// fields <see cref="GetFieldState"/> reports as validating: a live pass passes the field(s)
    /// that triggered it — one field for an immediate edit, every field an open live-debounce
    /// window accumulated for a debounced one; a refresh pass passes the fields edited within its
    /// debounce window; the pass <see cref="DiscloseLoadedValuesAsync"/> runs passes an EMPTY
    /// set, since no field is waiting on it; and a submit passes <see langword="null"/>
    /// (form-wide, every field), being the pass the visitor asked for. Only
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
                    // A current pass clearing the flag here ended without landing, however it
                    // came to — a fault and a caller's cancellation are the everyday routes —
                    // so the re-answer any held vouch is being served on the promise of is
                    // gone, and retraction must be immediate rather than bound-delayed: EndPass
                    // moves the coverage version out from under the cache, and abandoning the
                    // held answer keeps the serve route from handing it straight back out for a
                    // still-armed window or the bound's remainder. A pass that landed ended at
                    // its verdict dispatch and never reaches this branch, and a superseded pass
                    // fails the version guard above, so neither costs a hold anything here.
                    AbandonHeldCoverage();
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
    /// The kinds differ in what they hand in, not in how they run — a skeleton
    /// hand-rolled per kind is one where a single copy can quietly stop raising a notification, or
    /// stop clearing a flag, that the others still do. How the validation step itself runs is
    /// a capability split: a validator that can validate rule by rule gets the verdict store —
    /// only the rules with no fresh verdict at this pass's stamp execute, and the report handed
    /// downstream is ASSEMBLED, every selected rule's issues whether served from the store or
    /// just executed, so downstream never sees less than a whole-profile answer. Any other
    /// validator gets the whole profile in one call — correct, unoptimised. A pass whose every
    /// selected rule is fresh executes nothing and still runs this entire lifecycle, publishing
    /// its assembled verdict like any other.
    /// </summary>
    /// <param name="kind">
    /// Which lifecycle this is; it also decides the fault policy below, and whether freshness is
    /// consulted at all — a submit runs its full selection by fiat, repopulating the store on
    /// the way through.
    /// </param>
    /// <param name="profile">The profile the model is validated under.</param>
    /// <param name="external">
    /// The caller's own cancellation token, linked into the pass. Only the kinds a caller starts
    /// and awaits have one — a submit and a load; live and refresh pass
    /// <see cref="CancellationToken.None"/>, which is what lets one cancellation filter serve every
    /// kind: with no external token there is nothing a cancellation can mean except supersession by
    /// a newer pass.
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
            List<SetVerdict>? executed = null;
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
                    RulePlan plan = null!;
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
                if (executed is not null && generation == _storeGeneration)
                {
                    foreach (var verdict in executed)
                    {
                        StoreSetVerdict(verdict);
                    }
                }

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
                    _lastSubmitAnswerStamp = editStamp;
                    _lastSubmitAnswerErrorFields = [.. _submitVerdictErrors.Keys];
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
                await SetValidating(false, pass).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Decides what a capability evaluation has left to execute: the stored sets it may serve
    /// from, and the rules of the profile's selection none of them answers. A stored set is
    /// servable only where the current selection CONTAINS it, because a set's issues belong to
    /// the set as a whole and there is no per-rule attribution to strip the ones a narrower
    /// profile does not select — so a set answers a selection it is part of and never one it
    /// straddles. A submit sets <paramref name="executeAll"/> and serves nothing by fiat: it is
    /// the disclosure event, and its full run is also what repopulates the store so everything
    /// behind it starts from answered rules; every other kind, and the validity probe, consults
    /// freshness. Runs on the dispatcher (the caller marshals), because the store is read here
    /// and mutates only there.
    /// </summary>
    private RulePlan BuildRulePlan(
        IRuleLevelValidator<TModel> ruleLevel,
        ValidationProfile profile,
        bool executeAll,
        int editStamp)
    {
        var selection = ruleLevel.SelectRules(profile);
        var reused = new List<SetVerdict>();
        HashSet<RuleIdentity>? covered = null;

        if (!executeAll && _setVerdicts.Count > 0)
        {
            var selected = new HashSet<RuleIdentity>(selection);
            covered = [];
            foreach (var rule in selection)
            {
                if (covered.Contains(rule)
                    || !_ruleToSet.TryGetValue(rule, out var stored)
                    || !stored.IsFreshFor(editStamp, profile)
                    || !stored.Rules.IsSubsetOf(selected)
                    || stored.Rules.Overlaps(covered))
                {
                    continue;
                }

                reused.Add(stored);
                covered.UnionWith(stored.Rules);
            }
        }

        var remainder = new List<RuleIdentity>(selection.Count);
        foreach (var rule in selection)
        {
            if (covered is null || !covered.Contains(rule))
            {
                remainder.Add(rule);
            }
        }

        return new RulePlan(reused, remainder);
    }

    /// <summary>
    /// Runs a capability pass's plan: the rules no stored set answers are executed under the
    /// pass's own token, one call per SELECTION CLASS — the partition the validator says no
    /// profile can split. Running the whole remainder as one set instead would file a verdict a
    /// narrower profile could never serve from, since its issues mix in rules that profile does
    /// not select, so the two passes over one edit would each run the rules they share; the
    /// partition costs one validator call per class and keeps a set servable to whichever
    /// selection contains it. The report downstream consumes is assembled from the sets served
    /// and the sets just run. The groups' total size is compared with the plan's remainder and
    /// any mismatch throws, whether the groups drop a rule or repeat one: the groups are the only
    /// thing that runs, so a dropped identity is a rule that never executes under a report
    /// carrying no sign of it, and a repeated one is a rule executed twice whose issues report
    /// twice. Failing on either is the posture the seam takes toward an identity it cannot
    /// resolve, though only the drop is a wrong answer — a repeat by itself costs the second
    /// execution. The comparison is of counts rather than sets, so a drop and a repeat that
    /// cancel in the total pass it. The verdicts for what actually executed are returned
    /// beside it, stamped with the pass's begin stamp, for the version-and-generation-gated
    /// apply to write; nothing here touches the store itself, so a superseded or faulted pass's
    /// work simply evaporates with its locals.
    /// </summary>
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

    /// <summary>
    /// Files a set verdict, dropping every stored set it supersedes: one that answers for an
    /// older model state, and one that shares a rule with it, since two sets holding one rule
    /// between them would let a plan serve that rule's issues twice. Runs on the dispatcher,
    /// like every other store mutation.
    /// </summary>
    private void StoreSetVerdict(SetVerdict verdict)
    {
        for (var i = _setVerdicts.Count - 1; i >= 0; i--)
        {
            var stored = _setVerdicts[i];
            if (stored.EditStamp != verdict.EditStamp || stored.Rules.Overlaps(verdict.Rules))
            {
                _setVerdicts.RemoveAt(i);
                foreach (var rule in stored.Rules)
                {
                    if (_ruleToSet.TryGetValue(rule, out var owner) && ReferenceEquals(owner, stored))
                    {
                        _ruleToSet.Remove(rule);
                    }
                }
            }
        }

        _setVerdicts.Add(verdict);
        foreach (var rule in verdict.Rules)
        {
            _ruleToSet[rule] = verdict;
        }
    }

    /// <summary>
    /// The single fault policy behind every pass that reports rather than rethrows: a form-level
    /// issue saying the verdict is incomplete, written only while <paramref name="pass"/> is still
    /// the current one, then <see cref="ValidationFaulted"/> for a host that wants to log it. The
    /// event is raised either way — a superseded pass's exception still happened. The issue's
    /// sentence is read from <see cref="FormidableOptions.ValidationFaultMessage"/> here, at the
    /// one moment it is filed, so the stored issue keeps the wording in force when it was written.
    /// </summary>
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
    /// overwrite its answer — see <see cref="AdoptFormValidity"/> for why a submit, and every
    /// other whole-model kind, can adopt directly instead of merely invalidating. A probe never
    /// starts while a submit —
    /// or the pass <see cref="DiscloseLoadedValuesAsync"/> runs — is already in flight, for the
    /// same reason a live pass never does (see <see cref="RunLivePassAsync"/>): either one is
    /// about to compute this exact quantity itself moments from now, so racing it buys nothing.
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
        var generation = _storeGeneration;
        var profile = _options.SubmitProfile;

        var ruleLevel = _validator as IRuleLevelValidator<TModel>;
        var ruleCapable = ruleLevel is not null && ruleLevel.CanValidateByRule;

        ValidationReport report;
        List<SetVerdict>? executed = null;
        try
        {
            if (ruleCapable)
            {
                // The same dispatch discipline the pass skeleton uses: the store is read on the
                // dispatcher, where it mutates. A selection error surfaces through the fault
                // policy below, exactly as a whole-profile validation would surface it.
                RulePlan plan = null!;
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
            ValidationFaulted?.Invoke(this, new FormidableValidationFaultedEventArgs(exception));
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
                foreach (var verdict in executed)
                {
                    StoreSetVerdict(verdict);
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
    /// directly into <see cref="IsFormValid"/> — called from the verdict applies of every pass
    /// that answers the whole model under that profile (submit, refresh, load), each of which
    /// already computes exactly this quantity as part of its own pass, so
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

    private FieldIdentifier Resolve(ValidationIssue issue) => ResolvePath(issue.Path);

    /// <summary>
    /// Resolves a validator-declared path against the live model graph — the one place an issue's
    /// path and a rule's declared path become the same kind of answer, so a demand and the failure
    /// it describes can never land on different fields.
    /// </summary>
    private FieldIdentifier ResolvePath(string path) =>
        _introspector.Resolve(_model, path).ToFieldIdentifier(_model, path);

    /// <summary>
    /// The one report a form whose validator cannot report its own rules gets: a Trace line for a
    /// debugger, and a logged line when a logger was supplied — the same dual channel
    /// <see cref="ReportSuppressed"/> uses, at Information rather than Warning, because a
    /// validator that cannot be inspected is a supported configuration and the form goes on
    /// validating correctly through it. What that costs is invisible on the page, which is the
    /// whole reason for saying it somewhere: nothing is marked required from the rules, so a
    /// required indicator and <c>aria-required</c> reach only the fields
    /// <see cref="FormidableOptions.RequiredOverride"/> declares required, which is asked ahead
    /// of the rules on every read, and a <c>DiscloseLoadedValuesAsync</c> load confirms nothing.
    /// The line names the state rather than the mistake, because the capability test reads the
    /// same for a wrapper that dropped the capability as for a validator that never had one.
    /// </summary>
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

    /// <summary>
    /// The report a suppressed issue gets where one is made at all: a Trace line for a debugger, a
    /// logged warning for the host (WebAssembly's default provider is the browser console, so that
    /// channel needs no wiring to be seen), and the options callback for a page that wants to show
    /// its own list. Two sites route here — a submit, for the errors its reveal ledger leaves
    /// undisclosed, and <see cref="ApplyServerIssues(IEnumerable{ValidationIssue})"/>, for a
    /// response advisory its visibility answer hides, whether nothing renders its field or an
    /// explicit <see cref="FormidableOptions.DisclosureOverride"/> says no — and routing both
    /// through one place is what keeps the three channels from drifting apart between them. An
    /// issue dropped for want of somewhere to render it anywhere ELSE is dropped in silence, among
    /// them a submit's own non-visible advisories (filtered out of the advisory projection), a live
    /// verdict's issues under <see cref="LiveIssueDisclosure.EngagedAndVisible"/> (filtered by
    /// <see cref="LiveViewOf"/>, a read-time view), and a server ERROR an explicit
    /// <see cref="FormidableOptions.DisclosureOverride"/> hides — the one severity for which an
    /// override answer of no is decided before this and never reaches it. A never-registered field
    /// additionally reaches <see cref="FormidableOptions.NeverRegisteredFieldDiagnostic"/> — the
    /// general channels above fire either way, unchanged.
    /// </summary>
    private void ReportSuppressed(ValidationIssue issue)
    {
        var path = DiagnosticPathSanitizer.ForDiagnostic(issue.Path);
        System.Diagnostics.Trace.WriteLine(
            $"Formidable: issue at '{path}' is suppressed - no rendered field registration matches and no disclosure override applies.");
        _logger?.LogWarning(
            "Formidable: issue at '{Path}' is suppressed - no rendered field registration matches and no disclosure override applies.",
            path);
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
        field.Equals(ModelLevelField) || Registry.IsRegistered(field);

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
    /// Splits an already field-grouped set of issues into its error and non-error projections,
    /// each field's list keeping its original relative order. A severity filter and a field
    /// filter commute over one fixed issue order, so this reproduces exactly what grouping the
    /// report's errors and its non-error remainder separately by resolved field would — without
    /// resolving any issue a second time.
    /// </summary>
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

    private void NotifyStateChanged() => StateChanged?.Invoke(this, FormidableStateChangedEventArgs.Empty);

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

            if (!forField.Any(i => i.Message == issue.Message && i.Severity == issue.Severity))
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
        AbandonHeldCoverage();

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

    /// <summary>
    /// Decides what the values already in the model have earned, and marks the fields it decides
    /// for as touched and engaged. Three outcomes per field, and the middle one is the point: a
    /// field holding a value the profile's rules do not fail is vouched for, a field holding a
    /// value they DO fail is engaged so the failure discloses, and a field holding no value is
    /// left entirely alone — nobody has reached it yet, and nagging about every unfilled required
    /// field the moment a form loads is what engagement-gating exists to prevent.
    /// </summary>
    /// <remarks>
    /// The split between "wrong" and "not reached yet" is the VALUE, never which kind of rule
    /// failed. A rule's kind cannot carry it: presence written as
    /// <c>Must(s =&gt; !string.IsNullOrWhiteSpace(s))</c> is indistinguishable from a range check,
    /// and reading it as one paints an untouched field red the moment a form loads. The value
    /// answers directly, and it answers for a collection row as readily as for a top-level
    /// member, because a row's own value is as readable as any other.
    /// <para>
    /// Empty means what <c>NotEmpty()</c> means, read against the type the member DECLARES — see
    /// <see cref="IsEmptyValue"/>. That reading is what keeps a saved <c>false</c> in a
    /// <c>bool?</c> a real answer while a never-assigned <c>bool</c> is not. Where the rules are
    /// FluentValidation's own it is also THEIR reading, so a field this leaves alone is one a
    /// <c>NotEmpty()</c> on it would have failed; a validator written some other way is judged
    /// by the same definition without being asked to agree with it.
    /// </para>
    /// <para>
    /// The universe is every field with an error-severity failure, plus every field the
    /// validator declares a rule for
    /// (<see cref="IRuleInspectingValidator{TModel}.GetDeclaredFieldPaths"/>, expanded against
    /// the rows the model actually holds). The two halves need different things: disclosing a
    /// wrong value needs only the model, so a validator with no inspection capability still
    /// discloses one, while vouching for a good value needs the validator's own list of the
    /// fields it speaks about — nothing else can tell a field whose rules all passed from a
    /// field no rule mentions.
    /// </para>
    /// <para>
    /// Only error severity makes a value wrong. A warning or an info is a remark about a value
    /// that is otherwise acceptable, and a field carrying one is vouched for like any other —
    /// the advisory then shows through the live channel and paints its own state class, which is
    /// what the same value typed by hand would do.
    /// </para>
    /// <para>
    /// Where the value cannot be read, nothing is claimed: an unresolvable intermediate
    /// (<c>Address.City</c> where <c>Address</c> is null), a model-level failure, which carries
    /// no member name at all, and a path naming no member — a server-sent path, say — leave the
    /// field untouched and unengaged. That direction is chosen deliberately, and it is the same
    /// direction an empty value takes: silence for those fields is what the form does anyway
    /// until this is called.
    /// </para>
    /// </remarks>
    /// <param name="errors">
    /// The pass's error-severity issues, already grouped onto their fields — the same grouping
    /// the submit channel's client source is built from, so the two cannot describe different
    /// fields.
    /// </param>
    /// <param name="profile">The profile whose declared fields are the vouching half's universe.</param>
    /// <returns>The fields this adopted, for the live pass that discloses them to answer.</returns>
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

    /// <summary>
    /// The paths the validator declares for the profile, with every collection template expanded
    /// against the rows the model actually holds — <c>Attendees[].Name</c> becomes one path per
    /// attendee, and none at all for an empty list.
    /// </summary>
    /// <remarks>
    /// A validator that cannot be inspected, and a selection that throws (a typo'd ruleset name),
    /// both answer with nothing rather than taking the load down: the pass beside this one
    /// surfaces that exception through its own fault policy, which is where a configuration error
    /// belongs.
    /// </remarks>
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

    /// <summary>
    /// Replaces the first open index in <paramref name="template"/> with each index the model's
    /// own collection carries, and recurses — so a template with two of them
    /// (<c>Teams[].Members[].Alias</c>) yields one path per member of every team.
    /// </summary>
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

    /// <summary>
    /// How many elements the collection at <paramref name="path"/> holds — zero where the path
    /// resolves to nothing, to a non-collection, or to a collection with no elements, all of
    /// which mean the same thing here: there are no rows to expand a template into.
    /// </summary>
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

    /// <summary>
    /// Whether the value is an absence rather than an answer, read exactly as FluentValidation's
    /// own <c>NotEmpty()</c> reads it: null, a blank or whitespace-only string, a sequence with
    /// no elements, or the default of the type the member is DECLARED as.
    /// </summary>
    /// <remarks>
    /// The declared type is what makes the reading faithful, and it is the whole reason the value
    /// is read through <see cref="IModelIntrospector.TryReadValue"/> rather than boxed and
    /// inspected. A <c>bool</c> holding <see langword="false"/> is its own default and reads as
    /// an absence; a <c>bool?</c> holding <see langword="false"/> is not, and reads as the
    /// answer it is. The same split separates <c>int</c> from <c>int?</c>, an enum from a
    /// nullable enum, and <c>Guid</c>/<c>DateTime</c>/<c>decimal</c> from their nullable forms.
    /// <para>
    /// What is left ambiguous is exactly the set of non-nullable value types, and every one of
    /// them errs towards absence — the direction that stays silent rather than claiming
    /// something. The library's own statement follows from that: model an optional value as
    /// <c>T?</c> and a load reads it exactly; model it as <c>T</c> and its default reads as "not
    /// filled in".
    /// </para>
    /// </remarks>
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
    /// <see cref="RunRefreshPassAsync"/>'s own defer-then-snapshot shape — and only once no pass it
    /// stands down for is in flight does it snapshot and clear the fields accumulated since
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
                _submitVerdictAdvisories = GroupByResolvedField(
                    report.Issues.Where(i => i.Severity != ValidationSeverity.Error));
            }).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

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
/// Which of the engine's lifecycles a pass is running. The kind is what the engine's own
/// deference rules are written in — a refresh stands down for a live pass, and both stand down
/// for the two kinds a caller starts and awaits, a submit and a load — and it is also what
/// decides whether a validator's exception is reported as form state or left to the caller
/// awaiting the pass.
/// </summary>
internal enum PassKind
{
    /// <summary>One field's edit, revalidating the model under the live profile.</summary>
    Live,

    /// <summary>The form-wide pass a submit runs, under the submit profile.</summary>
    Submit,

    /// <summary>The debounced post-submit revalidation, also under the submit profile.</summary>
    Refresh,

    /// <summary>
    /// The whole-model submit-profile answer <see cref="FormidableEngine{TModel}.DiscloseLoadedValuesAsync"/>
    /// runs to decide what a page's freshly loaded values have earned. Its own kind rather than a
    /// refresh, because the deference rules are written in kinds: nothing an EDIT starts may
    /// supersede it, since its verdict is the only thing that engages the fields it speaks for.
    /// Another pass a caller starts and awaits still can — a submit, or a second load — the way
    /// two submits resolve (see <see cref="FormidableEngine{TModel}.SubmitInFlight"/>).
    /// </summary>
    Load,
}

/// <summary>
/// The identity a running pass carries: what it is, the version that decides whether it is still
/// the current pass, and the token it was started with. A pass is superseded — never waited for —
/// so every write it makes has to be gated on the version still being the engine's own.
/// </summary>
/// <param name="Kind">Which lifecycle this pass is running.</param>
/// <param name="Version">The engine version this pass took when it began.</param>
/// <param name="Token">The pass's cancellation token, linked to whatever the caller supplied.</param>
/// <param name="StartedAt">The timestamp the pass began at, from the engine's own
/// <see cref="TimeProvider"/> — what bounds how long a held coverage vouch may treat this pass
/// as the re-answer on its way.</param>
internal readonly record struct PassScope(
    PassKind Kind, int Version, CancellationToken Token, long StartedAt);

/// <summary>What one pass may serve from the store, and what it is left to execute.</summary>
/// <param name="Reused">The stored sets the current selection contains, each answering for every
/// rule it holds.</param>
/// <param name="Remainder">The selected rules no reused set answers, in declaration order.</param>
internal sealed record RulePlan(List<SetVerdict> Reused, List<RuleIdentity> Remainder);

/// <summary>
/// One executed SET of rules' answer, together with everything that decides whether a later pass
/// may serve it instead of running those rules again. Held as one value because the parts are
/// meaningless apart: issues with no idea which model state or which profile produced them
/// cannot be checked against anything. The issues belong to the set as a whole rather than to any
/// one member, which is why no per-rule attribution is needed and why a set answers only for a
/// selection that contains it: each rule's failures land in exactly one set's report.
/// </summary>
/// <param name="rules">The rules the set was executed for.</param>
/// <param name="issues">The issues they produced — empty when they all passed, which is as much a
/// fact worth reusing as a failure is.</param>
/// <param name="editStamp">The engine's edit count as the producing pass began — the model state
/// this verdict answers for.</param>
/// <param name="isProfileScoped">Whether the execution consulted a child-scope decision that can
/// differ across profiles (see <see cref="RuleLevelResult.IsProfileScoped"/>) — when it did, the
/// verdict answers only for <paramref name="profile"/> and an honest store re-runs the set for
/// any other.</param>
/// <param name="profile">The profile the producing pass ran under, remembered by reference
/// because the options holding the profiles are settable.</param>
internal sealed class SetVerdict(
    HashSet<RuleIdentity> rules,
    IReadOnlyList<ValidationIssue> issues,
    int editStamp,
    bool isProfileScoped,
    ValidationProfile profile)
{
    /// <summary>The rules this verdict answers for.</summary>
    internal HashSet<RuleIdentity> Rules { get; } = rules;

    /// <summary>The issues the set produced.</summary>
    internal IReadOnlyList<ValidationIssue> Issues { get; } = issues;

    /// <summary>The edit stamp the producing pass began at.</summary>
    internal int EditStamp { get; } = editStamp;

    /// <summary>Whether the verdict answers only for <see cref="Profile"/>.</summary>
    internal bool IsProfileScoped { get; } = isProfileScoped;

    /// <summary>The profile the producing pass ran under.</summary>
    internal ValidationProfile Profile { get; } = profile;

    /// <summary>
    /// Whether this verdict may be served to a pass that read <paramref name="editStamp"/> at
    /// its beginning and runs <paramref name="profile"/>: the stamps must agree, and a
    /// profile-scoped verdict additionally answers only for the very profile it ran under.
    /// </summary>
    /// <remarks>
    /// The stamp comparison says only that nothing has told the engine the model moved since —
    /// which is what "the model is unchanged" means to an engine that is told about changes. Two
    /// things tell it: a field-changed notification, and
    /// <see cref="FormidableEngine{TModel}.DiscloseLoadedValuesAsync"/>, which moves the stamp
    /// itself precisely because the values it is about arrived without one. A mutation made
    /// without either is invisible to it.
    /// </remarks>
    internal bool IsFreshFor(int editStamp, ValidationProfile profile) =>
        EditStamp == editStamp && (!IsProfileScoped || ReferenceEquals(Profile, profile));
}
