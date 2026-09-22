namespace Formidable;

/// <summary>One non-error issue as the <c>advisories</c> extension of a validation ProblemDetails body carries it.</summary>
/// <param name="Path">The property path in the client's format, such as <c>Items[0].Sku</c>.</param>
/// <param name="Message">The message shown to the user.</param>
/// <param name="Severity">The <see cref="ValidationSeverity"/> member name, <c>Warning</c> or <c>Info</c>; <see cref="FormidableValidationProblem.ToIssues"/> reads any other value, <c>Error</c> included, as <c>Warning</c>.</param>
/// <param name="Code">The machine-readable code, or <see langword="null"/>.</param>
/// <param name="DisplayName">The user-facing field name, or <see langword="null"/>.</param>
public sealed record ValidationProblemAdvisory(
    string Path,
    string Message,
    string Severity,
    string? Code = null,
    string? DisplayName = null);
