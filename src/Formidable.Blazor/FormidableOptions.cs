using Microsoft.AspNetCore.Components.Forms;

namespace Formidable.Blazor;

/// <summary>Engine configuration. Profiles are assigned meaning here — at the point of use.</summary>
public sealed class FormidableOptions
{
    /// <summary>Profile run on field changes. Defaults to <see cref="ValidationProfile.Draft"/>.</summary>
    public ValidationProfile LiveProfile { get; set; } = ValidationProfile.Draft;

    /// <summary>Profile run by the submit pipeline. Defaults to <see cref="ValidationProfile.Submit"/>.</summary>
    public ValidationProfile SubmitProfile { get; set; } = ValidationProfile.Submit;

    /// <summary>Debounce for the post-submit refresh. Defaults to 300 ms.</summary>
    public TimeSpan RefreshDebounce { get; set; } = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// Debounce for the live pass a field change triggers. Defaults to <see langword="null"/>,
    /// which runs the live pass immediately on every field change.
    /// When set, a field change arms a single timer instead of running the pass immediately; a
    /// further change within the window re-arms it rather than starting a second timer, and the
    /// pass runs once the window elapses with no further edit, scoped to every field changed
    /// since the window opened. The window is shared across fields rather than tracked per
    /// field — the same semantics <see cref="RefreshDebounce"/> already has for the post-submit
    /// refresh.
    /// </summary>
    public TimeSpan? LiveDebounce { get; set; }

    /// <summary>
    /// Opt-in whole-form validity probe for disable-submit scenarios. Defaults to
    /// <see langword="false"/> — off forever, never default-on: turning it on AT LEAST doubles
    /// the per-change validation work, and "doubles" is a floor, not a cap — <see cref="SubmitProfile"/>
    /// is a strict superset of <see cref="LiveProfile"/> (the default rules again, plus the whole
    /// Submit ruleset, where the more expensive async/server-shaped rules typically live), so this
    /// probe is usually the more expensive of the two revalidations, not an equal second half.
    /// When <see langword="true"/>, the engine keeps
    /// <see cref="IFormValidationEngine.IsFormValid"/> current with a standalone
    /// <see cref="SubmitProfile"/> validation that is not an engine pass: no disclosure, no
    /// message-store write, no pending-indicator flip — nothing about it is ever shown. The probe
    /// runs once at construction (so a pristine, untouched form still reports truthfully) and
    /// again on every field change afterwards, at the same cadence the live pass itself runs at —
    /// immediately per change, or once per window when <see cref="LiveDebounce"/> is also set.
    /// </summary>
    public bool TrackFormValidity { get; set; }

    /// <summary>
    /// Opt-in client-side normalization before a submit, mirroring the AspNetCore validation
    /// filters (which already call <see cref="INormalizableModel.Normalize"/> before validating
    /// a request body). Defaults to <see langword="false"/>. When <see langword="true"/> and the
    /// model implements <see cref="INormalizableModel"/>, the submit pass calls
    /// <c>model.Normalize()</c> in place before running <see cref="SubmitProfile"/>, so the
    /// profile validates the normalized values rather than whatever the user actually typed. The
    /// mutation happens before the pass runs, so the submit's own re-render repaints every bound
    /// input straight from the normalized model — a value normalization changed (trimmed,
    /// cleared, collapsed) visibly updates on screen with no extra wiring.
    /// </summary>
    public bool NormalizeOnSubmit { get; set; }

    /// <summary>
    /// Optional disclosure override. Return true to force an issue visible, false to force it
    /// suppressed, or null to defer to the field registry. Model-level issues (empty path) are
    /// always visible unless this returns false.
    /// </summary>
    public Func<ValidationIssue, bool?>? DisclosureOverride { get; set; }

    /// <summary>
    /// Invoked once per issue suppressed because no rendered field registration matched and no
    /// disclosure override applied — usually a missing wrapper or <c>FormidableFieldAnchor</c>.
    /// Two sites report here: a submit, for its own error-severity issues, and
    /// <c>ApplyServerIssues</c>, for the advisories in a server response (a server-declared error
    /// bypasses the registry rather than suppressing). A Trace-output warning is emitted regardless, and
    /// so is a logged warning when the host resolved an <c>ILoggerFactory</c> — WASM's default
    /// logging provider is the browser console, so that channel needs no wiring here to be seen.
    /// </summary>
    public Action<ValidationIssue>? SuppressedIssueDiagnostic { get; set; }

    /// <summary>
    /// Invoked additionally, alongside <see cref="SuppressedIssueDiagnostic"/>, when a suppressed
    /// issue's field has no registration history at all — it has never been rendered since the
    /// engine was built. Defaults to <see langword="null"/>. This signal alone cannot tell a
    /// genuine disclosure Pattern 2 miswiring (a rule's <c>.When(...)</c> condition failing to
    /// mirror the <c>@if</c> that gates the field's render, so the rule can fail in a state the
    /// field never renders in) apart from a legitimate Pattern 1 section the user simply has not
    /// opened yet — both look identical on a first submit, since neither field has ever been
    /// registered. It stays silent for a field that WAS registered and later unregistered (a
    /// visited-then-collapsed section), which is unambiguous — that shape is unchanged and still
    /// reports only to <see cref="SuppressedIssueDiagnostic"/>.
    /// </summary>
    public Action<ValidationIssue>? NeverRegisteredFieldDiagnostic { get; set; }

