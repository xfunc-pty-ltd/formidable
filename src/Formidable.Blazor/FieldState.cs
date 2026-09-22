namespace Formidable.Blazor;

/// <summary>Per-field state exposed to field components for CSS and pending-UI decisions.</summary>
/// <remarks>
/// Built with an object initializer over init-only properties — members added later arrive as
/// further init-only properties, leaving every construction site and the parameterless
/// constructor's binary shape intact, and are understood to fold into the record's synthesized
/// equality. Any construction that runs the parameterless constructor — <c>new FieldState()</c>,
/// with or without an object initializer — runs the property initializers;
/// <c>default(FieldState)</c> bypasses constructors and initializers entirely and zeroes every
/// member, <see cref="WouldPassSubmit"/> included.
/// </remarks>
public readonly record struct FieldState
{
    /// <summary>
    /// Creates a state with every flag clear except <see cref="WouldPassSubmit"/>, whose
    /// initializer this constructor runs.
    /// </summary>
    public FieldState()
    {
    }

    /// <summary>
    /// The field has been interacted with: marked by a field component on a blur or a commit, or by
    /// <see cref="IFormidableEngine.DiscloseLoadedValuesAsync"/> for a field whose loaded value it
    /// decided for.
    /// </summary>
    public bool IsTouched { get; init; }

    /// <summary>The EditContext reports the field as modified.</summary>
    public bool IsModified { get; init; }

    /// <summary>
    /// A validation pass involving this field is in flight — scoped to the changed field for live
    /// passes, scoped to whichever fields were edited within its debounce window for refresh
    /// passes, and form-wide for a submit, the pass the visitor asked for. The pass
    /// <see cref="IFormidableEngine.DiscloseLoadedValuesAsync"/> runs covers no field at all: it
    /// answers for the whole model, so <see cref="IFormidableEngine.IsValidating"/> reports it for
    /// a page-level spinner, but nobody asked for it and no field is waiting on it.
    /// </summary>
    public bool IsValidating { get; init; }

    /// <summary>The field currently has error-severity messages.</summary>
    public bool HasErrors { get; init; }

    /// <summary>The field currently has warning-severity issues.</summary>
    public bool HasWarnings { get; init; }

    /// <summary>The field currently has info-severity issues.</summary>
    public bool HasInfos { get; init; }

    /// <summary>
    /// The engine can vouch that a submit would not fail this field: every rule the submit profile
    /// selects has an answer current at the model's edit stamp, and none of those answers carries an
    /// error-severity issue for this field. This is the conjunct that gates the Valid class (see
    /// <see cref="FormidableCss.Compute"/>) — "no disclosed issues" alone cannot mean "would pass",
    /// because submit-selected rules that have not run for the value as it stands may yet reject it.
    /// Freshness is judged form-level, deliberately: which fields a PASSING rule speaks for is
    /// unknowable, so per-field freshness attribution does not exist and the form-wide answer is the
    /// honest one. A rendered field set that moves discards those answers without moving the edit
    /// stamp, and the answer computed at that stamp is held until the pass the move arms replaces it:
    /// this vouches for the model, not for the page's registration churn. Initialized to
    /// <see langword="true"/>, so a state built without an engine — a test double, a hand-rolled
    /// provider — keeps the Valid tier reachable; <c>default(FieldState)</c> never runs that
    /// initializer and zeroes this member with the rest, so a defaulted state cannot vouch — the
    /// safe direction for a state nobody built.
    /// </summary>
    public bool WouldPassSubmit { get; init; } = true;
}
