using Microsoft.AspNetCore.Components.Forms;

namespace Formidable.Blazor;

/// <summary>Engine configuration. Profiles are assigned meaning here — at the point of use.</summary>
/// <remarks>
/// The engine holds the instance it was built with and reads each property at each use — a pass
/// selecting its profile, a timer arming, a render asking for a class name or a marker — so
/// mutating a property mid-form takes effect at that property's next read, unless the property's
/// own remarks state a coarser read (<see cref="ClickRecovery"/>'s once-per-root read is the
/// archetype). A change notifies nothing by itself: it shows when something next validates or
/// renders. Handing a root a different options instance without swapping the model throws rather
/// than silently applying nothing — build the options once, hold them, and mutate that instance.
/// </remarks>
public sealed class FormidableOptions
{
    /// <summary>Creates an instance holding each property's own default.</summary>
    public FormidableOptions()
    {
    }

    /// <summary>
    /// Creates an instance holding every property's value from <paramref name="defaults"/> — the
    /// answer to "app-wide defaults, and one form that differs in one setting". Resolution has no
    /// merging step, so a form's <c>Options</c> parameter replaces the app-wide instance whole;
    /// copying is how such a form keeps the settings it never meant to change, rather than
    /// restating a design system's class names and a team's debounce to narrow a profile:
    /// <c>new FormidableOptions(appWide) { LiveProfile = ValidationProfile.Draft }</c>.
    /// </summary>
    /// <param name="defaults">
    /// The instance to copy from — usually the app-wide singleton that
    /// <see cref="FormidableBlazorServiceCollectionExtensions"/>'s
    /// <c>AddFormidableBlazor(Action&lt;FormidableOptions&gt;)</c> overload registered, injected
    /// into the page. Nothing stops a form copying any other instance.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="defaults"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// What is copied is each property's VALUE, read once, here. Every value but one is a string,
    /// a struct, an immutable <see cref="ValidationProfile"/> or a delegate, so for those the copy
    /// and its source part company the moment it is built: a later change to the source's
    /// <see cref="RefreshDebounce"/> does not reach a copy already made.
    /// <see cref="CssClasses"/> is the exception, and it is SHARED rather than cloned
    /// deliberately: its value is a mutable object, and the copy holds the same
    /// <see cref="FormidableCssClasses"/> instance, so mutating that map's properties goes on
    /// reaching this form as it reaches every other form resolving it. Cloning would split the
    /// copying form off from the rest — a class map is read at each computation precisely so a
    /// change reaches kit components and native <c>InputBase</c> components alike, and a private
    /// clone would reintroduce that split one level up. The snapshot rule still applies to the
    /// property itself: assigning a different <see cref="FormidableCssClasses"/> to the source
    /// afterwards leaves the copy holding the map it was handed. A form that wants different class
    /// names assigns a new map to its own copy.
    /// Build the copy once and hold it, exactly as the class remarks above require of any instance
    /// a form is handed.
    /// </remarks>
    public FormidableOptions(FormidableOptions defaults)
    {
        ArgumentNullException.ThrowIfNull(defaults);

        LiveProfile = defaults.LiveProfile;
        SubmitProfile = defaults.SubmitProfile;
        RefreshDebounce = defaults.RefreshDebounce;
        LiveDebounce = defaults.LiveDebounce;
        TrackFormValidity = defaults.TrackFormValidity;
        NormalizeOnSubmit = defaults.NormalizeOnSubmit;
        ClickRecovery = defaults.ClickRecovery;
        DisclosureOverride = defaults.DisclosureOverride;
        RequiredOverride = defaults.RequiredOverride;
        ShowRequiredIndicators = defaults.ShowRequiredIndicators;
        RequiredIndicatorContent = defaults.RequiredIndicatorContent;
        LiveDisclosure = defaults.LiveDisclosure;
        SuppressedIssueDiagnostic = defaults.SuppressedIssueDiagnostic;
        NeverRegisteredFieldDiagnostic = defaults.NeverRegisteredFieldDiagnostic;
        VerifyRowKeys = defaults.VerifyRowKeys;
        InlineMessageLive = defaults.InlineMessageLive;
        DefensiveGateMessage = defaults.DefensiveGateMessage;
        ModelLevelDisplayName = defaults.ModelLevelDisplayName;
        ValidationFaultMessage = defaults.ValidationFaultMessage;
        OrderIssues = defaults.OrderIssues;
        CssClasses = defaults.CssClasses;
    }

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
    /// <remarks>
    /// <see cref="TimeSpan.Zero"/> is the narrowest window, not a switch: each arming event
    /// re-arms the shared timer to fire as soon as it can run, so the window no longer waits for
    /// a burst of edits to settle — but the pass still starts from the timer's own callback
    /// rather than inside the notification that armed it, and a fire that finds a submit, live,
    /// or load pass in flight still defers and re-arms. The one spelling that turns the refresh
    /// off is <see cref="System.Threading.Timeout.InfiniteTimeSpan"/>, which arms a timer that
    /// never fires — no positive width does, however wide. Contrast <see cref="LiveDebounce"/>,
    /// whose <see langword="null"/> genuinely bypasses its timer.
    /// </remarks>
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
    /// <remarks>
    /// <see cref="TimeSpan.Zero"/> is not a spelling of <see langword="null"/>: it is a real
    /// window of zero width, and it keeps the timer path — field changes accumulate, the pass
    /// starts from the timer's fire scoped to whatever accumulated before it ran, a fire that
    /// finds a submit, refresh, or load pass in flight defers and re-arms, and the
    /// <see cref="TrackFormValidity"/> probe rides once per window.
    /// <see langword="null"/> bypasses the timer entirely: the live pass starts inside the
    /// field-changed notification itself, one pass per change with the probe beside it at that
    /// same cadence, standing down only for a submit or load in flight and superseding anything
    /// else.
    /// </remarks>
    public TimeSpan? LiveDebounce { get; set; }

