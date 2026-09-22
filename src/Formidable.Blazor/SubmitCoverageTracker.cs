using Microsoft.AspNetCore.Components.Forms;

namespace Formidable.Blazor;

/// <summary>The vouch behind the valid state class: whether the submit rules have answered for the model as it stands, which fields they fail, and the held answer served while the store is empty or an edit's re-answer is in flight.</summary>
/// <remarks>
/// Every member expects the renderer's context, and none locks or dispatches. Reading is what
/// mutates: a <see cref="WouldPassSubmit"/> ask whose coordinates moved recomputes the cached
/// answer, the held answer and the edited-field record in place, on the render path that asked.
/// </remarks>
/// <param name="store">The verdict store the rule walk plans against, through <see cref="SetVerdictStore.Plan"/> alone.</param>
/// <param name="reAnswerOnItsWay">Whether a re-answer of the submit-selected coverage is demonstrably on its way: the engine's <see cref="FormidableEngine{TModel}.ReAnswerOnItsWay"/>, which reads scheduler state the tracker never holds.</param>
/// <param name="resolve">Resolves an issue to the field it lands on: the engine's own resolution, so a coverage error lands on the field its issue does.</param>
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

    /// <summary>How long a pass in flight counts as a re-answer on its way for the held vouch: thirty seconds of the pass's own age.</summary>
    // A backstop rather than a knob: it decides anything only on a form whose pass has hung,
    // where the held field shows no pending indicator (the indicator scopes to the edited
    // fields), and a hung form should lose its confirmation borders rather than keep them for
    // ever.
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

    /// <summary>Whether <paramref name="field"/> would pass a submit: the submit rules have answered for the model as it stands, none fails the field, and the field was not edited past a held answer being served.</summary>
    /// <param name="field">The field the state class is being computed for.</param>
    /// <param name="editStamp">The current edit stamp.</param>
    /// <param name="profile">The submit profile.</param>
    /// <param name="selectRules">The validator's rule selection, or <see langword="null"/> for a validator that cannot validate rule by rule.</param>
    /// <returns><see langword="true"/> when the engine can vouch for the field; <see langword="false"/> when coverage is stale, an answer fails the field, or the field was edited past the held answer being served.</returns>
    // The ask's coordinates arrive from the engine at each ask, which keeps the options' and the
    // capability's read-at-each-use contracts the engine's to honour.
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

    /// <summary>Recomputes the cached coverage answer when the stamp, the version or the profile moved, or a held answer served across an edit finds no re-answer on its way; a throwing selection reads as stale, nothing held standing in.</summary>
    /// <param name="editStamp">The current edit stamp.</param>
    /// <param name="profile">The submit profile.</param>
    /// <param name="selectRules">The rule selection, or <see langword="null"/> for a validator without the rule-level seam.</param>
    // Once per state change rather than once per field per render: the walk selects rules and
    // resolves the failing verdicts' issues to fields. A throwing selection (a mistyped ruleset
    // name) is left for the next pass to surface through its own fault policy, which is where a
    // configuration error belongs.
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

    /// <summary>Holds the fresh answer just computed, with its stamp and profile, and clears the edited-past-hold record.</summary>
    /// <param name="editStamp">The edit stamp the answer is for.</param>
    /// <param name="profile">The profile it was selected under.</param>
    // Called only where the answer came out fresh: a stale read is not an answer, and holding one
    // would vouch for nothing. The record empties because the held answer is current, so no field
    // has been edited past it yet.
    private void HoldCoverage(int editStamp, ValidationProfile profile)
    {
        _heldCoverageStamp = editStamp;
        _heldCoverageProfile = profile;
        _heldCoverageErrorFields = _coverageErrorFields;
        _editedPastHold.Clear();
    }

    /// <summary>Serves the held answer in place of a recomputed one, either because only the rendered field set changed or because a re-answer of the edit is on its way.</summary>
    /// <param name="editStamp">The current edit stamp.</param>
    /// <param name="profile">The submit profile, which must be the held answer's instance.</param>
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

    /// <summary>Records a field edited past the held answer, which an answer served across the edit then cannot vouch for; called as each field-changed notification arrives.</summary>
    /// <param name="field">The field the committed change named.</param>
    internal void NoteEdit(FieldIdentifier field) => _editedPastHold.Add(field);

    /// <summary>Marks that a coverage source moved (a pass ending, a probe landing that filed or recorded, the field-set clear), so the next read recomputes.</summary>
    internal void MoveVersion() => _coverageVersion++;

    /// <summary>Drops the held answer, so nothing serves it until a fresh compute holds a new one.</summary>
    // Nulling the profile closes both serve routes: a stamp of -1 never matches, and no profile
    // compares equal to none.
    internal void Abandon()
    {
        _heldCoverageStamp = -1;
        _heldCoverageProfile = null;
        _heldCoverageErrorFields = null;
    }

    /// <summary>Records a completed whole-model submit-profile evaluation as the coverage source for a validator that cannot run rule by rule.</summary>
    /// <param name="beginStamp">The edit stamp the evaluation began at.</param>
    /// <param name="errorFields">The fields its report failed.</param>
    internal void RecordWholeModelAnswer(int beginStamp, HashSet<FieldIdentifier> errorFields)
    {
        _lastSubmitAnswerStamp = beginStamp;
        _lastSubmitAnswerErrorFields = errorFields;
    }
}
