namespace Formidable.Blazor;

/// <summary>
/// Neutralizes a <see cref="ValidationIssue.Path"/> before it is echoed into a Trace line or a
/// formatted log message — the two diagnostics that report a suppressed or unreachable issue.
/// <see cref="ValidationIssue.Path"/> is payload-supplied on the <c>ApplyServerIssues</c> route,
/// so nothing upstream constrains its shape: a crafted carriage return or line feed would split
/// the Trace line or the formatted message in two, and an unbounded one costs the line whatever
/// length the payload names. The structured <c>{Path}</c> property on the logged template
/// protects a structured sink's own field, never the Trace line or the text a plain formatter
/// builds from that same template — both channels need the path sanitized before either sees it,
/// not just the logger's own argument. Lives beside its two callers rather than in
/// <c>src/Shared</c>: that file is link-shared into <c>Formidable.AspNetCore</c> as well, which
/// has no diagnostic echoing a payload-supplied path, so sharing it there would widen a
/// hardening fix's reach to a host it protects nothing on.
/// </summary>
internal static class DiagnosticPathSanitizer
{
    /// <summary>
    /// The longest path let through unmarked. FluentValidation property paths, indexed rows
    /// included, run tens of characters; a payload-supplied one is whatever the sender wrote.
    /// Mirrors <c>ReflectionModelIntrospector.MaxCachedPathLength</c>'s reasoning for the same
    /// payload-supplied path: generous for every real path, still a cap on a forged one.
    /// </summary>
    private const int MaxLength = 256;

    /// <summary>
    /// Replaces every line-ending sequence <see cref="string.ReplaceLineEndings(string)"/>
    /// recognizes — CR, LF, CRLF, form feed, NEL, and the Unicode line and paragraph
    /// separators — with a single space, so the result can never split the single line a Trace
    /// entry or a formatted log message is meant to occupy, then caps the result at
    /// <see cref="MaxLength"/> — appending a marker naming how much was cut, rather than
    /// truncating silently to something that reads as a real, if short, path.
    /// </summary>
    internal static string ForDiagnostic(string path)
    {
        var oneLine = path.ReplaceLineEndings(" ");
        return oneLine.Length <= MaxLength
            ? oneLine
            : $"{oneLine[..MaxLength]}…[+{oneLine.Length - MaxLength} more]";
    }
}
