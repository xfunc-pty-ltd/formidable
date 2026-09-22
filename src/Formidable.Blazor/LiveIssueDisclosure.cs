namespace Formidable.Blazor;

/// <summary>Which engaged fields' live messages show: every engaged field, or only those on screen. The policy behind <see cref="FormidableOptions.LiveDisclosure"/>.</summary>
public enum LiveIssueDisclosure
{
    /// <summary>An engaged field's live messages show on every surface, rendered or not, until the field leaves the page. The default.</summary>
    Engaged,

    /// <summary>An engaged field's live messages show only where the field is rendered or <see cref="FormidableOptions.DisclosureOverride"/> answers yes, on every surface alike.</summary>
    /// <remarks>
    /// A field that leaves the page stops being engaged, so rendering it again shows nothing
    /// until a committed change or a load engages it again. For a summary that lists less without
    /// hiding anything, use <c>FormidableSummary</c>'s <c>Show</c> filter instead.
    /// </remarks>
    EngagedAndVisible,
}
