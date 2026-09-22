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

        // A foreign 400 body can carry explicit JSON nulls that override the property
        // initializers below (the deserializer doesn't enforce nullable-reference annotations),
        // and a key's message array itself can be null — tolerate both rather than throw.
        var errors = Errors ?? new Dictionary<string, string[]>();
        var advisories = Advisories ?? [];

        foreach (var (path, messages) in errors)
        {
            issues.AddRange((messages ?? []).Select(message => new ValidationIssue(path, message)));
        }

        foreach (var advisory in advisories)
        {
            var severity =
                Enum.TryParse<ValidationSeverity>(advisory.Severity, ignoreCase: true, out var parsed)
                && parsed != ValidationSeverity.Error
                    ? parsed
                    : ValidationSeverity.Warning;

            issues.Add(new ValidationIssue(advisory.Path, advisory.Message, severity, advisory.Code, advisory.DisplayName));
        }

        return issues;
    }
}
