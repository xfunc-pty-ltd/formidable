namespace Formidable.Blazor;

/// <summary>Per-field state exposed to field components for CSS and pending-UI decisions.</summary>
/// <param name="IsTouched">The user has interacted with the field (marked by field components).</param>
/// <param name="IsModified">The EditContext reports the field as modified.</param>
/// <param name="IsValidating">
/// A validation pass involving this field is in flight — scoped to the changed field for live
/// passes, scoped to whichever fields were edited within its debounce window for refresh
/// passes, form-wide for submit passes.
/// </param>
/// <param name="HasErrors">The field currently has error-severity messages.</param>
/// <param name="HasWarnings">The field currently has warning-severity issues.</param>
/// <param name="HasInfos">The field currently has info-severity issues.</param>
/// <param name="WouldPassSubmit">
/// The engine can vouch that a submit would not fail this field: every rule the submit profile
/// selects has an answer current at the model's edit stamp, and none of those answers carries an
/// error-severity issue for this field. This is the conjunct that gates the Valid class (see
/// <see cref="FormidableCss.Compute"/>) — "no disclosed issues" alone cannot mean "would pass",
/// because submit-selected rules that have not run for the value as it stands may yet reject it.
/// Freshness is judged form-level, deliberately: which fields a PASSING rule speaks for is
/// unknowable, so per-field freshness attribution does not exist and the form-wide answer is the
/// honest one. A rendered field set that moves discards those answers without moving the edit
/// stamp, and the answer computed at that stamp is held until the pass the move arms replaces it:
/// this vouches for the model, not for the page's registration churn. Defaults to
/// <see langword="true"/> so a state built without an engine — a test double, a hand-rolled
/// provider — keeps the Valid tier reachable.
/// </param>
public readonly record struct FieldState(
    bool IsTouched,
    bool IsModified,
    bool IsValidating,
    bool HasErrors,
    bool HasWarnings,
    bool HasInfos,
    bool WouldPassSubmit = true);