    /// <summary>
    /// Opt-in whole-form validity probe for disable-submit scenarios. Defaults to
    /// <see langword="false"/> — off, never default-on: only a form with something reading
    /// <see cref="IFormidableEngine.IsFormValid"/> gets anything for it. Two shapes make that
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
    /// The construction-time probe is the one read of this property a mutation cannot reach, and
    /// enabling tracking mid-form computes nothing by itself:
    /// <see cref="IFormidableEngine.IsFormValid"/> keeps whatever it holds —
    /// <see langword="false"/>, on a form tracking has never answered — until the next field
    /// change or whole-model pass answers it.
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
    /// Optional per-issue visibility answer, consulted where the engine decides whether an issue
    /// may be shown: return <see langword="true"/> to answer yes for an issue whose field
    /// nothing renders, <see langword="false"/> to answer no, or <see langword="null"/> to defer
    /// to the field registry. Defaults to <see langword="null"/>. What an answer decides is
    /// channel-dependent — see remarks; it is an input to each channel's own disclosure rule,
    /// never a per-issue switch over what is on screen.
    /// </summary>
    /// <remarks>
    /// On the submit channel the answer decides whether an error puts its FIELD under watch,
    /// and the watch is per field and only ever unions: <see langword="false"/> withholds that
    /// one issue's contribution and no more, so a field any sibling issue, earlier blocked
    /// submit, or server apply has revealed discloses its current submit-selected errors whole,
    /// with no per-issue re-check — the override's authority is over revealing, not over
    /// filtering a revealed field's answer. A submit's advisories are filtered per issue as they
    /// are captured; every other pass that answers the submit profile recaptures a watched
    /// field's advisories from the rules unfiltered. <c>ApplyServerIssues</c> asks per issue at
    /// apply: a server error is stored whether or not anything renders its field, only an explicit
    /// <see langword="false"/> drops one, and a dropped error reveals nothing either; a server
    /// advisory defers to the registry like a client one. The live channel consults this only under
    /// <see cref="LiveIssueDisclosure.EngagedAndVisible"/> — under the default
    /// <see cref="LiveIssueDisclosure.Engaged"/> policy no answer here reaches a live issue in
    /// either direction — and even where the opt-in applies it, per issue at every read,
    /// <see langword="true"/> grants visibility, never engagement. Model-level issues (empty
    /// path) resolve to the form's own element, which counts as rendered for as long as the form
    /// is on the page, so deferring leaves them visible.
    /// </remarks>
    public Func<ValidationIssue, bool?>? DisclosureOverride { get; set; }

    /// <summary>
    /// Optional requiredness override, consulted before the validator's own rules are read.
    /// Return a <see cref="FieldRequirement"/> to declare a field's requiredness outright, or
    /// null to defer to what the rules say. Defaults to <see langword="null"/>, which defers for
    /// every field.
    /// </summary>
    /// <remarks>
    /// Not a nicety: reading rules can only see presence expressed as FluentValidation's own
    /// <c>NotEmpty()</c>/<c>NotNull()</c>, so presence written as a predicate —
    /// <c>Must(s =&gt; !string.IsNullOrWhiteSpace(s))</c> — is indistinguishable from any other
    /// predicate and reports <see cref="FieldRequirement.NotRequired"/>; a validator that cannot
    /// be inspected at all reports it for every field. This is what a form says instead, and it
    /// declares in both directions: <see cref="FieldRequirement.Required"/> marks a field the
    /// rules cannot be read to demand, and <see cref="FieldRequirement.NotRequired"/> unmarks
    /// one they can — a <c>NotNull()</c> on a value the page fills in itself, say.
    /// It decides both surfaces at once, so the marker a <c>FormidableRequiredIndicator</c>
    /// renders and the <c>aria-required</c> the kit's inputs carry cannot disagree.
    /// Invoked on every ask — once per bound component per render — rather than cached with the
    /// rest of the answer, so a delegate reading state that changes is answered as it changes.
    /// Keep it cheap and keep it a pure read: it runs inside a render.
    /// </remarks>
    public Func<FieldIdentifier, FieldRequirement?>? RequiredOverride { get; set; }

