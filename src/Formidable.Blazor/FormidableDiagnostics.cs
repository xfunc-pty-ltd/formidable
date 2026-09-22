using Microsoft.Extensions.Logging;

namespace Formidable.Blazor;

/// <summary>
/// The dual-channel write two of the library's own diagnostics share: a
/// <see cref="System.Diagnostics.Trace"/> line, always, plus a logged warning when the host
/// resolved an <see cref="ILoggerFactory"/> — logging is additive, so a consumer who never
/// registered one still gets the Trace line alone. <see cref="FirstErrorFocus"/>'s focus-miss
/// diagnostic and <see cref="FormidableValidator{TModel}"/>'s missing-click-recovery-root
/// diagnostic both write through here rather than each hand-rolling the same pair. Two overloads
/// rather than one, because the two call sites format differently and neither shape should be
/// bent to fit the other: one writes the same constant string to both channels, the other
/// threads a sanitized value through a structured logging template whose placeholders read
/// nothing like the Trace line's own interpolation.
/// </summary>
internal static class FormidableDiagnostics
{
    /// <summary>
    /// Writes <paramref name="message"/> to both channels unchanged.
    /// </summary>
    /// <param name="logger">The resolved logger, or null when the host registered no
    /// <see cref="ILoggerFactory"/> — <paramref name="message"/> still reaches the Trace
    /// channel.</param>
    /// <param name="message">The text both channels carry, verbatim.</param>
    internal static void Warn(ILogger? logger, string message)
    {
        System.Diagnostics.Trace.WriteLine(message);
        logger?.LogWarning(message);
    }

    /// <summary>
    /// Writes <paramref name="traceMessage"/> to the Trace channel and, when
    /// <paramref name="logger"/> is not null, logs <paramref name="logTemplate"/> with
    /// <paramref name="args"/> as a structured warning — the two channels carry the same
    /// information rendered through each channel's own convention, a pre-formatted line for
    /// Trace and a message template for the logger, rather than one string copied to both.
    /// </summary>
    /// <param name="logger">The resolved logger, or null when the host registered no
    /// <see cref="ILoggerFactory"/> — <paramref name="traceMessage"/> still reaches the Trace
    /// channel.</param>
    /// <param name="traceMessage">The line the Trace channel gets, already fully formatted.</param>
    /// <param name="logTemplate">The structured logging template the logger gets, with
    /// placeholders <paramref name="args"/> fills.</param>
    /// <param name="args">The values <paramref name="logTemplate"/>'s placeholders bind to, in
    /// order.</param>
    internal static void Warn(ILogger? logger, string traceMessage, string logTemplate, params object?[] args)
    {
        System.Diagnostics.Trace.WriteLine(traceMessage);
        logger?.LogWarning(logTemplate, args);
    }
}
