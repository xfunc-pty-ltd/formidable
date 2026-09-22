using Microsoft.AspNetCore.Components.Forms;

namespace Formidable.Blazor;

/// <summary>Non-generic engine view consumed by components and the cascaded form context.</summary>
/// <remarks>
/// Implementing this interface is supported surface — a test double cascaded in place of the
/// shipped engine is the expected shape for component tests — and it grows accordingly: a
/// member added after v1 carries a default implementation whose answer is the absent-feature
/// one. A new <see langword="bool"/> reads <see langword="false"/>, a new collection reads
/// empty, and a new query answers the way an engine without the feature would, so an
/// implementation that predates the member keeps compiling and keeps its meaning — though it
/// also sits the new behaviour out until it overrides the member, which is what a
/// conservative default is for. Every other Formidable interface a consumer implements
/// carries this same policy, each naming its own conservative default.
/// <para>
/// The policy has honest limits, stated once, here. A default body has only the interface's
/// members to compute from, because an interface cannot carry per-implementation state — so a
/// member that needs state of its own is a design conversation, not a default. An event
/// cannot be defaulted into being raised: an implementation that predates one would compile
/// against accessors that discard its subscribers and silently never raise it, so a new event
/// is likewise a redesign rather than a default. What keeps the event story growable is the
/// shape the two events here already have — anything they learn to say arrives as init-only
/// properties on their arguments classes, never as a new event.
/// </para>
/// </remarks>
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
    /// which narrows to the triggering field during a live pass, to the fields edited within its
    /// debounce window during a refresh pass, and to no field at all during the pass
    /// <see cref="DiscloseLoadedValuesAsync"/> runs, which nobody asked for. A submit is the one
    /// pass where the two flags agree: it is form-wide on both.
    /// </summary>
    bool IsValidating { get; }

    /// <summary>True once the submit pipeline has run (and validation failed or succeeded).</summary>
    bool HasSubmitted { get; }

    /// <summary>
    /// Whether the form would currently pass <see cref="FormidableOptions.SubmitProfile"/> —
    /// meant for disable-submit scenarios. Meaningful only while
    /// <see cref="FormidableOptions.TrackFormValidity"/> is on — tracking is what keeps it
    /// current: a form that never enables tracking reads <see langword="false"/>, one that
    /// disables it mid-form keeps whatever it was last reading (nothing resets it), and even
    /// with tracking on it reads <see langword="false"/> until the first probe or whole-model
    /// pass answers it. The probe that keeps this current answers under
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

    /// <summary>
    /// Raised whenever validation or field state changes. The sender is the engine;
    /// <see cref="FormidableStateChangedEventArgs"/> carries no detail, and anything the event
    /// learns to say about what changed arrives there as init-only properties rather than as a
    /// change to the event's own shape, so a handler keeps compiling as the arguments grow.
    /// </summary>
    event EventHandler<FormidableStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// Raised when a pass nobody is awaiting — live, refresh, or the invisible probe behind
    /// <see cref="FormidableOptions.TrackFormValidity"/> — fails with an unexpected exception
    /// (not cancellation), carrying that exception on
    /// <see cref="FormidableValidationFaultedEventArgs.Exception"/>; subscribe to log or
    /// otherwise handle it. A pass with a caller awaiting it — a submit,
    /// <see cref="DiscloseLoadedValuesAsync"/> — throws to that caller instead and never raises
    /// this. A live or refresh fault also surfaces a generic model-level message while its pass
    /// is still the current one; the probe's never does, because the probe promises no
    /// disclosure. The sender is the engine, and anything the event learns to say beyond the
    /// exception arrives as init-only properties on the arguments class rather than as a change
    /// to the event's own shape, so a handler keeps compiling as the arguments grow.
    /// </summary>
    event EventHandler<FormidableValidationFaultedEventArgs>? ValidationFaulted;

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
    /// <c>NotEmpty()</c>/<c>NotNull()</c> and nothing else: presence written as a predicate and
    /// a validator that cannot be inspected both report
    /// <see cref="FieldRequirement.NotRequired"/>, which means "not known to be required" rather
    /// than "proven optional" — the override is how a form says otherwise. A rule the root does
    /// not declare for itself IS read, by whichever of three routes carries it: one inside a
    /// child validator is answered under the child's own path (<c>Address.City</c>), one merged
    /// in with <c>Include</c> at the including validator's level, and one inside a child a
    /// MODEL-level rule carries (<c>RuleFor(x =&gt; x).SetValidator(v)</c>, or the
    /// <c>ChildRules</c> form) at the root's own level, since that is where each one's failures
    /// land.
    /// <para>
    /// A field of a collection row is answered from the template its rule declares —
    /// <c>Attendees[].Name</c> — expanded against the rows the model holds when the answer is
    /// derived: one entry per row, keyed by the row's own resolved identifier, so an attendee's
    /// Name is marked and announced exactly as a top-level field is. The validator is asked
    /// once per template and every row shares that answer, which is what a template means: the
    /// rule was declared about the shape, not about any particular row. A row's fields register
    /// and unregister as rendered-field-set moves, so the derivation that follows a row's
    /// arrival is the one that first answers for it.
    /// </para>
    /// <para>
    /// One further limit is about which field an answer is filed under rather than about what
    /// the rules say, and it reaches any member filed under a resolved owner — a nested
    /// object's members and a collection row's fields alike. An answer is keyed exactly the way
    /// an ISSUE is keyed — the declared path resolved against the model graph — so a demand
    /// lands on the field the failure it describes would land on; and a component asks with the
    /// identifier it resolved when it last bound to the cascaded form context. Both name the
    /// same object, and go on naming it after a page replaces a nested object in place
    /// (<c>model.Address = new Address()</c>): the swap alone disturbs nothing. What separates
    /// them is the next derivation — a <see cref="FormidableOptions.SubmitProfile"/> swap, or a
    /// move in the rendered field set — which files <c>Address.City</c> under the NEW owner
    /// while a component that has not rebound still asks under the old one. From there the
    /// field reports <see cref="FieldRequirement.NotRequired"/> — no marker and no
    /// <c>aria-required</c> — and a further move does not repair it: only that component
    /// rebinding does, which is its host rebuilding the engine and registry. It fails towards
    /// claiming nothing, as every limit above it does, and
    /// <see cref="FormidableOptions.RequiredOverride"/> answers over it.
    /// </para>
    /// <para>
    /// A child validator scoped by its own call — <c>SetValidator(validator, ruleSets)</c> —
    /// is read under the selection FluentValidation runs it with: one built from those ruleset
    /// names, replacing the selection that chose the rule holding the child. A presence rule
    /// inside such a child is therefore a demand exactly where a submit would enforce it: a
    /// tagged rule those names admit demands its field whenever the holding rule is selected,
    /// and an untagged one, which FluentValidation runs under no profile, demands it nowhere —
    /// the field takes no marker and no <c>aria-required</c> for a value no submit asks of it.
    /// </para>
    /// <para>
    /// Because the derived answer is reused, asking per field per render is a dictionary lookup;
    /// the override delegate, being the part that can change on its own, is invoked on every ask.
    /// </para>
    /// </remarks>
    FieldRequirement GetFieldRequirement(FieldIdentifier field);

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
    /// Says what the values already in the model have earned — for a form whose fields were
    /// filled from somewhere other than this visitor's typing: a saved draft, a prefilled
    /// application, a record opened for editing. Without it such a form looks pristine however
    /// good or bad its contents are, because every state class and every live message waits on
    /// the visitor having committed a change. Calling it validates the whole model under
    /// <see cref="FormidableOptions.SubmitProfile"/> and then, field by field, does what that
    /// answer earns: a field HOLDING a value no rule fails with an error is marked touched and
    /// engaged, so it wears the valid class — or whichever advisory tier its warnings and infos
    /// earn; a field holding a value the rules DO fail is marked the same way, so its message
    /// appears; and a field holding no value is neither touched nor engaged, so it stays silent
    /// and unstyled, because nobody has reached it yet. A form nothing calls this on behaves as
    /// it always has.
    /// </summary>
    /// <remarks>
    /// It engages, and engagement is what carries the middle outcome. Merely marking the fields
    /// touched would paint the good ones green and leave a bad saved value SILENT among them,
    /// and silence in a row of green reads as "not filled in yet" rather than "this one is
    /// wrong". Engagement is permanent in the ordinary way — the fields it names go on being
    /// answered by every later live pass, exactly as a field the visitor had typed into would
    /// be, until they leave the page. The pass this ends with answers the whole engaged set,
    /// not only the fields it just adopted, so a field the visitor had already engaged is
    /// re-answered for the values as they now stand rather than left describing the ones the
    /// load replaced.
    /// <para>
    /// No option has to be turned on for the valid class to appear: green is a promise about
    /// submit, and the pass this runs is a submit-profile answer for the whole model, so the
    /// coverage that promise rests on is earned by the call itself rather than borrowed from
    /// <see cref="FormidableOptions.TrackFormValidity"/>. What it costs is that pass — one
    /// whole-model validation, async rules included, at the moment the values are loaded —
    /// plus the live pass that discloses what it found, which on a validator that can
    /// execute rule by rule executes nothing at all, every rule having just been answered
    /// at this same edit stamp, and on one that cannot is a second whole-model validation. It
    /// scales with the model as well as with the rules: a rule declared per collection element
    /// is answered once per element, and every element it speaks for that holds a value joins
    /// the engaged set, answered by every later live pass until that field DEPARTS — the same
    /// terminator any engagement has, and a predicate rather than an event: something registered
    /// the field once, and nothing holds a registration for it now. A field nothing has ever
    /// registered has not departed, and neither has one whose registration is held open by
    /// <c>KeepRegistered</c> — which is what a virtualized panel is told to set, so on the very
    /// shape that carries thousands of rows, scrolling drains nothing. Size the cost as
    /// thousands of answers and an engaged set that keeps them.
    /// One thing is visible while it runs, and one deliberately is not. Nothing here was
    /// triggered by one field and nobody asked for it, so no field reports
    /// <see cref="FieldState.IsValidating"/> and none wears the pending class — the engine-level
    /// flag is true throughout, for a page-level spinner to read. What IS visible is that on a
    /// form which had already answered for itself, the values it loaded are ones nothing
    /// notified for, so every stored answer is stranded as the call begins and any field
    /// already wearing the valid class loses it until this pass lands. That is invisible on a
    /// synchronous validator and lasts as long as the slowest rule on one that is not.
    /// </para>
    /// <para>
    /// The split between the second outcome and the third is the VALUE, never which kind of rule
    /// failed. "Holds no value" means what FluentValidation's own <c>NotEmpty()</c> means, read
    /// against the type the member is DECLARED as: null, a blank or whitespace-only string, a
    /// collection with no elements, or the default of a non-nullable value type. So a saved
    /// <see langword="false"/> in a <c>bool?</c> is an answer and earns its class, while an
    /// untouched <c>bool</c> is not; the same separates <c>int</c> from <c>int?</c>, an enum from
    /// a nullable enum, and <c>Guid</c>/<c>DateTime</c>/<c>decimal</c> from their nullable forms.
    /// The ambiguity that leaves is exactly the non-nullable value types, and it errs towards
    /// silence: model an optional value as <c>T?</c> and a load reads it exactly; model it as
    /// <c>T</c> and its default reads as "not filled in".
    /// </para>
    /// <para>
    /// The two outcomes need different things, which decides what a validator that cannot be
    /// read can still do. Disclosing a wrong value needs only the model, so it happens whatever
    /// the validator is — a hand-rolled <see cref="IModelValidator{TModel}"/> included. Vouching
    /// for a good one needs the validator's own list of the fields it has rules for
    /// (<see cref="IRuleInspectingValidator{TModel}.GetDeclaredFieldPaths"/>), because nothing
    /// else can tell a field whose rules all passed from a field no rule mentions — so a
    /// validator with no inspection capability vouches for nothing. That list is the validator's
    /// declared shape, child validators and <c>Include</c>d rules included, so a row inside a
    /// collection is confirmed and disclosed exactly as a top-level field is. Its own limits are
    /// documented on it, and each costs a green rather than producing a wrong one: a shape the
    /// walk cannot read leaves its paths absent, and an absent path is a field nothing vouches
    /// for.
    /// </para>
    /// <para>
    /// Where the value cannot be read at all, nothing is claimed: a path whose intermediate is
    /// null (<c>Address.City</c> where <c>Address</c> is), a model-level failure, which names no
    /// member, and a path naming no member. Those fail in the safe direction — the field stays
    /// unstyled rather than being painted red.
    /// <see cref="GetFieldRequirement"/> errs the same way at its own limits, but what
    /// claiming nothing LOOKS like differs: it reports
    /// <see cref="FieldRequirement.NotRequired"/> and drops a marker, where this stays silent
    /// and paints no class.
    /// </para>
    /// <para>
    /// One residual is worth knowing before wiring this to a model whose constructor seeds
    /// placeholder values. A seeded value is a value: it is on the model when the call runs, and
    /// nothing distinguishes it from one the draft saved. So a form that seeds a placeholder its
    /// own rules reject shows that rejection the moment the values load. Seeding the default
    /// instead — an empty string, a null — leaves the field silent until someone reaches it.
    /// </para>
    /// <para>
    /// Severity is read too: only an error says a value is wrong, so a field carrying nothing
    /// worse than a warning or an info is vouched for and shows that advisory, as it would had
    /// the visitor typed it.
    /// </para>
    /// <para>
    /// What the engaged fields then SHOW is the live channel's business, so a form that has
    /// narrowed <see cref="FormidableOptions.LiveProfile"/> discloses only what that profile
    /// selects: a loaded value failing a submit-only rule is engaged and counted against the
    /// form's validity, but stays quiet until a submit, exactly as it would after being typed.
    /// Call from the renderer's synchronization context (a Blazor event handler or
    /// <c>InvokeAsync</c>) — it mutates validation state and triggers renders.
    /// </para>
    /// </remarks>
    /// <param name="cancellationToken">
    /// Cancels the whole-model pass. A cancelled call throws and adopts nothing, so the form
    /// says no more than it did before — though the model is still one this engine has been
    /// told has moved, so whatever it could vouch for beforehand it no longer can.
    /// </param>
    Task DiscloseLoadedValuesAsync(CancellationToken cancellationToken = default);

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
    /// debounced refresh, the next submit, or a page saying what its freshly loaded values have
    /// earned through <see cref="DiscloseLoadedValuesAsync"/> — at which point a server-only
    /// issue with no matching client rule goes, while one a client rule agrees with keeps showing
    /// through the client's own answer. Because the payload is treated as a submit result,
    /// applying one also sets <see cref="HasSubmitted"/> — a page whose only validation is
    /// server-side reaches the submitted state through this call alone. Call from the renderer's
    /// synchronization context (a Blazor event handler or <c>InvokeAsync</c>) — it mutates
    /// validation state and triggers renders. <paramref name="issues"/> is enumerated exactly
    /// once.
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
