using Microsoft.AspNetCore.Components.Forms;

namespace Formidable.Blazor;

/// <summary>The engine as components and the cascaded <see cref="FormidableFormContext"/> see it: field state, issues, submit, loaded values, server issues and two events.</summary>
/// <remarks>
/// Implementing this interface is supported; a member added later carries a default
/// implementation, so an implementation written against these members keeps compiling. Both
/// roots build the shipped engine; a test double cascaded in its place is the expected shape
/// for a component test.
/// </remarks>
// A member added here defaults to the absent-feature answer: a new bool reads false, a new
// collection reads empty, a new query answers as an engine without the feature would. The
// policy has limits. A default body has only the interface's members to compute from, so a
// member that needs state of its own is a redesign, not a default; and an event cannot be
// defaulted into being raised (an implementation predating it would compile against accessors
// that discard its subscribers), so anything the two events learn to say arrives as init-only
// properties on their arguments classes, never as a new event.
public interface IFormidableEngine
{
    /// <summary>The <see cref="Microsoft.AspNetCore.Components.Forms.EditContext"/> this engine writes messages to.</summary>
    EditContext EditContext { get; }

    /// <summary>The registry rendered fields register with, which decides which fields a submit may disclose.</summary>
    FieldRegistry Registry { get; }

    /// <summary>The options this engine was built with.</summary>
    FormidableOptions Options { get; }

    /// <summary>Whether the form is mid-check (a live check, a submit, a load or the whole-form re-check), whichever field started it; a per-field "checking" indicator reads <see cref="FieldState.IsValidating"/> instead.</summary>
    bool IsValidating { get; }

    /// <summary>Whether a submit is checking: true from the call to <see cref="ValidateForSubmitAsync"/> until its last check ends, however it ends, with <see cref="StateChanged"/> raised as it falls (none at disposal); a live check or a load of values leaves it false.</summary>
    /// <remarks>
    /// It covers the check alone, so it falls before the page's save. A lock that should hold
    /// through <see cref="FormidableForm{TModel}.OnValidSubmit"/> binds
    /// <see cref="FormidableForm{TModel}.IsSubmitting"/>, which the cascaded context carries as
    /// <see cref="FormidableFormContext.IsSubmitting"/>. An engine without the feature answers
    /// <see langword="false"/>.
    /// </remarks>
    bool IsSubmitting => false;

    /// <summary>Whether a submit has answered through <see cref="ValidateForSubmitAsync"/> or a server reply has been applied through <see cref="ApplyServerIssues(IEnumerable{ValidationIssue})"/>; either starts the whole-form re-check that follows every later edit under <see cref="FormidableOptions.RefreshDebounce"/>.</summary>
    bool HasSubmitted { get; }

    /// <summary>Whether the form would pass the submit profile, kept current only while <see cref="FormidableOptions.TrackFormValidity"/> is on; <see langword="false"/> on a form that has never tracked.</summary>
    /// <remarks>
    /// It is written only while tracking is on, so it keeps its last answer whenever tracking is
    /// off, whatever check was already running. With tracking on from the start it reads
    /// <see langword="false"/> until a whole-form check has answered once. Issues a server applied through
    /// <see cref="ApplyServerIssues(IEnumerable{ValidationIssue})"/> are not part of the answer. The first read with tracking
    /// off writes one Trace line and one logged warning naming the option.
    /// </remarks>
    bool IsFormValid { get; }

    /// <summary>Raised whenever validation or field state changes; the sender is the engine and <see cref="FormidableStateChangedEventArgs"/> carries no detail.</summary>
    event EventHandler<FormidableStateChangedEventArgs>? StateChanged;

    /// <summary>Raised when a check no caller awaits (a live check, the whole-form re-check or the validity check) throws; a submit or a load throws to its caller instead.</summary>
    /// <remarks>
    /// A cancellation is not a fault. Under <see cref="FormidableForm{TModel}"/> and
    /// <see cref="FormidableValidator{TModel}"/> it is raised on the renderer's dispatcher, so a
    /// handler can call <c>StateHasChanged</c>.
    /// </remarks>
    event EventHandler<FormidableValidationFaultedEventArgs>? ValidationFaulted;

    /// <summary>The current state of one field.</summary>
    /// <param name="field">The field.</param>
    /// <returns>Its touched, modified, checking, severity and would-pass-submit flags.</returns>
    FieldState GetFieldState(FieldIdentifier field);

    /// <summary>How firmly the submit profile's rules demand a value in <paramref name="field"/>, as the required indicator and <c>aria-required</c> render it; <see cref="FormidableOptions.RequiredOverride"/> answers first.</summary>
    /// <param name="field">The field.</param>
    /// <returns>The requirement; <see cref="FieldRequirement.NotRequired"/> means no presence rule that fails as an error was found, not that the field is proven optional.</returns>
    /// <remarks>
    /// Presence is read from <c>NotEmpty()</c> and <c>NotNull()</c> rules failing as an error; a
    /// predicate, and any rule on a validator that cannot be inspected, leaves the field
    /// <see cref="FieldRequirement.NotRequired"/>. After the page replaces a nested object in
    /// place, the next derivation (a submit profile swap or a move in the rendered field set)
    /// files the demand under the new instance, so a component still bound to the old one reads
    /// <see cref="FieldRequirement.NotRequired"/> until it rebinds; the override answers over it.
    /// </remarks>
    FieldRequirement GetFieldRequirement(FieldIdentifier field);

