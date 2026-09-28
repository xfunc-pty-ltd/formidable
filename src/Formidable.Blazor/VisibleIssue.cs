using Microsoft.AspNetCore.Components.Forms;

namespace Formidable.Blazor;

/// <summary>An issue currently showing, paired with the field it resolved to.</summary>
/// <param name="Field">The resolved field; the model-level identifier for a form-level issue.</param>
/// <param name="Issue">The issue, of any severity.</param>
/// <remarks>Grows by init-only properties, never by constructor parameters, so code constructing it keeps compiling.</remarks>
public sealed record VisibleIssue(FieldIdentifier Field, ValidationIssue Issue)
{
    /// <summary>The name the issue is listed under: its <see cref="ValidationIssue.DisplayName"/>, or its <see cref="ValidationIssue.Path"/> when it has none, or <see cref="FormidableOptions.ModelLevelDisplayName"/> when that is empty. <see langword="null"/> on an instance built by hand.</summary>
    /// <remarks>It takes no part in the record's equality, because it is derived from the issue and an option, so an instance built by hand equals the one <see cref="IFormidableEngine.GetVisibleIssues"/> returns for the same field and issue.</remarks>
    public string? DisplayName { get; init; }

    /// <summary>Whether <paramref name="other"/> pairs the same field with an equal issue; <see cref="DisplayName"/> takes no part.</summary>
    /// <param name="other">The instance to compare with.</param>
    /// <returns><see langword="true"/> when <paramref name="other"/> is not <see langword="null"/> and its <see cref="Field"/> and <see cref="Issue"/> equal this instance's.</returns>
    public bool Equals(VisibleIssue? other) =>
        ReferenceEquals(this, other)
        || (other is not null
            && Field.Equals(other.Field)
            && EqualityComparer<ValidationIssue>.Default.Equals(Issue, other.Issue));

    /// <summary>Hashes the <see cref="Field"/> and <see cref="Issue"/> that <see cref="Equals(VisibleIssue)"/> compares, so equal instances hash equal.</summary>
    /// <returns>The hash code.</returns>
    public override int GetHashCode() => HashCode.Combine(Field, Issue);
}
