namespace Formidable;

/// <summary>A single validation finding for a property path.</summary>
/// <remarks>
/// Every member takes part in the record's equality; <paramref name="State"/> compares through
/// <c>object.Equals</c>, so two issues differing only in a state instance whose type does not
/// override <c>Equals</c> are unequal.
/// </remarks>
/// <param name="Path">The property path in FluentValidation's format, such as <c>LineItems[0].Quantity</c>.</param>
/// <param name="Message">The message shown to the user.</param>
/// <param name="Severity">The issue's severity. Defaults to <see cref="ValidationSeverity.Error"/>.</param>
/// <param name="Code">The machine-readable code (FluentValidation's error code), or <see langword="null"/>.</param>
/// <param name="DisplayName">The user-facing field name (from <c>WithName(...)</c>), or <see langword="null"/>.</param>
/// <param name="State">The caller-defined payload from FluentValidation's <c>WithState(...)</c>; <see langword="null"/> for an issue parsed from a ProblemDetails body, whose wire shape carries no state.</param>
// The member set is deliberately complete: every member folds into the record's synthesized
// equality, so an addition would change what Equals means for every consumer comparing,
// set-keying or deduplicating issues.
public sealed record ValidationIssue(
    string Path,
    string Message,
    ValidationSeverity Severity = ValidationSeverity.Error,
    string? Code = null,
    string? DisplayName = null,
    object? State = null);
