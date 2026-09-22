using Microsoft.Extensions.Logging;

namespace Formidable.Blazor;

/// <summary>Writes a component-side diagnostic to <see cref="System.Diagnostics.Trace"/> and, when the host has an <see cref="ILoggerFactory"/>, as a logged warning.</summary>
// The diagnostics written from components resolve a logger at each write rather than holding
// one the way the engine does, and write through here rather than each hand-rolling the pair.
internal static class FormidableDiagnostics
{
    /// <summary>Writes <paramref name="message"/> to Trace and, when <paramref name="logger"/> is given, as a warning.</summary>
    /// <param name="logger">The resolved logger, or <see langword="null"/> when the host registered no <see cref="ILoggerFactory"/>.</param>
    /// <param name="message">The text both channels carry, verbatim.</param>
    internal static void Warn(ILogger? logger, string message)
    {
        System.Diagnostics.Trace.WriteLine(message);
        logger?.LogWarning(message);
    }

    /// <summary>Writes <paramref name="traceMessage"/> to Trace and, when <paramref name="logger"/> is given, logs <paramref name="logTemplate"/> with <paramref name="args"/> as a warning.</summary>
    /// <param name="logger">The resolved logger, or <see langword="null"/> when the host registered no <see cref="ILoggerFactory"/>.</param>
    /// <param name="traceMessage">The line the Trace channel gets, already formatted.</param>
    /// <param name="logTemplate">The structured logging template, with placeholders <paramref name="args"/> fills.</param>
    /// <param name="args">The values the template's placeholders bind to, in order.</param>
    // Two overloads rather than one, because the call sites format two different ways: one
    // writes the same constant string to both channels, the other threads a sanitized value
    // through a structured template whose placeholders read nothing like the Trace line's own
    // interpolation. The two channels carry the same information rendered through each channel's
    // own convention.
    internal static void Warn(ILogger? logger, string traceMessage, string logTemplate, params object?[] args)
    {
        System.Diagnostics.Trace.WriteLine(traceMessage);
        logger?.LogWarning(logTemplate, args);
    }
}
