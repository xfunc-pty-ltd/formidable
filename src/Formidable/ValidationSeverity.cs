namespace Formidable;

/// <summary>Severity of a <see cref="ValidationIssue"/>.</summary>
/// <remarks>
/// The member names are wire contract as well as API: the ASP.NET Core package's
/// <c>ValidationReportProblemMapper.ToAdvisories</c> writes a non-error issue's severity onto
/// the <c>advisories</c> wire payload as this enum's member name, and
/// <see cref="FormidableValidationProblem.ToIssues"/> parses the name back, reading one it does
/// not recognize as <see cref="Warning"/> — so renaming a member is a silent wire break, not
/// just an API break.
/// </remarks>
public enum ValidationSeverity
{
    /// <summary>A failure that blocks submission.</summary>
    Error = 0,

    /// <summary>Shown to the user but does not block submission.</summary>
    Warning = 1,

    /// <summary>Informational only; never blocks submission.</summary>
    Info = 2
}
