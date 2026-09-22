namespace Formidable.Blazor;

/// <summary>
/// How the live channel decides whether an engaged field's issues are disclosed — the policy
/// behind <see cref="FormidableOptions.LiveDisclosure"/>. The live channel answers only for
/// engaged fields under either member — the ones a committed change has named, and the ones
/// <see cref="IFormValidationEngine.DiscloseLoadedValuesAsync"/> adopted; what differs is whether
/// registration is consulted on top of engagement.
/// </summary>
public enum LiveIssueDisclosure
{
    /// <summary>
    /// Engagement alone discloses (the default): an engaged field's live verdict shows on every
    /// surface — the engine's issue reads and the <c>ValidationMessageStore</c> a native
    /// <c>ValidationMessage</c> renders from — whether or not anything currently renders the
    /// field. This is the stated bridge contract: a native form with no Formidable wrappers or
    /// anchors registers nothing, and its live errors must still reach the store it reads. A
    /// field that LEAVES the page leaves the engaged set with it, which is the one thing
    /// registration decides here — and it decides it by departure, not by absence: a field
    /// something registered and nothing renders any more has left, while a field nothing ever
    /// registered has not left but never arrived, and its verdict stands.
    /// </summary>
    Engaged,

    /// <summary>
    /// Engagement plus visibility: each live issue is additionally gated on the same
    /// override-aware visibility read the submit channel's reveal uses —
    /// <see cref="FormidableOptions.DisclosureOverride"/> first, the field's rendered
    /// registration otherwise — uniformly across every surface, the message store included.
    /// Opting in means exactly what it says: on a page that notifies changes for fields it does
    /// not currently render, an engaged-unrendered field's live errors are hidden everywhere
    /// until the field renders (an override answering <see langword="true"/> still forces one
    /// visible). A field that had rendered and then left is not waiting to be shown again: its
    /// departure ended the engagement outright, so re-rendering restores nothing until the next
    /// committed change files a verdict. A page that only wants a quieter summary should reach for
    /// <c>FormidableSummary</c>'s <c>Show</c> filter instead — that narrows one surface and
    /// leaves disclosure alone.
    /// </summary>
    EngagedAndVisible,
}
