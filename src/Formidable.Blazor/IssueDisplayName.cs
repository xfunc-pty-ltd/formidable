using Microsoft.AspNetCore.Components.Forms;

namespace Formidable.Blazor;

/// <summary>The one rule that names an issue for display: its display name when it has text, else its path when it has text, else the model-level name.</summary>
// Every name the kit hands out comes from here: each entry GetVisibleIssues returns, each name in a
// submit outcome's VisibleErrorSummary and each item a message list's template receives. One copy
// of the rule is what keeps the name beside an entry equal to the name the count line lists it
// under.
internal static class IssueDisplayName
{
    /// <summary>Names <paramref name="issue"/>: its <see cref="ValidationIssue.DisplayName"/> when it has text, else its <see cref="ValidationIssue.Path"/> when it has text, else <paramref name="modelLevelDisplayName"/>.</summary>
    /// <param name="issue">The issue to name.</param>
    /// <param name="modelLevelDisplayName">The name for an issue that names no field of its own, as <see cref="FormidableOptions.ModelLevelDisplayName"/> reads at the call.</param>
    /// <returns>The name, never empty unless <paramref name="modelLevelDisplayName"/> is.</returns>
    // An empty display name falls through to the path rather than stopping the rule: a name
    // computed from the model (WithName(x => x.NickName ?? "")) or carried on a reply comes out
    // empty for a field that still has a path, and that field is not the form.
    internal static string Resolve(ValidationIssue issue, string modelLevelDisplayName) =>
        issue.DisplayName is { Length: > 0 } displayName ? displayName
        : issue.Path is { Length: > 0 } path ? path
        : modelLevelDisplayName;

    /// <summary>Pairs <paramref name="issue"/> with <paramref name="field"/> under the name <see cref="Resolve"/> gives it.</summary>
    /// <param name="field">The field the issue is listed against.</param>
    /// <param name="issue">The issue.</param>
    /// <param name="modelLevelDisplayName">The name for an issue that names no field of its own, as <see cref="FormidableOptions.ModelLevelDisplayName"/> reads at the call.</param>
    /// <returns>The entry, its <see cref="VisibleIssue.DisplayName"/> set.</returns>
    // The one place a named entry is built, so every entry the kit hands out carries the name
    // this rule gives it.
    internal static VisibleIssue Named(FieldIdentifier field, ValidationIssue issue, string modelLevelDisplayName) =>
        new(field, issue) { DisplayName = Resolve(issue, modelLevelDisplayName) };
}
