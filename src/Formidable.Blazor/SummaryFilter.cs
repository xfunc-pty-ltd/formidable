namespace Formidable.Blazor;

/// <summary>
/// Selects which severity band <see cref="FormidableSummary"/> renders, for a page that wants
/// errors and advisories apart instead of one combined list.
/// </summary>
public enum SummaryFilter
{
    /// <summary>Every severity — today's single combined summary. The default.</summary>
    All,

    /// <summary>Only <see cref="ValidationSeverity.Error"/> issues.</summary>
    Errors,

    /// <summary>
    /// Every non-error issue — <see cref="ValidationSeverity.Warning"/> and
    /// <see cref="ValidationSeverity.Info"/> together, matching <see cref="ValidationReport.Advisories"/>.
    /// </summary>
    Advisories,

    /// <summary>Only <see cref="ValidationSeverity.Warning"/> issues.</summary>
    Warnings,

    /// <summary>Only <see cref="ValidationSeverity.Info"/> issues.</summary>
    Infos,
}
