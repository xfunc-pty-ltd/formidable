namespace Formidable;

/// <summary>
/// Client-side shape of the validation ProblemDetails body produced by Formidable.AspNetCore:
/// the standard <c>errors</c> dictionary keyed by property path plus an <c>advisories</c>
/// extension for non-error issues. Deserialize an HTTP 400 body into this (web JSON defaults,
/// e.g. <c>ReadFromJsonAsync</c>) and pass <see cref="ToIssues"/> to the Blazor engine's
/// server-issue application.
/// </summary>
public sealed class FormidableValidationProblem
{
    /// <summary>Error messages keyed by property path (standard ValidationProblemDetails shape).</summary>
    public Dictionary<string, string[]> Errors { get; set; } = [];

    /// <summary>Non-error issues from the <c>advisories</c> extension.</summary>
    public List<ValidationProblemAdvisory> Advisories { get; set; } = [];

    /// <summary>
    /// Flattens the payload into engine-ready issues: error entries first (one issue per
    /// message), then advisories with their severity parsed case-insensitively — unknown or
    /// "Error" severities read as <see cref="ValidationSeverity.Warning"/>, because the
    /// errors dictionary is the only error channel.
    /// </summary>
    public IReadOnlyList<ValidationIssue> ToIssues()
    {
        var issues = new List<ValidationIssue>();

        // A foreign 400 body can carry explicit JSON nulls anywhere its shape admits one — the
        // collections themselves, a key's message array, an element inside that array, a whole
        // advisory entry, or an advisory's fields (the deserializer doesn't enforce
        // nullable-reference annotations) — tolerate every shape rather than throw. A null
        // advisory entry carries nothing to show, so it is skipped; a null path reads as ""
        // (the model-level path); a null message reads as "".
        var errors = Errors ?? new Dictionary<string, string[]>();
        var advisories = Advisories ?? [];

        foreach (var (path, messages) in errors)
        {
            issues.AddRange((messages ?? []).Select(message => new ValidationIssue(path, message ?? string.Empty)));
        }

        foreach (var advisory in advisories)
        {
            if (advisory is null)
            {
                continue;
            }

            var severity =
                Enum.TryParse<ValidationSeverity>(advisory.Severity, ignoreCase: true, out var parsed)
                && parsed != ValidationSeverity.Error
                    ? parsed
                    : ValidationSeverity.Warning;

            issues.Add(new ValidationIssue(
                advisory.Path ?? string.Empty,
                advisory.Message ?? string.Empty,
                severity,
                advisory.Code,
                advisory.DisplayName));
        }

        return issues;
    }
}