    /// <summary>
    /// Whether <c>FormidableRequiredIndicator</c> renders at all. Defaults to
    /// <see langword="true"/>. Set to <see langword="false"/> to render no marker anywhere on
    /// the form — no element, not an empty one — the form-wide off switch for a design that
    /// marks its optional fields instead.
    /// </summary>
    /// <remarks>
    /// Off means off for everything the indicator might ever render: should the component grow a
    /// per-instance template, this switch suppresses that too, so "no marker anywhere on the
    /// form" stays true whatever a field supplies. What it never suppresses is
    /// <c>aria-required</c>: whether a value is demanded is a fact about the input, not a
    /// decoration, so assistive technology keeps being told even where nothing is drawn. For a
    /// marker drawn entirely in CSS, leave this on and set
    /// <see cref="RequiredIndicatorContent"/> to <c>""</c> instead — the empty element is what a
    /// stylesheet's <c>::before</c> needs to land on.
    /// </remarks>
    public bool ShowRequiredIndicators { get; set; } = true;

    /// <summary>
    /// The content <c>FormidableRequiredIndicator</c> renders inside its marker for a required
    /// field. Defaults to <c>"*"</c>. It decides what a drawn marker holds, never whether one is
    /// drawn — that is <see cref="ShowRequiredIndicators"/>. Set to <c>""</c> to render the
    /// marker element empty: the route for a marker drawn entirely in CSS, since it keeps the
    /// <c>formidable-required</c> element on the page for a stylesheet's
    /// <c>::before</c>/<c>::after</c> to draw into.
    /// </summary>
    /// <remarks>
    /// The library ships no styling, so this is the text inside the marker's
    /// <c>formidable-required</c> element and nothing else — colour, spacing and any glyph drawn
    /// with <c>::before</c>/<c>::after</c> are the consumer's stylesheet's business.
    /// </remarks>
    public string RequiredIndicatorContent { get; set; } = "*";

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
    /// Development-time check that a component still speaks for the field it registered. Defaults
    /// to <see langword="false"/>. When <see langword="true"/>, every component bound to a field
    /// re-reads its accessor on each parameter set and compares the field it now names against the
    /// one it registered, throwing an <see cref="InvalidOperationException"/> that names the field
    /// and the fix when the two diverge without the component having been torn down in between.
    /// A row list rendered without a <c>@key</c> is the common way to produce that divergence, and
    /// the exception leads with it: removing or reordering a row leaves Blazor reusing each row's
    /// components for the next item along, and since a field is resolved once at registration, the
    /// registration, the element id, the aria attributes and the messages all stay with the row
    /// that moved away while the input displays the new row's value. Nothing about that misfiling
    /// is visible on screen, which is what makes it worth an exception rather than a diagnostic.
    /// Replacing a nested object under a field bound to it produces the same divergence and the
    /// same throw with no collection anywhere on the page: a field is the object owning the value
    /// plus a member name, so a fresh owner is a different field.
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
    /// Read once per bound component, as it binds to the form's context, so a change mid-form
    /// governs only components that bind afterwards — those already bound keep the answer they
    /// read.
    /// </summary>
    public bool VerifyRowKeys { get; set; }

    /// <summary>
    /// The <c>aria-live</c> politeness applied to every message list — field-, collection- and
    /// model-level alike
    /// (<c>FormidableFieldMessage</c>/<c>FormidableCollectionMessage</c>/<c>FormidableModelMessage</c>).
    /// Defaults to <see langword="null"/>, which renders no <c>aria-live</c> attribute at all. Set
    /// to <c>"polite"</c> to have each list announced to assistive technology as its content
    /// changes, or <c>"assertive"</c> to interrupt whatever is being read; recommended on forms
    /// that render no <see cref="FormidableSummary"/>, which already announces on its own.
    /// </summary>
    /// <remarks>
    /// <c>aria-live</c> rather than a <c>role</c>, and the difference is not cosmetic. A
    /// <c>role</c> on the list element replaces the list role, which drops every <c>li</c> inside
    /// out of the accessibility tree as presentational: the announcement arrives and the messages
    /// stop being a list the visitor can move through. <c>status</c> and <c>alert</c> also carry
    /// an implicit <c>aria-atomic</c> of true, so correcting one field reads back every message
    /// still standing. <c>aria-live</c> costs neither — the list stays a list, and atomicity
    /// stays false, so only what changed is announced.
    /// </remarks>
    public string? InlineMessageLive { get; set; }

