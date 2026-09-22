using Microsoft.AspNetCore.Components.Forms;

namespace Formidable.Blazor;

/// <summary>Non-generic engine view consumed by components and the cascaded form context.</summary>
public interface IFormValidationEngine
{
    /// <summary>The edit context this engine writes messages to.</summary>
    EditContext EditContext { get; }

    /// <summary>The disclosure registry rendered fields register with.</summary>
    FieldRegistry Registry { get; }

    /// <summary>The options this engine was constructed with (css class names, profiles, debounce).</summary>
    FormidableOptions Options { get; }

    /// <summary>
    /// True while a validation pass is in flight — form-wide: true for any pass regardless of
    /// which field triggered it. Field-scoped consumers (per-field "checking..." indicators)
    /// should read <see cref="FieldState.IsValidating"/> via <see cref="GetFieldState"/> instead,
    /// which narrows to the triggering field during a live pass, or to the fields edited within
    /// its debounce window during a refresh pass.
    /// </summary>
    bool IsValidating { get; }

    /// <summary>True once the submit pipeline has run (and validation failed or succeeded).</summary>
    bool HasSubmitted { get; }

    /// <summary>
    /// Whether the form would currently pass <see cref="FormidableOptions.SubmitProfile"/> —
    /// meant for disable-submit scenarios. Meaningful only when
    /// <see cref="FormidableOptions.TrackFormValidity"/> is turned on; otherwise this always
    /// reads <see langword="false"/>, and even with tracking on it reads <see langword="false"/>
    /// until the engine's first probe completes. The probe that keeps this current runs the
    /// submit profile invisibly — no disclosure, no message-store write, no pending-indicator
    /// flip — so nothing about it is ever shown to the user. Tracking is opt-in because the
    /// probe adds a full-model validation on top of the ordinary live pass for every change; see
    /// <see cref="FormidableOptions.TrackFormValidity"/> for the cadence it runs at. This is a
    /// client-side answer only — it does not reflect issues a server applied through
    /// <see cref="ApplyServerIssues"/> — and, like the rest of the engine, it does not see a
    /// model mutation that never raises the <see cref="EditContext"/>'s field-changed
    /// notification.
    /// </summary>
    bool IsFormValid { get; }

    /// <summary>Raised whenever validation or field state changes.</summary>
    event Action? StateChanged;

    /// <summary>
    /// Raised when a validation pass fails with an unexpected exception (not cancellation). The
    /// engine also surfaces a generic model-level message; subscribe to log or customize.
    /// </summary>
    event Action<Exception>? ValidationFaulted;

    /// <summary>Current state for one field.</summary>
    FieldState GetFieldState(FieldIdentifier field);

    /// <summary>
    /// The field's current issues, any severity — submit-pass entries first, then live-pass
    /// entries not already present with the same message; includes the engine's fault issue
    /// for the model-level field.
    /// </summary>
    IReadOnlyList<ValidationIssue> GetIssues(FieldIdentifier field);

    /// <summary>
    /// All currently-visible issues across the form: the fault issue first (model-level),
    /// then submit-pass entries, then live-pass entries not already present for the same
    /// field with the same message.
    /// </summary>
    IReadOnlyList<VisibleIssue> GetVisibleIssues();

    /// <summary>Marks a field as touched (called by field components on user interaction).</summary>
    void MarkTouched(FieldIdentifier field);

    /// <summary>
    /// Runs the submit pipeline: validate with the submit profile, surface visible issues, record
    /// the submit-visible set. Call from the renderer's synchronization context (a Blazor event
    /// handler or <c>InvokeAsync</c>) — it mutates validation state and triggers renders.
    /// </summary>
    Task<SubmitOutcome> ValidateForSubmitAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies server-declared issues (e.g. from a 400 ValidationProblemDetails) as if they were
    /// submit results: the server's verdict applies at the severity it carries. Errors land on
    /// their fields and reach the EditContext's message store; warnings and infos land as
    /// advisories, which the reads above surface and the store — an error-only surface — does not.
    /// The payload is treated as the server's CURRENT verdict: each call replaces the issues added
    /// by the previous call, rather than accumulating with them, so re-submitting the same or a
    /// corrected payload does not duplicate inline messages. Client-sourced submit issues on the
    /// same fields are unaffected by a replace, and an advisory whose message a client rule already
    /// disclosed for the same field shows once, as the client's copy. Applied issues also persist
    /// until the next debounced refresh replaces the submit-visible state from the client
    /// validator's report; a server-only issue with no matching client rule clears on that refresh.
    /// Because the payload is treated as a submit result, applying one also sets
    /// <see cref="HasSubmitted"/> — a page whose only validation is server-side reaches the
    /// submitted state through this call alone. Call from the renderer's synchronization context (a
    /// Blazor event handler or <c>InvokeAsync</c>) — it mutates validation state and triggers
    /// renders. <paramref name="issues"/> is enumerated exactly once.
    /// </summary>
    /// <remarks>
    /// Errors bypass the field registry: the server judged what was actually submitted, so an error
    /// shows whether or not the client rendered its field, and only a disclosure override returning
    /// <see langword="false"/> hides one. Advisories defer to the registry exactly as the client's
    /// own do — one with no rendered field is not shown, and the suppressed-issue diagnostic reports
    /// it — because an advisory blocks nothing, so hiding one strands no verdict. Replace is
    /// value-equality-based: if a client-sourced issue on a field is value-identical to a server
    /// issue previously applied to that field, a subsequent replace may remove either of the two
    /// equal entries — the two are indistinguishable, so which one is removed is unspecified.
    /// </remarks>
    void ApplyServerIssues(IEnumerable<ValidationIssue> issues);
}
