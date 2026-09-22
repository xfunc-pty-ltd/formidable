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
    /// until the engine's first probe completes. The probe that keeps this current answers under
    /// the submit profile invisibly — no disclosure, no message-store write, no pending-indicator
    /// flip — so nothing about it is ever shown to the user. It shares the engine's per-rule
    /// verdict store with the passes beside it: on a validator that can execute rule by rule it
    /// runs only the submit-selected rules with no current answer and files what it ran for those
    /// passes to serve, so the probe and the passes beside it share one execution per rule per
    /// model state rather than each running their own — where every selected rule is already
    /// answered, the probe executes nothing and this property is a read of what the store already
    /// holds. Sharing is settled by what has landed rather than by what is running, and a pass
    /// files its whole plan in one act at the end: a pass still awaiting an async rule has filed
    /// nothing at all yet, so a probe starting meanwhile plans those same rules and both
    /// executions are paid. Where nothing yields, which of the two starts first does not matter:
    /// an all-synchronous plan runs to completion before the call that started it returns, so
    /// whichever goes first has already filed everything the other would have planned, and they
    /// share in full. Tracking stays opt-in because a form reading none of this
    /// gets nothing for the work, and a validator with no rule-level seam has no store to share
    /// and pays a whole-profile validation per change; see
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
    /// How firmly <see cref="FormidableOptions.SubmitProfile"/>'s rules demand that
    /// <paramref name="field"/> carry a value — what a required-field marker renders from, and
    /// what puts <c>aria-required</c> on the kit's inputs. The submit profile is the one that
    /// decides, because "required" on a form means "required before this can be submitted"; a
    /// narrowed <see cref="FormidableOptions.LiveProfile"/> changes when a message appears,
    /// never whether the value is demanded.
    /// </summary>
    /// <remarks>
    /// <see cref="FormidableOptions.RequiredOverride"/> answers first where it is set and
    /// returns non-null. Otherwise the answer is read from the validator's declared rules
    /// through <see cref="IRuleInspectingValidator{TModel}"/>, which sees presence written as
    /// <c>NotEmpty()</c>/<c>NotNull()</c> and nothing else: presence written as a predicate, a
    /// validator that cannot be inspected, a rule inside a child validator, and a field of a
    /// collection row all report <see cref="RuleRequirement.NotRequired"/>, which means "not
    /// known to be required" rather than "proven optional" — the override is how a form says
    /// otherwise.
    /// <para>
    /// One further limit is about which field an answer is filed under rather than about what
    /// the rules say, and it reaches only nested members. An answer is keyed exactly the way an
    /// ISSUE is keyed — the declared path resolved against the model graph — so a demand lands
    /// on the field the failure it describes would land on; and a component asks with the
    /// identifier it resolved when it last bound to the cascaded form context. Both name the
    /// same object, and go on naming it after a page replaces a nested object in place
    /// (<c>model.Address = new Address()</c>): the swap alone disturbs nothing. What separates
    /// them is the next derivation — a <see cref="FormidableOptions.SubmitProfile"/> swap, or a
    /// move in the rendered field set — which files <c>Address.City</c> under the NEW owner
    /// while a component that has not rebound still asks under the old one. From there the
    /// field reports <see cref="RuleRequirement.NotRequired"/> — no marker and no
    /// <c>aria-required</c> — and a further move does not repair it: only that component
    /// rebinding does, which is its host rebuilding the engine and registry. It fails the way
    /// every limit above fails, towards claiming nothing, and
    /// <see cref="FormidableOptions.RequiredOverride"/> answers over it.
    /// </para>
    /// <para>
    /// Because the derived answer is reused, asking per field per render is a dictionary lookup;
    /// the override delegate, being the part that can change on its own, is invoked on every ask.
    /// </para>
    /// </remarks>
    RuleRequirement GetFieldRequirement(FieldIdentifier field);

    /// <summary>
    /// The field's current issues, any severity, computed from the engine's own state each time
    /// it is asked: the submit channel's view first (its errors, then its advisories), then the
    /// live channel's, then the engine's fault issue on the model-level field. Every channel
    /// after the errors is filtered against what is already showing for the field, so a message
    /// two channels both carry reads once, in the position the first of them gave it.
    /// </summary>
    IReadOnlyList<ValidationIssue> GetIssues(FieldIdentifier field);

    /// <summary>
    /// All currently-visible issues across the form, computed the same way and collected channel
    /// by channel: the fault issue (model-level), then every field's submit errors, then their
    /// advisories, then the live channel, each channel after the errors minus whatever is already
    /// showing for the same field. Ordered by where each field sits on the page once the host has
    /// resolved that — so entries from different channels interleave by field, and anything the
    /// host could not place sorts last. Until then, and for a host that never resolves an order,
    /// they arrive in that channel order: fault, submit, live.
    /// </summary>
    IReadOnlyList<VisibleIssue> GetVisibleIssues();

    /// <summary>Marks a field as touched (called by field components on user interaction).</summary>
    void MarkTouched(FieldIdentifier field);

    /// <summary>
    /// Runs the submit pipeline: validate with the submit profile, surface visible issues, record
    /// which fields it disclosed. Call from the renderer's synchronization context (a Blazor event
    /// handler or <c>InvokeAsync</c>) — it mutates validation state and triggers renders.
    /// </summary>
    Task<SubmitOutcome> ValidateForSubmitAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies server-declared issues (e.g. from a 400 ValidationProblemDetails) as if they were
    /// submit results: the server's verdict applies at the severity it carries. Errors land on
    /// their fields and reach the EditContext's message store; warnings and infos land as
    /// advisories, which the reads above surface and the store — an error-only surface — does not.
    /// The payload is treated as the server's CURRENT verdict: it replaces the server's previous
    /// one outright rather than accumulating with it, so re-submitting the same or a corrected
    /// payload does not duplicate inline messages. The server's issues are held apart from the
    /// client's own, so a replace cannot disturb a client-sourced issue on the same field, and an
    /// advisory whose message a client rule already disclosed for that field shows once, as the
    /// client's copy. Applying is itself a disclosure event for the fields it names: a client
    /// error the last submit computed but had nowhere to show surfaces alongside the server's.
    /// The server's verdict stands until a newer whole-model answer supersedes it — the next
    /// debounced refresh, or the next submit — at which point a server-only issue with no matching
    /// client rule goes, while one a client rule agrees with keeps showing through the client's own
    /// answer. Because the payload is treated as a submit result, applying one also sets
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
    /// it — because an advisory blocks nothing, so hiding one strands no verdict. A payload
    /// carrying the same message twice for one field at one severity lands it once: a reader has
    /// no use for it twice.
    /// </remarks>
    void ApplyServerIssues(IEnumerable<ValidationIssue> issues);
}
