using Microsoft.AspNetCore.Components.Forms;

namespace Formidable.Blazor;

/// <summary>The settings a form validates and renders with, from the profiles and the two waits to the diagnostics, messages and class names.</summary>
/// <remarks>
/// Build one instance, hold it, and change its properties in place: a property is read where it
/// is used unless its own remarks say it is read once, so a change shows at the next check or
/// render and re-renders nothing by itself. Handing a root a different instance without the swap
/// that rebuilds its engine (a new <c>Model</c> on <see cref="FormidableForm{TModel}"/>, a new
/// <c>EditContext</c> on <see cref="FormidableValidator{TModel}"/>) throws
/// <see cref="InvalidOperationException"/>.
/// </remarks>
public sealed class FormidableOptions
{
    /// <summary>Creates an instance holding every property's default.</summary>
    public FormidableOptions()
    {
    }

    /// <summary>Creates a copy of <paramref name="defaults"/>, for a form whose <c>Options</c> replaces the app-wide instance whole and wants every setting but one kept.</summary>
    /// <param name="defaults">The instance to copy, usually the app-wide one <see cref="FormidableBlazorServiceCollectionExtensions.AddFormidableBlazor(Microsoft.Extensions.DependencyInjection.IServiceCollection, Action{FormidableOptions})"/> registered.</param>
    /// <exception cref="ArgumentNullException"><paramref name="defaults"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// Every value is copied once, so a later change to <paramref name="defaults"/> does not reach
    /// the copy, except <see cref="CssClasses"/>: the copy shares that
    /// <see cref="FormidableCssClasses"/> instance, so a class name changed on it reaches both
    /// forms. Assigning a new map to either instance leaves the other holding the old one.
    /// </remarks>
    /// <example>
    /// <code>new FormidableOptions(appWide) { LiveProfile = ValidationProfile.Draft }</code>
    /// </example>
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
        StaleRegistrationDiagnostic = defaults.StaleRegistrationDiagnostic;
        VerifyRowKeys = defaults.VerifyRowKeys;
        ReportStaleRegistrations = defaults.ReportStaleRegistrations;
        InlineMessageLive = defaults.InlineMessageLive;
        DefensiveGateMessage = defaults.DefensiveGateMessage;
        ModelLevelDisplayName = defaults.ModelLevelDisplayName;
        ValidationFaultMessage = defaults.ValidationFaultMessage;
        OrderIssues = defaults.OrderIssues;