    /// <summary>
    /// Development-time check that a collection's rows carry a <c>@key</c>. Defaults to
    /// <see langword="false"/>. When <see langword="true"/>, every component bound to a field
    /// re-reads its accessor on each parameter set and compares the field it now names against the
    /// one it registered, throwing an <see cref="InvalidOperationException"/> that names the field
    /// and the fix when the two diverge without the component having been torn down in between.
    /// That divergence is the signature of a row list rendered without a <c>@key</c>: removing or
    /// reordering a row leaves Blazor reusing each row's components for the next item along, and
    /// since a field is resolved once at registration, the registration, the element id, the aria
    /// attributes and the messages all stay with the row that moved away while the input displays
    /// the new row's value. Nothing about that misfiling is visible on screen, which is what makes
    /// it worth an exception rather than a diagnostic.
    /// Correctly keyed rows never trip it, whatever the edit — the three shapes differ only in what
    /// the keyed diff does with the components. Replacing a row keyed by the row object retires
    /// that row's key and introduces a different one, so its components are disposed and new ones
    /// built for the replacement. Removing a row disposes that row's components and builds nothing.
    /// Adding or reordering disposes nothing at all — a keyed diff permutes the components it
    /// already has, which is the point of <c>@key</c>. What that leaves is the same in all three:
    /// anything newly built registers the row it was handed, and every retained component keeps
    /// resolving its accessor to the row it already spoke for, so the comparison passes.
    /// Recommended in Development builds only. It costs an accessor resolution per bound component
    /// per render, and a form that reaches production with the mistake should misfile a message
    /// rather than take the page down.
    /// </summary>
    public bool VerifyRowKeys { get; set; }

    /// <summary>
    /// Role attribute applied to every field- and collection-level message list
    /// (<c>FormidableFieldMessage</c>/<c>FormidableCollectionMessage</c>). Defaults to
    /// <see langword="null"/>, which renders no <c>role</c> attribute at all. Set to
    /// <c>"status"</c> to make each list its own polite live region, announced
    /// to assistive technology as its content changes; recommended on forms that render no
    /// <see cref="FormidableSummary"/>, which already announces on its own.
    /// </summary>
    public string? InlineMessageRole { get; set; }

    /// <summary>
    /// Re-sorts the order visible issues are reported in. Defaults to <see langword="null"/>,
    /// which reports them in the document order of the rendered fields — what most forms want.
    /// </summary>
    /// <remarks>
    /// A pipeline stage rather than a replacement for
    /// <see cref="IFormidableFieldOrderService"/>: the service answers where the fields are, and
    /// this answers what order to report them in. It receives the fields already in document order
    /// and returns them re-sorted, so a consumer who only wants to move one group ahead of another
    /// can do that without describing the whole form. It runs once per order resolution — the same
    /// cadence as the service, behind the same registry-version guard — not per render and not per
    /// <see cref="IFormValidationEngine.GetVisibleIssues"/> call: its result is baked into the
    /// ordinal map, which every read of the issues then sorts by, a lookup per issue rather than
    /// another run of the delegate.
    /// The fields handed over include the model-level one — a <see cref="FieldIdentifier"/> with an
    /// empty <see cref="FieldIdentifier.FieldName"/>, carrying the all-suppressed gate's
    /// explanation and any validator fault — because it is resolved like any other field, its id
    /// riding on the form's own element. A delegate keying on field names has to say where that one
    /// goes; indexing a dictionary of field names that has no entry for it throws.
    /// Exceptions are the delegate's own. Nothing catches one — not even the interop family a call
    /// to the order service is shielded from — and this runs inside a render lifecycle method, so a
    /// delegate that throws takes the form down with it.
    /// Read by <c>FormidableForm</c>, which owns the order resolution. Attach mode
    /// (<c>FormidableValidator</c>) resolves no order at all, so setting this on a form that
    /// attaches to an existing <c>EditForm</c> does nothing.
    /// It is synchronous by design. Anything that needs to measure the DOM has to be async, and
    /// async ordering already has a home in the service; an async delegate here would duplicate it
    /// without adding reach.
    /// Reordering is all it can do. A field it leaves out of its result is appended in document
    /// order rather than dropped — an issue that is never reported is an issue a visitor cannot
    /// act on, and hiding one is what disclosure is for, with its own diagnostic.
    /// Because the map is rebuilt when the registered field set changes, a sort criterion that
    /// changes on its own — a runtime "group by severity" toggle, say — is not picked up until the
    /// next registration change.
    /// </remarks>
    public Func<IReadOnlyList<FieldIdentifier>, IReadOnlyList<FieldIdentifier>>? OrderIssues { get; set; }

    /// <summary>Class names field components and native InputBase components apply based on field state.</summary>
    public FormidableCssClasses CssClasses { get; set; } = new();
}
