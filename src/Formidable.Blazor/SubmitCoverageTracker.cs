using Microsoft.AspNetCore.Components.Forms;

namespace Formidable.Blazor;

/// <summary>
/// The submit-coverage vouch behind the Valid state class: one derived, cached answer to
/// whether every submit-selected rule has answered for the model as it stands and which fields
/// those answers fail, the held copy of the last fresh answer that keeps a confirmation border
/// steady across the gaps the sources open, and the whole-model record that answers the same
/// question for a validator with no rule-level seam. The engine asks through
/// <see cref="WouldPassSubmit"/>, handing in the ask's own coordinates, and feeds the narrow
/// writes from its own sites; scheduler state stays deliberately outside — whether a re-answer
/// is on its way arrives as the constructor's predicate, asked fresh exactly where the serve
/// doctrine requires.
/// </summary>
/// <remarks>
/// Fully synchronous, with no locking and no dispatch, deliberately: the threading doctrine is
/// the caller's to keep, and its distinctive half is that reading is what mutates — a
/// <see cref="WouldPassSubmit"/> ask whose coordinates have moved recomputes the cache, the
/// held answer and the edited-field record in place, on the render path that asked. Every
/// member therefore expects the renderer's context: the engine reads here from its render-path
/// field state and feeds every write from a site already on that context — its own class
/// remarks carry the site-by-site inventory.
/// </remarks>
/// <param name="store">
/// The set-verdict store the rule walk plans against — <see cref="SetVerdictStore.Plan"/> is
/// the only member consumed, so the store's structures stay its own.
/// </param>
/// <param name="reAnswerOnItsWay">
/// Whether a re-answer of the submit-selected coverage is demonstrably on its way — the
/// engine's <see cref="FormidableEngine{TModel}.ReAnswerOnItsWay"/>, injected because it reads
/// pass and debounce scheduler state the tracker never holds.
/// </param>
/// <param name="resolve">
/// Resolves an issue to the field it lands on, against the live model graph — the engine's own
/// resolution, so a coverage error and the issue it came from can never land on different
/// fields.
/// </param>
internal sealed class SubmitCoverageTracker(
    SetVerdictStore store,
    Func<bool> reAnswerOnItsWay,
    Func<ValidationIssue, FieldIdentifier> resolve)
{
    private readonly SetVerdictStore _store = store;
    private readonly Func<bool> _reAnswerOnItsWay = reAnswerOnItsWay;
    private readonly Func<ValidationIssue, FieldIdentifier> _resolve = resolve;

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

    /// <summary>
    /// How long a pass in flight counts as "a re-answer on its way". A backstop rather than a
    /// knob: it only ever decides anything on a form whose pass has hung — where the held field
    /// shows no pending indicator, since the indicator scopes to the edited fields — and a hung
    /// form should lose its confirmation borders rather than keep them for ever. Generous on
    /// purpose: a rule slower than this loses the held green early, the conservative direction.
    /// The bound is the current pass's age, never the held answer's: each edit against a
    /// validator that hangs again starts a fresh pass, so the same, ever-staler answer can be
    /// re-served for another bound per edit — broken-form territory by design, and the edited
    /// fields themselves are excluded throughout.
    /// </summary>
    internal static readonly TimeSpan HeldVouchBound = TimeSpan.FromSeconds(30);

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
    /// found it. The ask's coordinates — the current edit stamp, the submit profile, and the
    /// rule selection, <see langword="null"/> for a validator that cannot validate rule by
    /// rule — arrive from the engine at each ask, which is what keeps the options' and the
    /// capability's read-at-each-use contracts the engine's to honour.
    /// </summary>
    internal bool WouldPassSubmit(
        FieldIdentifier field,
        int editStamp,
        ValidationProfile profile,
        Func<ValidationProfile, IReadOnlyList<RuleIdentity>>? selectRules)
    {
        EnsureSubmitCoverageCurrent(editStamp, profile, selectRules);
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
    private void EnsureSubmitCoverageCurrent(
        int editStamp,
        ValidationProfile profile,
        Func<ValidationProfile, IReadOnlyList<RuleIdentity>>? selectRules)
    {
        if (_coverageCacheEditStamp == editStamp
            && _coverageCacheVersion == _coverageVersion
            && ReferenceEquals(_coverageCacheProfile, profile)
            // An answer served across an edit stands only while the re-answer it was served on
            // the promise of is still on its way; nothing re-keys this cache while a pass merely
            // hangs, so the promise is re-checked at the read itself. Once a recompute lands the
            // flag is clear and the short-circuit is unconditional again.
            && (!_coverageServedAcrossEdit || _reAnswerOnItsWay()))
        {
            return;
        }

        _coverageCacheEditStamp = editStamp;
        _coverageCacheVersion = _coverageVersion;
        _coverageCacheProfile = profile;
        _coverageFresh = false;
        _coverageErrorFields = null;
        _coverageServedAcrossEdit = false;

        if (selectRules is null)
        {
            if (_lastSubmitAnswerStamp == editStamp)
            {
                _coverageFresh = true;
                _coverageErrorFields = _lastSubmitAnswerErrorFields.Count > 0
                    ? _lastSubmitAnswerErrorFields
                    : null;
                HoldCoverage(editStamp, profile);
            }

            return;
        }

        HashSet<FieldIdentifier>? errorFields = null;
        try
        {
            var plan = _store.Plan(
                selectRules(profile), executeAll: false, editStamp, profile);
            if (plan.Remainder.Count > 0)
            {
                // A rule with no current answer. The held answer stands in for it on either of
                // two grounds — a rendered-field-set change emptied the store without moving
                // the edit stamp, so an answer computed at that stamp is one nothing since has
                // invalidated; or an edit moved the stamp while the pass re-answering it is
                // demonstrably on its way, in which case the held answer serves every field
                // the edit did not touch. Otherwise coverage is stale and its fields are moot.
                ServeHeldCoverage(editStamp, profile);
                return;
            }

            foreach (var stored in plan.Reused)
            {
                foreach (var issue in stored.Issues)
                {
                    if (issue.Severity == ValidationSeverity.Error)
                    {
                        (errorFields ??= []).Add(_resolve(issue));
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
        HoldCoverage(editStamp, profile);
    }

    /// <summary>
    /// Holds the answer <see cref="EnsureSubmitCoverageCurrent"/> has just computed, together
    /// with the edit stamp and profile it answers for. Called only where the answer came out
    /// fresh: a stale read is not an answer, and holding one would be vouching for nothing.
    /// Holding also empties the edited-field record: the answer being held is current, so no
    /// field has been edited past it yet.
    /// </summary>
    private void HoldCoverage(int editStamp, ValidationProfile profile)
    {
        _heldCoverageStamp = editStamp;
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
    /// answer while a re-answer is demonstrably on its way
    /// (<see cref="FormidableEngine{TModel}.ReAnswerOnItsWay"/>, whose answer the constructor
    /// injects) — without this, one field's edit withdraws every other field's confirmation for
    /// the whole gap between the edit and the pass's landing, a debounce window plus the rules'
    /// flight. An answer served on the second ground is marked as such, and two things keep the
    /// mark honest: <see cref="WouldPassSubmit"/> excludes the fields edited since the hold, so
    /// the edited field itself paints exactly as it would with no hold anywhere, and
    /// <see cref="EnsureSubmitCoverageCurrent"/> re-checks the serve condition on every read of
    /// the marked answer, so a pass that hangs past <see cref="HeldVouchBound"/> — or a promise
    /// that evaporates — loses the vouch at the next read rather than keeping it for ever. The
    /// profile match guards both grounds: an answer about one selection of rules never vouches
    /// for another. What a served answer can be wrong about is bounded the same way it always
    /// was: it answers from the model state it was computed against until the pass behind it
    /// lands, the lag <see cref="IFormidableEngine.IsFormValid"/> has always carried, on the
    /// same terms.
    /// </summary>
    private void ServeHeldCoverage(int editStamp, ValidationProfile profile)
    {
        if (!ReferenceEquals(_heldCoverageProfile, profile))
        {
            return;
        }

        if (_heldCoverageStamp == editStamp)
        {
            _coverageFresh = true;
            _coverageErrorFields = _heldCoverageErrorFields;
            _coverageServedAcrossEdit = false;
            return;
        }

        if (!_reAnswerOnItsWay())
        {
            return;
        }

        _coverageFresh = true;
        _coverageErrorFields = _heldCoverageErrorFields;
        _coverageServedAcrossEdit = true;
    }

    /// <summary>
    /// Records a field a committed change has just moved past the held answer: while an answer
    /// is served across an edit, these are exactly the fields it cannot vouch for. The engine
    /// calls this beside its own edit-stamp bump, as each field-changed notification arrives.
    /// </summary>
    internal void NoteEdit(FieldIdentifier field) => _editedPastHold.Add(field);

    /// <summary>
    /// Moves the coverage version: a source the coverage read derives from has moved — a pass
    /// ending however it ends, a probe landing that filed or recorded anything, the
    /// rendered-field-set clear — so the next read re-decides rather than standing on a cache
    /// that predates the change.
    /// </summary>
    internal void MoveVersion() => _coverageVersion++;

    /// <summary>
    /// Drops the held coverage answer outright, so nothing serves it again until a fresh compute
    /// holds a new one. Two things reach for it: a current pass ending without landing — the
    /// re-answer any served vouch was standing on the promise of is gone, and retraction must
    /// not wait out <see cref="HeldVouchBound"/> or a still-armed window — and the whole-model
    /// adoption <see cref="FormidableEngine{TModel}.DiscloseLoadedValuesAsync"/> opens with,
    /// which declares the model moved out from under everything the hold describes; its own
    /// pass re-answers and re-holds.
    /// Nulling the profile is what closes both serve routes: a stamp of -1 never matches, and
    /// no profile ever compares equal to none.
    /// </summary>
    internal void Abandon()
    {
        _heldCoverageStamp = -1;
        _heldCoverageProfile = null;
        _heldCoverageErrorFields = null;
    }

    /// <summary>
    /// Records a completed whole-model SubmitProfile evaluation — the edit stamp it began at
    /// and the fields its report failed — as the capability-less coverage source. Which
    /// landings qualify is the caller's decision, made where the pass kinds live: the engine
    /// records a submit, refresh or load pass's apply and the fallback probe's landing, never a
    /// live pass, which is excluded by kind rather than by the profile it ran.
    /// </summary>
    internal void RecordWholeModelAnswer(int beginStamp, HashSet<FieldIdentifier> errorFields)
    {
        _lastSubmitAnswerStamp = beginStamp;
        _lastSubmitAnswerErrorFields = errorFields;
    }
}
