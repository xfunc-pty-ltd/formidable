namespace Formidable;

/// <summary>A single validation finding for a property path.</summary>
/// <param name="Path">Property path in FluentValidation format, e.g. <c>LineItems[0].Quantity</c>.</param>
/// <param name="Message">Human-readable message.</param>
/// <param name="Severity">Issue severity. Defaults to <see cref="ValidationSeverity.Error"/>.</param>
/// <param name="Code">Optional machine-readable code (FluentValidation error code).</param>
/// <param name="DisplayName">Optional user-facing field name (from <c>WithName(...)</c>).</param>
public sealed record ValidationIssue(
    string Path,
    string Message,
    ValidationSeverity Severity = ValidationSeverity.Error,
    string? Code = null,
    string? DisplayName = null);
