namespace Formidable.Blazor;

/// <summary>Neutralizes a <see cref="ValidationIssue.Path"/> before a diagnostic Trace line or log message echoes it.</summary>
// ValidationIssue.Path is payload-supplied on the ApplyServerIssues route, so nothing upstream
// constrains its shape: a crafted carriage return or line feed would split the Trace line or the
// formatted message in two, and an unbounded one costs the line whatever length the payload
// names. The structured {Path} property on a logged template protects a structured sink's own
// field, never the Trace line or the text a plain formatter builds from that same template, so
// both channels need the path sanitized before either sees it. Lives beside its two callers
// (the suppressed-issue and the focus-miss diagnostics) rather than in src/Shared: that file is
// link-shared into Formidable.AspNetCore, which echoes no payload-supplied path.
internal static class DiagnosticPathSanitizer
{
    /// <summary>The longest path echoed unmarked; a longer one is cut and marked.</summary>
    // FluentValidation property paths, indexed rows included, run tens of characters; a
    // payload-supplied one is whatever the sender wrote. Mirrors
    // ReflectionModelIntrospector.MaxCachedPathLength's reasoning for the same payload-supplied
    // path: generous for every real path, still a cap on a forged one.
    private const int MaxLength = 256;

    /// <summary>The character that replaces each control character in an echoed path.</summary>
    // Visible rather than a space, so a path that carried one shows where it was instead of
    // reading as a real, if odd, path.
    private const char Placeholder = '?';

    /// <summary>Returns <paramref name="path"/> as one line with each control character replaced and, past <see cref="MaxLength"/> characters, cut with a marker naming how many were dropped.</summary>
    /// <param name="path">The issue path, as the payload supplied it.</param>
    /// <returns>The sanitized path, ending in a marker naming how many characters were cut when any were.</returns>
    internal static string ForDiagnostic(string path)
    {
        // Every line ending ReplaceLineEndings recognizes (a carriage return, a line feed, the
        // pair, a form feed, a next-line character and the Unicode line and paragraph separators)
        // becomes one space, so none can split the single line a Trace entry or a formatted log
        // message occupies. Then every control character still standing (the C0 range, delete
        // and the C1 range) becomes the placeholder, so an escape sequence cannot redraw the line
        // on a terminal that honours one, a backspace cannot overwrite what the line already
        // said, and a bell cannot ring. Two passes rather than one, because neither covers the
        // other: a line or paragraph separator is not a control character, and a carriage return
        // and line feed pair is one line ending rather than two placeholders. The cap appends a
        // marker naming how much was cut rather than truncating silently to something that reads
        // as a real, if short, path.
        var oneLine = path.ReplaceLineEndings(" ");
        var cut = Math.Max(0, oneLine.Length - MaxLength);
        var visible = string.Concat(oneLine.Take(MaxLength).Select(c => char.IsControl(c) ? Placeholder : c));
        return cut == 0 ? visible : $"{visible}…[+{cut} more]";
    }
}
