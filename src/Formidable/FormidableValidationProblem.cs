namespace Formidable;

/// <summary>The client-side shape of a Formidable.AspNetCore validation 400: the standard <c>errors</c> dictionary keyed by property path plus an <c>advisories</c> extension for non-error issues.</summary>
/// <remarks>
/// Read through <see cref="FormidableValidationProblemJsonContext"/> (a trimmed publish cannot
/// read this type by reflection) inside a guard, and treat a <see langword="null"/> result as no
/// verdict: a proxy, gateway or WAF in front of the endpoint answers a 400 with its own body, on
/// which <c>ReadFromJsonAsync</c> throws <see cref="System.Text.Json.JsonException"/>, and a JSON
/// <c>null</c> deserializes to <see langword="null"/>, which <c>ApplyServerIssues</c> rejects.
/// Unguarded, each is an unhandled exception in a Blazor event handler. Hand the result, or
/// <see cref="ToIssues"/>, to <c>ApplyServerIssues</c> on <c>FormidableForm</c> or <c>FormidableValidator</c>.
/// </remarks>
public sealed class FormidableValidationProblem
{
    /// <summary>Error messages keyed by property path, the standard ValidationProblemDetails shape. Defaults to empty.</summary>
    public Dictionary<string, string[]> Errors { get; set; } = [];

    /// <summary>Non-error issues from the <c>advisories</c> extension. Defaults to empty.</summary>
    public List<ValidationProblemAdvisory> Advisories { get; set; } = [];

    /// <summary>Flattens the body into issues: one per error message, then the advisories, an unknown or <c>Error</c> severity reading as <see cref="ValidationSeverity.Warning"/>.</summary>
    /// <returns>The issues, errors first.</returns>
    /// <remarks>
    /// Tolerates a JSON <c>null</c> anywhere in the body: a null advisory is skipped, and a null
    /// path or message reads as empty. The tolerance covers shapes that survive the parse, not
    /// ones that fail it.
    /// </remarks>
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
            // "Error" is refused for a different reason: the errors dictionary is the only error
            // channel, so an advisory claiming it is downgraded to Warning.
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