    /// <summary>
    /// The sentence the all-suppressed defensive gate carries — the one model-level explanation a
    /// blocked submit shows when every field that failed is hidden and nothing on screen accounts
    /// for the block. Defaults to <c>"The form cannot be submitted because information that is not
    /// currently displayed is invalid."</c>, which is a sentence in English, so a form addressing
    /// its users in another language, or in a wording of its own, replaces it here.
    /// </summary>
    /// <remarks>
    /// Read wherever a surface asks the submit channel what it holds, so
    /// <see cref="IFormidableEngine.GetIssues"/>,
    /// <see cref="IFormidableEngine.GetVisibleIssues"/> and the components reading them answer
    /// with a change from the next read after it is made; the <c>EditContext</c>'s
    /// <c>ValidationMessageStore</c> is a materialized projection of those same reads rather than a
    /// read of them, so it carries the change from its next rebuild. The engine files no gate
    /// entry that could go stale between the two: the gate is a predicate over source state, and
    /// its issue is built where it is read.
    /// This property decides that one explanation and nothing else. A failing rule's message
    /// belongs to the validator that wrote it, and the name the gate's explanation is listed
    /// under in <see cref="SubmitOutcome.VisibleErrorSummary"/> is
    /// <see cref="ModelLevelDisplayName"/>.
    /// </remarks>
    public string DefensiveGateMessage { get; set; } =
        "The form cannot be submitted because information that is not currently displayed is invalid.";

    /// <summary>
    /// The name <see cref="SubmitOutcome.VisibleErrorSummary"/> lists an error under when that
    /// error's issue names no field of its own — the defensive gate's explanation, and any
    /// model-level rule a blocked submit disclosed. Defaults to <c>"This form"</c>. It is English,
    /// as <see cref="DefensiveGateMessage"/> and <see cref="ValidationFaultMessage"/> are, and a
    /// form re-voicing any of the three has reason to look at the other two.
    /// </summary>
    /// <remarks>
    /// That list holds names rather than messages: an entry is the issue's
    /// <see cref="ValidationIssue.DisplayName"/> where the issue carries one and its
    /// <see cref="ValidationIssue.Path"/> otherwise, and this property stands in wherever that
    /// pair leaves an empty string. Read as each submit builds its outcome, so a
    /// <see cref="SubmitOutcome"/> already handed back holds the names it was built with.
    /// It reaches that list and nothing else. A <c>FormidableSummary</c> entry renders its
    /// issue's <see cref="ValidationIssue.Message"/>, and an <c>ItemTemplate</c> that renders a
    /// name instead reads the <see cref="ValidationIssue.DisplayName"/> the issue itself carries
    /// — which the gate's explanation leaves unset.
    /// </remarks>
    public string ModelLevelDisplayName { get; set; } = "This form";

    /// <summary>
    /// The sentence a faulted pass files against the form: a validation pass threw before it could
    /// finish, so what the form shows is incomplete rather than wrong. Defaults to
    /// <c>"Validation could not run to completion; recent changes may not be fully validated."</c>,
    /// English as its two siblings above are, and replaced the same way.
    /// </summary>
    /// <remarks>
    /// Read where a faulted pass files its issue, and that issue is stored rather than derived, so
    /// a change reaches the NEXT fault while one already on screen goes on saying what it said
    /// when it was filed. Contrast <see cref="DefensiveGateMessage"/>, whose explanation is built
    /// at each read because the gate files nothing. The stored issue is cleared from two places
    /// rather than one: a pass that completes without faulting, and
    /// <see cref="IFormidableEngine.ApplyServerIssues"/>, which clears it with no pass
    /// involved — so a server round trip after a client-side fault ends it exactly as a recovered
    /// pass does, and a form does not carry a fault it is no longer subject to.
    /// It says nothing about what threw, deliberately: the exception is delivered to
    /// <see cref="IFormidableEngine.ValidationFaulted"/>, which is where a host logs it and
    /// where anything diagnostic belongs. This is the half the person filling the form reads.
    /// </remarks>
    public string ValidationFaultMessage { get; set; } =
        "Validation could not run to completion; recent changes may not be fully validated.";

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
    /// <see cref="IFormidableEngine.GetVisibleIssues"/> call: its result is baked into the
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
    /// <remarks>
    /// Read at each class computation, on every surface. Mutating this instance's properties and
    /// assigning a whole new instance are therefore the same lever: both reach kit components and
    /// native InputBase components alike, from the next computation each makes. Nothing latches a
    /// class map at engine construction.
    /// </remarks>
    public FormidableCssClasses CssClasses { get; set; } = new();
}
