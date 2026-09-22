namespace Formidable.Blazor;

/// <summary>
/// Whether a Formidable root recovers a click the page displaced out from under the pointer, and
/// for what. A click is a press and a release, and the browser fires one only when both share a
/// target — otherwise it dispatches on their nearest common ancestor, where nothing is listening.
/// That rule is what makes dragging off a button cancel it, and it is written in terms of targets
/// while the visitor's intent lives in pointer movement: a button that moves out from under a
/// still pointer produces exactly the same different target as a pointer that moved off a still
/// button. Recovery supplies the missing discrimination.
/// </summary>
/// <remarks>
/// A form is the common case rather than an exotic one. Pressing a submit button blurs the field
/// the visitor was in, that blur commits the value, the commit discloses whatever the value now
/// fails, and the message lands above the button — so the button leaves the pointer before the
/// release. Keyboard activation is unaffected either way, since focus follows the element rather
/// than a coordinate; pointer and touch are the whole gap.
/// </remarks>
public enum DisplacedClickRecovery
{
    /// <summary>
    /// Recover nothing: the root installs no guard at all, and a click the page displaced is lost
    /// exactly as the browser left it. Choose this for a page that moves its own controls
    /// deliberately during a press and wants the platform's rule to stand unqualified.
    /// </summary>
    None,

    /// <summary>
    /// Recover clicks on buttons — <c>&lt;button&gt;</c>, <c>&lt;input type="submit"&gt;</c> and
    /// <c>&lt;input type="image"&gt;</c> — inside this root. The default, because the defect
    /// reaches a consumer who never reads a word of documentation.
    /// </summary>
    /// <remarks>
    /// The click is re-delivered to the button itself, so an ordinary handler on a button that
    /// submits nothing is reached the same way a submit button is, and the mis-delivered click is
    /// suppressed as the recovery goes out. One press therefore stays one click at every level:
    /// the browser dispatched the original on an ancestor of the button, which is on the
    /// re-delivered click's own path, so the intended click arrives exactly where the misdirected
    /// one would have and replaces it. A consumer's delegated click handler, an analytics
    /// listener or a click-outside-to-close guard sees one click, not two.
    /// What arrives is script-dispatched, and not only at the button: every element on its path
    /// reads <c>isTrusted</c> as <see langword="false"/>. Code that gates on that flag — a
    /// consumer's own handler, or a third-party widget's — treats a recovered click as synthetic
    /// and can decline it. Script cannot dispatch a trusted event, so that is the price of
    /// recovery rather than something this option can settle; <see cref="None"/> is the answer
    /// for a page that would rather lose the click than deliver an untrusted one. Transient user
    /// activation survives, because the delivery happens inside the browser's own handling of the
    /// displaced click, so an activation-gated API — a clipboard write, a popup, full-screen —
    /// still works from the recovered click.
    /// Three conditions all have to hold, and the last two are what keep the recovery from being
    /// a second, cruder activation path: the press began on a button inside this root; the browser
    /// retargeted the click to an ancestor of that button rather than delivering it; and the
    /// pointer stayed where it was pressed while the button's border box moved. A press dragged
    /// off the button still cancels, and a button merely covered by something that opened over it
    /// is left alone, since intercepting a click is what such an element is for.
    /// </remarks>
    Buttons,
}
