namespace Formidable.Blazor;

/// <summary>
/// Every rule's most recent verdict, held one entry per executed SET rather than one per rule:
/// a pass runs the stale remainder of its selection in as few validator calls as its rules'
/// selection classes allow, and the issues one call produced answer for exactly the set it was
/// given. An index maps each rule to the set that answered it, so the per-rule questions the
/// engine asks — is this rule covered, and is that answer fresh — stay a dictionary read.
/// Reuse stays bidirectional and order-independent for a rule: whichever pass ran it last at
/// the current stamp has answered it for every later pass at that stamp, whatever profile
/// either ran, for as long as the whole set it was run in sits within that later pass's own
/// selection — which is what a selection-class partition guarantees, since no profile can take
/// part of a class. Filing a set drops every stored set answering for a different model state
/// and every one sharing a rule with it, and <see cref="Clear"/> empties both structures, so
/// their size stays bounded by rule count.
/// </summary>
/// <remarks>
/// The threading doctrine is the caller's to keep, deliberately: this type carries no dispatch
/// machinery, and every member expects the renderer's dispatcher. The engine's edit stamp
/// mutates synchronously on the caller's context as each field change arrives — and as
/// <see cref="FormidableEngine{TModel}.DiscloseLoadedValuesAsync"/> begins, which is a page
/// stating that the model moved without one — and reaches this type only as arguments, while
/// the store itself is read on the dispatcher — by a pass or a validity probe deciding what is
/// left to execute, and by the submit-coverage tracker deciding mid-render whether a field's
/// confirmation still has answers behind it — and written only in a verdict landing: a pass's
/// apply, version- and generation-gated, in the same dispatch as the channel sources beside it,
/// or the probe's own dispatch, generation-gated and skipped when an edit has arrived since the
/// probe began. The generation mutates with the rendered-field-set change's
/// <see cref="Clear"/>, also on the dispatcher. A pass in flight across such a change can
/// therefore neither read a store that is mutating under it nor write verdicts computed against
/// a page that has since moved. One gate stays outside on purpose: whether a pass is still the
/// engine's current one is pass bookkeeping this type never sees, so a superseded pass's apply
/// stops at the engine's version gate before any call lands here.
/// </remarks>
internal sealed class SetVerdictStore
{
    private readonly List<SetVerdict> _setVerdicts = [];
    private readonly Dictionary<RuleIdentity, SetVerdict> _ruleToSet = [];

    // Names the rendered field set the stored verdicts were computed against, the one staleness
    // the edit counter cannot see. Clear bumps it as it empties the store — the engine clears
    // on a rendered-field-set change — and a landing writes verdicts only while the generation
    // its evaluation captured at its own begin still stands (the TryFile gate): a pass in
    // flight across a field-set change would otherwise put back, verbatim, exactly what the
    // clear just removed.
    private int _generation;

    /// <summary>
    /// The current store generation. A caller captures it as its evaluation begins and hands it
    /// back to <see cref="TryFile(IReadOnlyList{SetVerdict}, int)"/>, which refuses a write the
    /// store was cleared under.
    /// </summary>
    internal int Generation => _generation;

    /// <summary>
    /// Decides what a capability evaluation has left to execute: the stored sets it may serve
    /// from, and the rules of <paramref name="selection"/> none of them answers. A stored set
    /// is servable only where the current selection CONTAINS it, because a set's issues belong
    /// to the set as a whole and there is no per-rule attribution to strip the ones a narrower
    /// profile does not select — so a set answers a selection it is part of and never one it
    /// straddles. A submit sets <paramref name="executeAll"/> and serves nothing by fiat: it is
    /// the disclosure event, and its full run is also what repopulates the store so everything
    /// behind it starts from answered rules; every other kind, and the validity probe, consults
    /// freshness. Runs on the dispatcher (the caller marshals), because the store is read here
    /// and mutates only there. The selection itself is the caller's: the validator seam that
    /// produces it stays with the engine, so this type never holds a validator.
    /// </summary>
    internal RulePlan Plan(
        IReadOnlyList<RuleIdentity> selection,
        bool executeAll,
        int editStamp,
        ValidationProfile profile)
    {
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
    /// Files a landed evaluation's verdicts, or refuses the batch whole. Nothing is written
    /// unless <paramref name="plannedGeneration"/> still names the current generation — a
    /// landing that crossed a rendered-field-set change would otherwise put back exactly what
    /// the change's clear removed — and a null or empty batch is no landing at all. Returns
    /// whether anything was filed.
    /// </summary>
    internal bool TryFile(IReadOnlyList<SetVerdict>? executed, int plannedGeneration)
    {
        if (executed is not { Count: > 0 } || plannedGeneration != _generation)
        {
            return false;
        }

        foreach (var verdict in executed)
        {
            File(verdict);
        }

        return true;
    }

    /// <summary>
    /// The validity probe's write gate: everything the two-argument overload refuses, plus the
    /// stricter conjunct only a probe needs — the batch is refused when
    /// <paramref name="plannedEditStamp"/> no longer equals
    /// <paramref name="currentEditStamp"/>, because verdicts stamped for a model state that is
    /// no longer current would never be served, and writing them could only displace fresher
    /// entries a pass landed meanwhile. A pass's apply carries no such conjunct: against a
    /// fresher landing from another pass, the engine's version gate already stops the stale
    /// apply — the fresher pass moved the version at its own begin — and against a fresher
    /// landing from a probe, which moves no version, the stale pass write stands and may
    /// displace the probe's newer entries through the filing rules. That displacement is at
    /// worst conservative: a displaced rule is left with no stored answer, or with the older
    /// one the landing filed — neither is served, so it re-executes at the next plan.
    /// </summary>
    internal bool TryFile(
        IReadOnlyList<SetVerdict>? executed,
        int plannedGeneration,
        int plannedEditStamp,
        int currentEditStamp)
    {
        return plannedEditStamp == currentEditStamp && TryFile(executed, plannedGeneration);
    }

    /// <summary>
    /// Empties the store whole and moves the generation with it, so a landing planned against
    /// the pre-clear store refuses to write: the clear cannot be undone by work that predates
    /// it. The engine calls this on a rendered-field-set change — the one staleness the edit
    /// stamp cannot see.
    /// </summary>
    internal void Clear()
    {
        _setVerdicts.Clear();
        _ruleToSet.Clear();
        _generation++;
    }

    /// <summary>
    /// Files a set verdict, dropping every stored set it supersedes: one that answers for an
    /// older model state, and one that shares a rule with it, since two sets holding one rule
    /// between them would let a plan serve that rule's issues twice. Runs on the dispatcher
    /// like every other store mutation — the discipline the type remarks put on the caller.
    /// </summary>
    private void File(SetVerdict verdict)
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
}

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
