namespace Formidable;

/// <summary>Severity of a <see cref="ValidationIssue"/>.</summary>
/// <remarks>
/// The member names are wire contract as well as API: the ASP.NET Core package's
/// <c>ValidationReportProblemMapper.ToAdvisories</c> writes a non-error issue's severity onto
/// the <c>advisories</c> wire payload as this enum's member name, and
/// <see cref="FormidableValidationProblem.ToIssues"/> parses the name back, reading one it does
/// not recognize as <see cref="Warning"/> — so renaming a member is a silent wire break, not
/// just an API break.
/// <para>
/// The set is closed. It mirrors <see cref="FluentValidation.Severity"/>, which is what a rule
/// can declare and so all there is to map, and a fourth member would be absorbed by the reads
/// already written rather than refused by any of them. The reads that test for
/// <see cref="Error"/> and take the rest together put it in the advisory tier. The three-arm
/// switches behind a message's own class and a summary band's heading fall through to
/// <see cref="Info"/>'s. The field-state scan names all three members and falls through to
/// nothing, so a field carrying only an issue of the new severity reports as carrying none at
/// all and is free to wear the valid class. Three destinations and no refusal: growing this
/// enum is a behaviour change nothing would report.
/// </para>
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
