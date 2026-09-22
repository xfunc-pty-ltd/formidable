namespace Formidable.Blazor;

/// <summary>Which severities a <see cref="FormidableSummary"/> lists, for a page that shows errors and advisories apart.</summary>
public enum SummaryFilter
{
    /// <summary>Every severity in one combined list. The default.</summary>
    All,

    /// <summary>Only <see cref="ValidationSeverity.Error"/> issues.</summary>
    Errors,

    /// <summary>Every non-error issue, as <see cref="ValidationReport.Advisories"/> counts them: <see cref="ValidationSeverity.Warning"/> and <see cref="ValidationSeverity.Info"/>.</summary>
    Advisories,

    /// <summary>Only <see cref="ValidationSeverity.Warning"/> issues.</summary>
    Warnings,

    /// <summary>Only <see cref="ValidationSeverity.Info"/> issues.</summary>
    Infos,
}
