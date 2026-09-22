namespace Formidable.Blazor;

/// <summary>The engine's verdict store: every rule's latest answer, held per executed set and served only to a selection that wholly contains the set.</summary>
/// <remarks>
/// Every member expects the renderer's dispatcher; the store carries no dispatch of its own.
/// Whether a pass is still the engine's current one is never checked here: the engine's version
/// gate stops a superseded pass's filing before it reaches
/// <see cref="TryFile(IReadOnlyList{SetVerdict}, int)"/>.
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

    /// <summary>The store's generation, captured as an evaluation begins so <see cref="TryFile(IReadOnlyList{SetVerdict}, int)"/> can refuse a filing made against a store cleared after that.</summary>
    internal int Generation => _generation;

    /// <summary>Plans an evaluation: the stored sets the selection wholly contains and that are fresh for it, and the rules no served set answers.</summary>
    /// <param name="selection">The rules the profile selects.</param>
    /// <param name="executeAll">Whether to serve nothing and execute the whole selection, as a submit does.</param>
    /// <param name="editStamp">The edit stamp the evaluation began at.</param>
    /// <param name="profile">The profile the evaluation runs under.</param>
    /// <returns>The sets to serve and the remainder to execute; under <paramref name="executeAll"/> the remainder is the whole selection.</returns>
    // The selection is the caller's: the validator seam that produces it stays with the engine,
    // so the store never holds a validator.
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

    /// <summary>Files the sets an evaluation executed, or refuses the whole batch when the store was cleared after the evaluation began.</summary>
    /// <param name="executed">The sets the evaluation ran; <see langword="null"/> or empty files nothing.</param>
    /// <param name="plannedGeneration">The <see cref="Generation"/> captured as the evaluation began.</param>
    /// <returns><see langword="true"/> when the batch was filed; <see langword="false"/> for an empty batch or a moved generation.</returns>
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

    /// <summary>The probe's filing gate: as the two-argument overload, and refused too when an edit arrived after the probe began.</summary>
    /// <param name="executed">The sets the probe ran; <see langword="null"/> or empty files nothing.</param>
    /// <param name="plannedGeneration">The <see cref="Generation"/> captured as the probe began.</param>
    /// <param name="plannedEditStamp">The edit stamp the probe began at.</param>
    /// <param name="currentEditStamp">The engine's edit stamp as the probe lands.</param>
    /// <returns><see langword="true"/> when the batch was filed.</returns>
    // Verdicts stamped for a model state no longer current would never be served, and filing
    // them could only displace fresher entries a pass landed meanwhile.
    internal bool TryFile(
        IReadOnlyList<SetVerdict>? executed,
        int plannedGeneration,
        int plannedEditStamp,
        int currentEditStamp)
    {
        return plannedEditStamp == currentEditStamp && TryFile(executed, plannedGeneration);
    }

    /// <summary>Empties the store and moves its generation, so an evaluation planned before the clear cannot file into it.</summary>
    internal void Clear()
    {
        _setVerdicts.Clear();
        _ruleToSet.Clear();
        _generation++;
    }

    /// <summary>Files one set verdict, dropping every stored set from another model state or sharing a rule with it.</summary>
    /// <param name="verdict">The set to file.</param>
    // Two sets holding one rule between them would let a plan serve that rule's issues twice.
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

/// <summary>What an evaluation may serve from the store, and what it must execute.</summary>
/// <param name="Reused">The stored sets the selection contains, each answering for every rule it holds.</param>
/// <param name="Remainder">The selected rules no reused set answers, in selection order.</param>
internal sealed record RulePlan(List<SetVerdict> Reused, List<RuleIdentity> Remainder);

/// <summary>One executed rule set's issues, with the edit stamp and profile that decide whether a later evaluation may serve it.</summary>
/// <param name="rules">The rules the set was executed for.</param>
/// <param name="issues">The issues they produced; empty when every rule passed.</param>
/// <param name="editStamp">The edit stamp the producing evaluation began at.</param>
/// <param name="isProfileScoped">Whether the execution consulted a child-scope decision that can differ across profiles (<see cref="RuleLevelResult.IsProfileScoped"/>), which confines the verdict to <paramref name="profile"/>.</param>
/// <param name="profile">The profile the producing evaluation ran under, compared by reference.</param>
// Held as one value because the parts mean nothing apart: issues with no record of the model
// state or the profile that produced them cannot be checked against anything. The profile is
// kept by reference because the options holding the profiles are settable.
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

    /// <summary>The edit stamp the producing evaluation began at.</summary>
    internal int EditStamp { get; } = editStamp;

    /// <summary>Whether the verdict answers only for <see cref="Profile"/>.</summary>
    internal bool IsProfileScoped { get; } = isProfileScoped;

    /// <summary>The profile the producing evaluation ran under.</summary>
    internal ValidationProfile Profile { get; } = profile;

    /// <summary>Whether the verdict may serve an evaluation that began at <paramref name="editStamp"/> under <paramref name="profile"/>.</summary>
    /// <param name="editStamp">The edit stamp the evaluation began at.</param>
    /// <param name="profile">The profile it runs under.</param>
    /// <returns><see langword="true"/> when the stamps agree and, for a profile-scoped verdict, <paramref name="profile"/> is the same instance as <see cref="Profile"/>.</returns>
    internal bool IsFreshFor(int editStamp, ValidationProfile profile) =>
        EditStamp == editStamp && (!IsProfileScoped || ReferenceEquals(Profile, profile));
}
