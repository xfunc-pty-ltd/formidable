namespace Formidable;

/// <summary>Severity of a <see cref="ValidationIssue"/>.</summary>
public enum ValidationSeverity
{
    /// <summary>A failure that blocks submission.</summary>
    Error = 0,

    /// <summary>Shown to the user but does not block submission.</summary>
    Warning = 1,

    /// <summary>Informational only; never blocks submission.</summary>
    Info = 2
}
