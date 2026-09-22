namespace Formidable;

/// <summary>The severity of a <see cref="ValidationIssue"/>: an error blocks submission, a warning or an info does not.</summary>
/// <remarks>
/// Member names travel on the wire: the ASP.NET Core package writes a non-error issue's severity
/// to the <c>advisories</c> extension as the member's name, and
/// <see cref="FormidableValidationProblem.ToIssues"/> reads a name it does not recognize as
/// <see cref="Warning"/>.
/// </remarks>
// Renaming a member is a silent wire break, not just an API break: the advisories payload carries
// the name, and a client reading a name it does not recognize falls back to Warning.
// The set is closed. It mirrors FluentValidation.Severity, which is what a rule can declare and so
// all there is to map, and a fourth member would be absorbed by the reads already written rather
// than refused by any of them: the reads that test for Error and take the rest together put it in
// the advisory tier; the three-arm switches behind a message's own class and a summary band's
// heading fall through to Info's; the field-state scan names all three members and falls through
// to nothing, so a field carrying only an issue of the new severity reports as carrying none and
// is free to wear the valid class. Three destinations and no refusal: growing this enum is a
// behaviour change nothing would report.
public enum ValidationSeverity
{
    /// <summary>A failure that blocks submission.</summary>
    Error = 0,

    /// <summary>Shown to the user but does not block submission.</summary>
    Warning = 1,

    /// <summary>Informational only; never blocks submission.</summary>
    Info = 2
}