    /// <summary>The issues currently showing for one field, at every severity, computed on each call: its submit errors, then its advisories and live messages less any message already listed.</summary>
    /// <param name="field">The field.</param>
    /// <returns>Its submit errors, then its advisories, then its live messages, each later group minus any message already listed; the fault message last, on the model-level field.</returns>
    IReadOnlyList<ValidationIssue> GetIssues(FieldIdentifier field);

    /// <summary>Every issue currently showing across the form, computed on each call, in the page's reading order once the root resolved one.</summary>
    /// <returns>Each issue with its field; before an order is resolved, and under a root that resolves none, the fault message first, then submit errors, advisories and live messages.</returns>
    IReadOnlyList<VisibleIssue> GetVisibleIssues();

    /// <summary>Marks the field touched, which lets its state class paint, without reporting a value change; a committed change marks it too.</summary>
    /// <param name="field">The field.</param>
    void MarkTouched(FieldIdentifier field);

    /// <summary>Validates the whole model under the submit profile, discloses what is on screen, and reports whether the submit may proceed.</summary>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>The outcome; blocked, with nothing shown, when a newer submit or load answered in its place (its report empty when the check honoured the cancellation, its own otherwise), and when values were edited during each of its three checks and the last found no error.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled before the check answered.</exception>
    /// <remarks>
    /// A value edited while the check runs makes it check again before it reports, at most
    /// twice, so a submit that may proceed answers for the values the model holds as the check
    /// ends. With <see cref="FormidableOptions.NormalizeOnSubmit"/> on, the model is normalized
    /// before each check. Call it from the renderer's synchronization context.
    /// </remarks>
    Task<SubmitOutcome> ValidateForSubmitAsync(CancellationToken cancellationToken = default);

    /// <summary>Says what the values already in the model have earned: a field holding a value shows what the submit profile says of it, valid, or its message unless it waits for Submit, and a field holding nothing stays silent.</summary>
    /// <param name="cancellationToken">Cancels the check; a cancelled call discloses nothing.</param>
    /// <returns>A task that completes when the fields have been marked and the check that discloses them has answered.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled before the whole-model check answered.</exception>
    /// <remarks>
    /// Holding a value means what <c>NotEmpty()</c> means for the member's declared type, so a
    /// <see langword="false"/> in a <see langword="bool"/> is empty and one in a <c>bool?</c> is
    /// not. A validator that cannot report its rules discloses failing values and confirms none.
    /// A field a component renders with <c>WaitForSubmit</c> keeps its message back until a
    /// submit answers (not one displaced or faulted) or a server reply is applied; a passing value
    /// still shows valid. Call it from the renderer's synchronization context.
    /// </remarks>
    Task DiscloseLoadedValuesAsync(CancellationToken cancellationToken = default);

    /// <summary>Applies a server reply's issues as the server's current answer in the submit view: an error lands whether or not its field is rendered, an advisory only where a submit could show it.</summary>
    /// <param name="issues">The server's current issues; enumerated once.</param>
    /// <exception cref="ArgumentNullException"><paramref name="issues"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// A reply replaces the previous one. It stands until the next submit or load, or until the
    /// form is re-checked after a later edit or a later change in which fields render; a live
    /// check, or one already running when the reply arrives, leaves it. Applying sets
    /// <see cref="HasSubmitted"/> and clears a standing
    /// <see cref="FormidableOptions.ValidationFaultMessage"/>. Only
    /// <see cref="FormidableOptions.DisclosureOverride"/> answering <see langword="false"/> hides
    /// an error. Call it from the renderer's synchronization context.
    /// </remarks>
    void ApplyServerIssues(IEnumerable<ValidationIssue> issues);

    /// <summary>Marks where the form stands as a page sends a request whose reply it will apply, for <see cref="ApplyServerIssues(IEnumerable{ValidationIssue}, ServerRequestMark)"/> to check against.</summary>
    /// <returns>The mark to hand back with the reply; an engine without the feature returns the default mark, which marks nothing.</returns>
    /// <remarks>
    /// A component that renders a field arriving or leaving after the mark drops the reply, and this
    /// member counts one only once the root has taken it in, which can be after the render. A page
    /// whose send adds or removes rows marks through
    /// <see cref="FormidableForm{TModel}.MarkServerRequest"/> or
    /// <see cref="FormidableValidator{TModel}.MarkServerRequest"/>, which take such a change in
    /// first. Call it from the renderer's synchronization context.
    /// </remarks>
    ServerRequestMark MarkServerRequest() => default;

    /// <summary>Applies a server reply as <see cref="ApplyServerIssues(IEnumerable{ValidationIssue})"/> does, unless the form has moved on since <paramref name="sentAt"/> was taken; a dropped reply changes nothing.</summary>
    /// <param name="issues">The server's issues for the request <paramref name="sentAt"/> marks; enumerated at most once.</param>
    /// <param name="sentAt">The mark taken as the request was sent.</param>
    /// <returns><see langword="true"/> when the reply was applied; <see langword="false"/> when it was dropped.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="issues"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// The reply is dropped when, after the mark, a value was edited (a change an input holds until
    /// a blur counts when the blur announces it), the page loaded values, a component that renders
    /// a field arrived or left, or a reset replaced the engine, or once the engine is disposed. A
    /// dropped reply shows nothing, sets nothing and moves no focus. The default mark applies every
    /// reply. Call it from the renderer's synchronization context.
    /// </remarks>
    bool ApplyServerIssues(IEnumerable<ValidationIssue> issues, ServerRequestMark sentAt)
    {
        ApplyServerIssues(issues);
        return true;
    }
}
