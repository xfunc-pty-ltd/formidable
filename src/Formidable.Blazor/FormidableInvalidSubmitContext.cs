namespace Formidable.Blazor;

/// <summary>
/// What <see cref="FormidableForm{TModel}.OnInvalidSubmit"/> is handed: the blocked submit's
/// <see cref="SubmitOutcome"/>, and the one thing a handler can tell the form back — that it has
/// answered this submit itself and the automatic focus move should stand down.
/// </summary>
/// <remarks>
/// The case it exists for is a dialog. A handler that opens one has covered the form, and the
/// move the form makes next, left to itself, puts the caret in a field behind the overlay; but the
/// form cannot see that a dialog was opened, and a handler that logs telemetry or scrolls a banner
/// wants that move to happen. So the decision is made where the
/// knowledge is — in the handler, at the moment it opens the dialog — rather than by a
/// parameter set once at the top of the markup for every submit the form will ever run.
/// <para>
/// Nothing here is a channel back into validation: the outcome is the engine's own value and this
/// object holds it unchanged. Suppression is the whole of what a handler can say through it, and
/// it says it about one submit.
/// </para>
/// </remarks>
public sealed class FormidableInvalidSubmitContext
{
    /// <summary>
    /// A blocked submit builds one of these for its handler, so a page has no reason to construct
    /// one. It is public for the case that has: a test exercising a handler on its own, without a
    /// rendered form around it.
    /// </summary>
    /// <param name="outcome">The verdict this submit blocked on.</param>
    /// <exception cref="ArgumentNullException"><paramref name="outcome"/> is null.</exception>
    public FormidableInvalidSubmitContext(SubmitOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        Outcome = outcome;
    }

    /// <summary>
    /// The verdict this submit blocked on — the same <see cref="SubmitOutcome"/> instance
    /// <see cref="FormidableForm{TModel}.SubmitAsync"/> returns to whoever called it. So a
    /// handler drawing a list of what needs attention reads
    /// <see cref="SubmitOutcome.VisibleErrorSummary"/>, already the distinct names, and one
    /// wanting everything behind those names reads <see cref="SubmitOutcome.Report"/>.
    /// </summary>
    public SubmitOutcome Outcome { get; }

    /// <summary>
    /// Whether <see cref="SuppressFirstErrorFocus"/> has been called on this context. The form
    /// reads it once the handler's returned task has completed; a handler that delegates to a
    /// helper can read it to see whether that helper already took the submit over.
    /// </summary>
    public bool FirstErrorFocusSuppressed { get; private set; }

    /// <summary>
    /// Tells the form not to make its automatic first-error focus move for this submit, because
    /// the handler is answering the block itself. Call it from a handler that opens a dialog or
    /// otherwise takes the visitor somewhere of its own choosing; the page then asks for the move
    /// when it is ready, through
    /// <see cref="FormidableForm{TModel}.FocusFirstErrorAsync"/>.
    /// </summary>
    /// <remarks>
    /// It suppresses; it does not request. A form whose
    /// <see cref="FormidableForm{TModel}.FocusFirstErrorOnInvalidSubmit"/> is
    /// <see langword="false"/> was not going to move focus for this submit anyway, and calling
    /// this changes nothing there: asking for a move is what
    /// <see cref="FormidableForm{TModel}.FocusFirstErrorAsync"/> is for.
    /// <para>
    /// The effect is this submit's alone: the form builds a fresh context for every blocked
    /// submit, so a handler that suppresses one and not the next gets exactly that, and
    /// <see cref="FormidableForm{TModel}.FocusFirstErrorOnInvalidSubmit"/> keeps deciding every
    /// submit the handler stays quiet about. Calling it more than once for one submit is the same
    /// as calling it once, and nothing undoes it. It is a method rather than a settable flag for
    /// that reason: <see cref="System.ComponentModel.CancelEventArgs.Cancel"/> and its kin are
    /// requests a handler may reconsider, where this reports a fact about what the page has
    /// already done, and a fact is not something a later line can un-know.
    /// </para>
    /// <para>
    /// It has to be called before the handler's returned task completes, which is when the form
    /// reads the answer. A handler that starts unawaited work and suppresses from inside that work
    /// is racing the read, since the form does not wait for it.
    /// </para>
    /// </remarks>
    public void SuppressFirstErrorFocus() => FirstErrorFocusSuppressed = true;
}