        // Shared rather than cloned: a class map is read at each computation precisely so a
        // change reaches kit components and native InputBase components alike, and a private
        // clone would split the copying form off from the rest one level up.
        CssClasses = defaults.CssClasses;
    }

    /// <summary>The profile a live check runs. Defaults to <see langword="null"/>, which runs whatever <see cref="SubmitProfile"/> holds, so an engaged field shows whatever would block a submit.</summary>
    /// <remarks>
    /// Narrow it only where a submit rule is too expensive to run per change;
    /// <see cref="ValidationProfile.Draft"/> is the usual choice.
    /// </remarks>
    public ValidationProfile? LiveProfile { get; set; }

    /// <summary>The profile a submit runs; the whole-form re-check, <see cref="IFormidableEngine.DiscloseLoadedValuesAsync"/> and the <see cref="TrackFormValidity"/> check run it too. Defaults to <see cref="ValidationProfile.Submit"/>.</summary>
    public ValidationProfile SubmitProfile { get; set; } = ValidationProfile.Submit;

    /// <summary>After a submit, how long after an edit before the whole form is re-checked so the summary stays truthful. Defaults to 300 ms.</summary>
    /// <remarks>
    /// A change to which fields are on screen re-checks the whole form after the same wait at any
    /// point in the form's life. <see cref="TimeSpan.Zero"/> re-checks as soon as the edit is
    /// committed; only <see cref="Timeout.InfiniteTimeSpan"/> turns the re-check off. Contrast
    /// <see cref="LiveDebounce"/>, whose <see langword="null"/> means no wait at all.
    /// </remarks>
    public TimeSpan RefreshDebounce { get; set; } = TimeSpan.FromMilliseconds(300);

    /// <summary>How long after a change before the live check runs, one shared wait for every field changed meanwhile. Defaults to <see langword="null"/>, which checks at once on every change.</summary>
    /// <remarks>
    /// A further change inside the wait restarts it. <see cref="TimeSpan.Zero"/> is a wait of no
    /// width, not a spelling of <see langword="null"/>: the check still starts from a timer rather
    /// than inside the change itself. With <see cref="TrackFormValidity"/> on, the validity check
    /// waits for the same window.
    /// </remarks>
    public TimeSpan? LiveDebounce { get; set; }

    /// <summary>Whether <see cref="IFormidableEngine.IsFormValid"/> is kept current as the visitor edits, by a whole-form check of the submit profile, so a Submit button can be disabled. Defaults to <see langword="false"/>.</summary>
    /// <remarks>
    /// The check runs once when the form is built and then at the live check's own cadence, once
    /// per window under <see cref="LiveDebounce"/>; it shows no message and no "checking". Turning
    /// it on mid-form computes nothing until the next change, submit, load or whole-form re-check.
    /// With it off, <see cref="IFormidableEngine.IsFormValid"/> keeps its last answer,
    /// <see langword="false"/> on a form that has never tracked.
    /// </remarks>
    public bool TrackFormValidity { get; set; }

    /// <summary>Whether a submit calls <see cref="INormalizableModel.Normalize"/> on a model implementing it before validating, so the rules and the inputs' next render see the normalized values. Defaults to <see langword="false"/>.</summary>
    // Mirrors the AspNetCore validation filters, which normalize a request body before validating
    // it, so a model normalized on the server is normalized the same way on the client.
    public bool NormalizeOnSubmit { get; set; }

    /// <summary>Whether the root re-delivers a click the page moved out from under a still pointer between the press and the release. Defaults to <see cref="DisplacedClickRecovery.Buttons"/>.</summary>
    /// <remarks>
    /// Read once per root, on its first render after its engine is built, so change it with the
    /// swap that rebuilds the engine rather than mid-form. Recovery needs the library's own
    /// script: a host that cannot load it recovers nothing, and prerendering loses nothing.
    /// </remarks>
    public DisplacedClickRecovery ClickRecovery { get; set; } = DisplacedClickRecovery.Buttons;

    /// <summary>A per-issue answer to whether an issue may show: <see langword="true"/> for yes though nothing renders its field, <see langword="false"/> for no, <see langword="null"/> to defer to the field registry (whether the field is rendered). Defaults to <see langword="null"/>.</summary>
    /// <remarks>
    /// What an answer decides depends on the channel asking. At a submit it decides whether an
    /// error's field is watched; a watched field shows all its errors, whatever the answer for a
    /// sibling. A server error hides only on <see langword="false"/>; a server advisory shows on
    /// <see langword="true"/>, hides on <see langword="false"/>, defers to the registry on
    /// <see langword="null"/>. The live channel asks only under
    /// <see cref="LiveIssueDisclosure.EngagedAndVisible"/>, where <see langword="true"/> grants
    /// visibility, never engagement. A model-level issue counts as rendered while the form is on the
    /// page.
    /// </remarks>
    public Func<ValidationIssue, bool?>? DisclosureOverride { get; set; }

    /// <summary>A per-field answer to how firmly the field is required, asked before the validator's rules: a <see cref="FieldRequirement"/> declares, <see langword="null"/> defers to the rules. Defaults to <see langword="null"/>.</summary>
    /// <remarks>
    /// The rules show presence only as <c>NotEmpty()</c> or <c>NotNull()</c>, so presence written
    /// as a predicate, or any field of a validator that cannot be inspected, reads as
    /// <see cref="FieldRequirement.NotRequired"/>; this declares in both directions, and the
    /// marker and <c>aria-required</c> follow the one answer. Invoked on every ask (once per bound
    /// component per render), never cached, so keep it a cheap pure read.
    /// </remarks>
    public Func<FieldIdentifier, FieldRequirement?>? RequiredOverride { get; set; }

    /// <summary>Whether <see cref="FormidableRequiredIndicator{TValue}"/> renders anything; <see langword="false"/> renders no marker element anywhere on the form. Defaults to <see langword="true"/>.</summary>
    /// <remarks>
    /// It never suppresses <c>aria-required</c>, which follows the field's requirement. For a
    /// marker drawn entirely in CSS, leave this on and set <see cref="RequiredIndicatorContent"/>
    /// to an empty string, which keeps the element for a stylesheet's <c>::before</c> to land on.
    /// </remarks>
    public bool ShowRequiredIndicators { get; set; } = true;

    /// <summary>The text inside the marker <see cref="FormidableRequiredIndicator{TValue}"/> renders for a required field; an empty string keeps the element for a stylesheet to draw into. Defaults to <c>"*"</c>.</summary>
    public string RequiredIndicatorContent { get; set; } = "*";

    /// <summary>Which engaged fields' live messages show: every engaged field, or only those on screen. Defaults to <see cref="LiveIssueDisclosure.Engaged"/>.</summary>
    /// <remarks>
    /// A change reaches the engine's own reads at once and a native <c>ValidationMessage</c>
    /// after the next check lands.
    /// </remarks>
    public LiveIssueDisclosure LiveDisclosure { get; set; } = LiveIssueDisclosure.Engaged;

    /// <summary>Called once for each error a submit could not show, and each advisory a server reply could not, because nothing renders its field or <see cref="DisclosureOverride"/> said no. Defaults to <see langword="null"/>.</summary>
    /// <remarks>
    /// A submit reports its own errors here and <see cref="IFormidableEngine.ApplyServerIssues"/>
    /// a reply's advisories; a server error the override drops reports nowhere. A Trace line and,
    /// where the host has an <c>ILoggerFactory</c>, a logged warning are written whether or not
    /// this is set. On the server route the issue's strings are the reply's own, so a callback
    /// writing them to a log should neutralize control characters in the path as the library's
    /// own lines do; a Blazor render encodes them.
    /// </remarks>
    // A logged warning needs no wiring on WASM, whose default logging provider is the browser
    // console.
    public Action<ValidationIssue>? SuppressedIssueDiagnostic { get; set; }

    /// <summary>Called beside <see cref="SuppressedIssueDiagnostic"/>, with the same issue, when the suppressed issue's field has never been rendered in the engine's lifetime. Defaults to <see langword="null"/>.</summary>
    /// <remarks>
    /// A section the visitor has not opened and a rule whose <c>.When(...)</c> never matches the
    /// <c>@if</c> around its field look the same here, so treat it as a place to look. A field
    /// rendered once and hidden afterwards reports only to <see cref="SuppressedIssueDiagnostic"/>.
    /// </remarks>
    public Action<ValidationIssue>? NeverRegisteredFieldDiagnostic { get; set; }

    /// <summary>Called once for each stale registration <see cref="ReportStaleRegistrations"/> finds, with the component and both fields. Defaults to <see langword="null"/>.</summary>
    /// <remarks>
    /// Nothing reaches it while <see cref="ReportStaleRegistrations"/> is off, and with
    /// <see cref="VerifyRowKeys"/> on the exception replaces the report. A Trace line and, where
    /// the host has an <c>ILoggerFactory</c>, a logged warning are written whether or not this is
    /// set. Read at each report, and invoked unguarded, so a throw surfaces from the component's
    /// own lifecycle. Nothing handed over is payload-supplied: the names come from the component's
    /// own accessor.
    /// </remarks>
    public Action<StaleRegistrationReport>? StaleRegistrationDiagnostic { get; set; }

    /// <summary>Whether a bound component throws <see cref="InvalidOperationException"/>, naming the field and the fix, when its accessor names a different field than it registered, as an unkeyed row list produces. Defaults to <see langword="false"/>.</summary>
    /// <remarks>
    /// For Development builds: the misfiling is invisible on screen, and where a throw is the
    /// wrong severity <see cref="ReportStaleRegistrations"/> reports the same divergence. Read
    /// once per bound component as it binds, so a change mid-form reaches only components binding
    /// afterwards. A list keyed by its row objects never trips it, whatever the edit; replacing a
    /// nested object under a bound field does.
    /// </remarks>
    public bool VerifyRowKeys { get; set; }

    /// <summary>Whether a bound component reports, without throwing, when its accessor names a different field than it registered. Defaults to <see langword="false"/>.</summary>
    /// <remarks>
    /// A divergence is reported once, through a Trace line, a logged warning and
    /// <see cref="StaleRegistrationDiagnostic"/>, and again only after it heals and reopens or the
    /// component rebinds; an accessor that cannot resolve is skipped, and with
    /// <see cref="VerifyRowKeys"/> on the exception replaces the report. Read once per bound
    /// component as it binds. Pair them:
    /// <c>VerifyRowKeys = isDevelopment; ReportStaleRegistrations = !isDevelopment;</c>.
    /// </remarks>
    // Off by default on a measurement: detection costs an accessor resolution per bound component
    // per parameter set, and a one-hop accessor (() => Order.Total) resolves in nanoseconds while
    // one that navigates further (() => Order.Customer.Name) compiles its owner expression on
    // every resolution, microseconds and kilobytes each.
    public bool ReportStaleRegistrations { get; set; }

    /// <summary>The <c>aria-live</c> value every message list carries (field, collection and model level), such as <c>"polite"</c>. Defaults to <see langword="null"/>, which renders none.</summary>
    // aria-live rather than a role, and the difference is not cosmetic: a role on the list element
    // replaces the list role and drops every li out of the accessibility tree as presentational,
    // and status and alert carry an implicit aria-atomic of true, so correcting one field would
    // read back every message still standing.
    public string? InlineMessageLive { get; set; }

    /// <summary>The sentence a blocked submit shows when every failing field is hidden and nothing on screen accounts for the block. Defaults to <c>"The form cannot be submitted because information that is not currently displayed is invalid."</c>.</summary>
    public string DefensiveGateMessage { get; set; } =
        "The form cannot be submitted because information that is not currently displayed is invalid.";

    /// <summary>The name <see cref="SubmitOutcome.VisibleErrorSummary"/> lists an error under when the error names no field of its own: the gate's explanation, or a model-level rule. Defaults to <c>"This form"</c>.</summary>
    public string ModelLevelDisplayName { get; set; } = "This form";

    /// <summary>The form-level message shown when a live check or the whole-form re-check throws before finishing, which leaves what the form shows incomplete. Defaults to <c>"Validation could not run to completion; recent changes may not be fully validated."</c>.</summary>
    /// <remarks>
    /// It says nothing about what threw: the exception goes to
    /// <see cref="IFormidableEngine.ValidationFaulted"/>, where a host logs it.
    /// </remarks>
    public string ValidationFaultMessage { get; set; } =
        "Validation could not run to completion; recent changes may not be fully validated.";

    /// <summary>A re-sort of the order visible issues are listed in, handed the rendered fields in document order once per order resolution. Defaults to <see langword="null"/>, which keeps document order.</summary>
    /// <remarks>
    /// The list includes the model-level field (an empty <see cref="FieldIdentifier.FieldName"/>),
    /// so a delegate keyed by name must place it. A field the delegate leaves out is appended in
    /// document order, never dropped. It runs inside a render, and a throw takes the form down.
    /// <see cref="FormidableValidator{TModel}"/> resolves no order, so this does nothing in attach
    /// mode. A criterion that changes on its own is picked up at the next order resolution (a
    /// change to the rendered field set, or a reported layout move).
    /// </remarks>
    // Synchronous by design: anything that must measure the DOM belongs in the async
    // IFormidableFieldOrderService, and an async delegate here would duplicate it without adding
    // reach. Reordering is all it can do, because an issue never listed is one a visitor cannot
    // act on; hiding is disclosure's job, with its own diagnostic.
    public Func<IReadOnlyList<FieldIdentifier>, IReadOnlyList<FieldIdentifier>>? OrderIssues { get; set; }

    /// <summary>The class name a field wears in each state, on kit inputs and native <c>InputBase</c> inputs alike. Defaults to a new <see cref="FormidableCssClasses"/>.</summary>
    public FormidableCssClasses CssClasses { get; set; } = new();
}
