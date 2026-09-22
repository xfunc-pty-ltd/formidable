namespace Formidable.Blazor;

/// <summary>What <see cref="FormidableForm{TModel}.OnInvalidSubmit"/> receives: the blocked submit's <see cref="SubmitOutcome"/>, and the way to tell the form this handler has taken the submit over.</summary>
// The case this exists for is a dialog. A handler that opens one has covered the form, and the
// focus move the form makes next, left to itself, puts the caret in a field behind the overlay;
// but the form cannot see that a dialog was opened, and a handler that logs telemetry or scrolls
// a banner wants that move to happen. So the decision is made where the knowledge is, in the
// handler at the moment it opens the dialog, rather than by a parameter set once at the top of
// the markup for every submit the form will ever run.
public sealed class FormidableInvalidSubmitContext
{
    /// <summary>Creates a context over an outcome, for a test that exercises a handler without a form; a blocked submit builds its own.</summary>
    /// <param name="outcome">The outcome the submit blocked on.</param>
    /// <exception cref="ArgumentNullException"><paramref name="outcome"/> is <see langword="null"/>.</exception>
    public FormidableInvalidSubmitContext(SubmitOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        Outcome = outcome;
    }

    /// <summary>The outcome the submit blocked on, the same instance <see cref="FormidableForm{TModel}.SubmitAsync"/> returns.</summary>
    public SubmitOutcome Outcome { get; }

    /// <summary>Whether <see cref="SuppressFirstErrorFocus"/> has been called on this context; the form reads it once the handler's task completes.</summary>
    public bool FirstErrorFocusSuppressed { get; private set; }

    /// <summary>Tells the form not to move focus for this submit, because the handler is answering the block itself.</summary>
    /// <remarks>
    /// Call it before the handler's task completes, which is when the form reads it; work the
    /// handler leaves unawaited is too late. It applies to this submit only, cannot be undone,
    /// and never asks for a move: that is <see cref="FormidableForm{TModel}.FocusFirstErrorAsync"/>.
    /// With <see cref="FormidableForm{TModel}.FocusFirstErrorOnInvalidSubmit"/> off it changes
    /// nothing.
    /// </remarks>
    // A method rather than a settable flag: CancelEventArgs.Cancel and its kin are requests a
    // handler may reconsider, where this reports a fact about what the page has already done,
    // and a fact is not something a later line can un-know.
    public void SuppressFirstErrorFocus() => FirstErrorFocusSuppressed = true;
}
