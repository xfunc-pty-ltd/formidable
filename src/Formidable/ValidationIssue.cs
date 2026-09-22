namespace Formidable;

/// <summary>A single validation finding for a property path.</summary>
/// <remarks>
/// The member set is deliberately complete: every member folds into the record's synthesized
/// equality, so an addition would change what <c>Equals</c> means for every consumer comparing,
/// set-keying, or deduplicating issues.
/// </remarks>
/// <param name="Path">Property path in FluentValidation format, e.g. <c>LineItems[0].Quantity</c>.</param>
/// <param name="Message">Human-readable message.</param>
/// <param name="Severity">Issue severity. Defaults to <see cref="ValidationSeverity.Error"/>.</param>
/// <param name="Code">Optional machine-readable code (FluentValidation error code).</param>
/// <param name="DisplayName">Optional user-facing field name (from <c>WithName(...)</c>).</param>
/// <param name="State">Optional caller-defined payload — FluentValidation's <c>WithState(...)</c>
/// channel, carried over from <c>ValidationFailure.CustomState</c> wherever failures map to
/// issues. Participates in the record's equality through <c>object.Equals</c>: for a state type
/// that does not override <c>Equals</c>, two issues differing only in state instance compare
/// unequal. Issues built from a ProblemDetails body carry <see langword="null"/> — the wire
/// shape has no state channel, so state never crosses the HTTP boundary.</param>
public sealed record ValidationIssue(
    string Path,
    string Message,
    ValidationSeverity Severity = ValidationSeverity.Error,
    string? Code = null,
    string? DisplayName = null,
    object? State = null);
