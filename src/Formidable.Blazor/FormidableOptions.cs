using Microsoft.AspNetCore.Components.Forms;

namespace Formidable.Blazor;

/// <summary>Engine configuration. Profiles are assigned meaning here — at the point of use.</summary>
public sealed class FormidableOptions
{
    /// <summary>
    /// Profile run on field changes. Defaults to <see langword="null"/>, which tracks
    /// <see cref="SubmitProfile"/> — including a custom one, and including one swapped at
    /// runtime — so a field the visitor has engaged discloses whatever would block a submit,
    /// a required-field rule among it.
    /// </summary>
    /// <remarks>
    /// What keeps a form from nagging is engagement, not rule selection: the live channel writes
    /// verdicts only for fields the visitor has committed a change to, so an untouched field says
    /// nothing whatever its rules would report. Narrowing the live channel's rules on top of that
    /// is a second, blunter gate — it cannot tell "not touched yet" from "touched and left empty",
    /// and the field the visitor is working in is exactly the one it silences.
    /// Set it where a submit rule is too expensive to run per change — a uniqueness check against
    /// a server, say — and the narrowed profile then decides what the live channel evaluates;
    /// <see cref="ValidationProfile.Draft"/> is the usual choice, leaving every submit-ruleset
    /// rule to the submit itself. Save-progress flows are unaffected either way: they validate
    /// <see cref="ValidationProfile.Draft"/> at the save call, whatever the live channel is doing.
    /// </remarks>
    public ValidationProfile? LiveProfile { get; set; }

    /// <summary>Profile run by the submit pipeline. Defaults to <see cref="ValidationProfile.Submit"/>.</summary>
    public ValidationProfile SubmitProfile { get; set; } = ValidationProfile.Submit;

    /// <summary>
    /// Debounce for the refresh pass, armed by a field change once a submit has happened and by
    /// any move in the rendered field set. Defaults to 300 ms.
    /// </summary>
    public TimeSpan RefreshDebounce { get; set; } = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// Debounce for the live pass a field change triggers. Defaults to <see langword="null"/>,
    /// which runs the live pass immediately on every field change.
    /// When set, a field change arms a single timer instead of running the pass immediately; a
    /// further change within the window re-arms it rather than starting a second timer, and the
    /// pass runs once the window elapses with no further edit, scoped to every field changed
    /// since the window opened. The window is shared across fields rather than tracked per
    /// field — the same semantics <see cref="RefreshDebounce"/> already has for the refresh.
    /// </summary>
    public TimeSpan? LiveDebounce { get; set; }

    /// <summary>
    /// Opt-in whole-form validity probe for disable-submit scenarios. Defaults to
    /// <see langword="false"/> — off, never default-on: only a form with something reading
    /// <see cref="IFormValidationEngine.IsFormValid"/> gets anything for it. Two shapes make that
    /// work permanent rather than shared: a validator that cannot execute rule by rule, and one
    /// whose <see cref="LiveProfile"/> narrows what the live pass beside it answers. On either,
    /// the extra evaluation is paid on every change for the whole life of the form. Those are the
    /// standing costs, not the whole of them — the rest of this remark says exactly what the probe
    /// shares with the passes around it and where sharing does not happen.
    /// When <see langword="true"/>, the engine keeps that property current with a standalone
    /// <see cref="SubmitProfile"/> evaluation that is not an engine pass: no disclosure, no
    /// message-store write, no pending-indicator flip — nothing about it is ever shown. The probe
    /// runs once at construction (so a pristine, untouched form still reports truthfully) and
    /// again on every field change afterwards, at the same cadence the live pass itself runs at —
    /// immediately per change, or once per window when <see cref="LiveDebounce"/> is also set.
    /// On a validator that can execute rule by rule, the probe reads and feeds the engine's
    /// verdict store: it executes only the submit-selected rules with no current answer and
    /// lands what it ran for later passes to reuse, so the probe and the passes beside it share
    /// one execution per rule per model state rather than each running their own — where every
    /// selected rule is already answered, the probe executes nothing at all. Sharing is settled
    /// by what has landed rather than by what is running, and a pass files its whole plan in one
    /// act at the end: a pass still awaiting an async rule has filed nothing at all yet, so a
    /// probe starting meanwhile plans those same rules and both executions are paid. Where nothing
    /// yields, which of the two starts first does not matter: an all-synchronous plan runs to
    /// completion before the call that started it returns, so whichever goes first has already
    /// filed everything the other would have planned, and they share in full. On any other
    /// validator each probe is one whole <see cref="SubmitProfile"/> validation, in addition to
    /// the live pass it rides beside — which selects those same rules unless
    /// <see cref="LiveProfile"/> narrows it, so the two evaluate the same work twice. Where it
    /// does narrow, the probe is the more expensive of the two, with the async/server-shaped
    /// rules among the difference.
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
    /// Whether the root recovers a click the page displaced out from under the pointer between the
    /// press and its release. Defaults to <see cref="DisplacedClickRecovery.Buttons"/>, which
    /// recovers clicks on buttons inside the root; see that type for the three conditions that
    /// have to hold and for what the recovered click is. Set
    /// <see cref="DisplacedClickRecovery.None"/> to install no guard at all.
    /// </summary>
    /// <remarks>
    /// Read once per root, on its first interactive render, so this is not something a page turns
    /// on and off mid-life — swap it alongside the model, the same way every other option here is
    /// changed. It needs the library's own script: a host that cannot load it recovers nothing,
    /// which is the same bargain the reading-order resolve already strikes. Prerendering is
    /// unaffected — a form that cannot submit yet cannot lose a click.
    /// </remarks>
    public DisplacedClickRecovery ClickRecovery { get; set; } = DisplacedClickRecovery.Buttons;

    /// <summary>
    /// Optional disclosure override. Return true to force an issue visible, false to force it
    /// suppressed, or null to defer to the field registry. Model-level issues (empty path) are
    /// always visible unless this returns false.
    /// </summary>
    public Func<ValidationIssue, bool?>? DisclosureOverride { get; set; }

    /// <summary>
    /// How the live channel discloses an engaged field's issues. Defaults to
    /// <see cref="LiveIssueDisclosure.Engaged"/>: engagement alone discloses, on every surface —
    /// the engine's issue reads and the <c>ValidationMessageStore</c> alike — and registration
    /// filters the live channel nowhere, beyond ending the engagement of a field that LEAVES the
    /// page (one something registered and nothing renders any more; a field nothing ever
    /// registered has not left, so it keeps its verdict). That default is the native-interop
    /// bridge contract: an anchor-free native form registers no fields, and its live errors must
    /// reach the store regardless. <see cref="LiveIssueDisclosure.EngagedAndVisible"/> opts in to
    /// gating each live issue on override-aware visibility
    /// (<see cref="DisclosureOverride"/> first, rendered registration otherwise), uniformly
    /// across every surface, store included — which means a page that notifies changes for
    /// fields it does not currently render hides those fields' live errors everywhere until
    /// they render; that is what opting in asks for. For a summary that lists less without
    /// changing disclosure, use <c>FormidableSummary</c>'s <c>Show</c> filter instead. Read at
    /// every view evaluation, so the engine's own reads answer to a change the moment it is made;
    /// the <c>ValidationMessageStore</c> is a materialized projection of those same views rather
    /// than a read of them, so it carries the change from its next rebuild.
    /// </summary>
    public LiveIssueDisclosure LiveDisclosure { get; set; } = LiveIssueDisclosure.Engaged;

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
