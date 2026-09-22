namespace Formidable.Blazor;

/// <summary>Whether a root re-delivers a click the page moved out from under a still pointer between the press and the release, and to which elements.</summary>
// The browser fires a click only when the press and the release share a target; otherwise it
// dispatches on their nearest common ancestor, where nothing listens. That rule is written in
// terms of targets while the visitor's intent lives in pointer movement, so a button that moves
// out from under a still pointer looks exactly like a pointer dragged off a still button.
// Recovery supplies the discrimination the platform cannot make.
public enum DisplacedClickRecovery
{
    /// <summary>No guard: a click the page displaced is lost as the browser leaves it, for a page that moves its own controls during a press.</summary>
    None,

    /// <summary>Recovers a click a button inside the root lost by moving out from under the pointer; a button is a <c>&lt;button&gt;</c>, <c>&lt;input type="submit"&gt;</c> or <c>&lt;input type="image"&gt;</c>. The default.</summary>
    /// <remarks>
    /// The click is re-delivered only when the press began on the button, the browser sent the
    /// click to an ancestor instead, and the pointer stayed within a few pixels while the
    /// button's box moved; a drag off the button still cancels, and a button something opened
    /// over is left alone. The re-delivered click is script-dispatched, so <c>isTrusted</c> reads
    /// <see langword="false"/> on its path, while transient user activation survives. One press
    /// stays one click: the misdirected click is stopped.
    /// </remarks>
    // The default because the defect reaches a consumer who never reads a word of documentation.
    // Script cannot dispatch a trusted event, so an untrusted click is the price of recovery
    // rather than something this option can settle; None is the answer for a page that would
    // rather lose the click than deliver an untrusted one.
    Buttons,
}
