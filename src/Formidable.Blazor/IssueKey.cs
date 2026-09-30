using Microsoft.AspNetCore.Components.Forms;

namespace Formidable.Blazor;

/// <summary>What makes a listed issue the same issue from one render to the next: its field and every member of the issue but its state.</summary>
/// <param name="Field">The field the issue is listed against.</param>
/// <param name="Path">The issue's <see cref="ValidationIssue.Path"/>.</param>
/// <param name="Message">The issue's <see cref="ValidationIssue.Message"/>.</param>
/// <param name="Severity">The issue's <see cref="ValidationIssue.Severity"/>.</param>
/// <param name="Code">The issue's <see cref="ValidationIssue.Code"/>.</param>
/// <param name="DisplayName">The issue's <see cref="ValidationIssue.DisplayName"/>.</param>
// The state stays out because it compares through object.Equals: a WithState payload of a
// plain class is a new instance each time its rule runs, so an issue carrying one would be a
// different key after every re-check, and its summary entry or message item a different
// element, losing focus and being announced again for saying the same thing.
internal readonly record struct IssueKey(
    FieldIdentifier Field,
    string Path,
    string Message,
    ValidationSeverity Severity,
    string? Code,
    string? DisplayName)
{
    /// <summary>The key of <paramref name="issue"/> listed against <paramref name="field"/>.</summary>
    /// <param name="field">The field the issue is listed against.</param>
    /// <param name="issue">The issue.</param>
    /// <returns>The key, equal for any two issues that differ in nothing but their state.</returns>
    internal static IssueKey Of(FieldIdentifier field, ValidationIssue issue) =>
        new(field, issue.Path, issue.Message, issue.Severity, issue.Code, issue.DisplayName);
}
