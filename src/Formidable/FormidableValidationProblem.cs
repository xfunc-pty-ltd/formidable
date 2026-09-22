namespace Formidable;

/// <summary>
/// Client-side shape of the validation ProblemDetails body produced by Formidable.AspNetCore:
/// the standard <c>errors</c> dictionary keyed by property path plus an <c>advisories</c>
/// extension for non-error issues. Deserialize an HTTP 400 body into this (web JSON defaults,
/// e.g. <c>ReadFromJsonAsync</c>) and pass <see cref="ToIssues"/> to the Blazor engine's
/// server-issue application.
/// </summary>
/// <remarks>
/// Deserialize inside a guard, and treat a <see langword="null"/> result as no verdict. A 400 says
/// the request was rejected, not that the endpoint is what rejected it: a reverse proxy, a gateway
/// or a WAF in front of it answers with its own body, and <c>ReadFromJsonAsync</c> throws
/// <see cref="System.Text.Json.JsonException"/> on one it cannot read into this type — an HTML
/// page, a line of plain text, an empty body — and <see cref="InvalidOperationException"/> when the
/// response's character set is one the runtime does not have. The JSON literal <c>null</c> throws
/// nothing and deserializes to <see langword="null"/>, which the engine's server-issue application
/// rejects. Inside a Blazor event handler each of those is an unhandled exception rather than a
/// message on screen. <see cref="ToIssues"/>'s own tolerance covers the shapes that survive the
/// parse, not the ones that fail it.
/// </remarks>
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

            // Enum.TryParse admits a numeric string ("99", "-1") and a comma-joined list
            // ("Info, Warning") as readily as a member name, so the parse alone would let a
            // foreign body name a severity no member defines — one that then reaches every
            // severity switch and the field-state class provider as an advisory of no band.
            // IsDefined is what keeps "unknown reads as Warning" true of every unknown.
            var severity =
                Enum.TryParse<ValidationSeverity>(advisory.Severity, ignoreCase: true, out var parsed)
                && Enum.IsDefined(parsed)
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
