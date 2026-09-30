using Microsoft.Extensions.Logging;

namespace Formidable.Blazor;

/// <summary>Writes a diagnostic to <see cref="System.Diagnostics.Trace"/> and, when the host has an <see cref="ILoggerFactory"/>, as a logged entry.</summary>
// Every diagnostic this package writes goes through here rather than hand-rolling the pair,
// whether its writer resolves a logger at each write (the components) or holds one (the engine).
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
    // Two overloads rather than one, because the call sites format two different ways: some
    // write one finished string to both channels, the others thread the values their template
    // names through a structured template whose placeholders read nothing like the Trace line's
    // own interpolation. The two channels carry the same information rendered through each
    // channel's own convention.
    internal static void Warn(ILogger? logger, string traceMessage, string logTemplate, params object?[] args) =>
        Write(logger, LogLevel.Warning, traceMessage, logTemplate, args);

    /// <summary>Writes <paramref name="traceMessage"/> to Trace and, when <paramref name="logger"/> is given, logs <paramref name="logTemplate"/> with <paramref name="args"/> at <paramref name="level"/>.</summary>
    /// <param name="logger">The resolved logger, or <see langword="null"/> when the host registered no <see cref="ILoggerFactory"/>.</param>
    /// <param name="level">The level the logged entry carries; the Trace line carries none.</param>
    /// <param name="traceMessage">The line the Trace channel gets, already formatted.</param>
    /// <param name="logTemplate">The structured logging template, with placeholders <paramref name="args"/> fills.</param>
    /// <param name="args">The values the template's placeholders bind to, in order.</param>
    // For a diagnostic that is not a warning: the engine's note on a validator whose rules cannot
    // be read logs at Information, because that is a supported configuration.
    internal static void Write(ILogger? logger, LogLevel level, string traceMessage, string logTemplate, params object?[] args)
    {
        System.Diagnostics.Trace.WriteLine(traceMessage);
        logger?.Log(level, logTemplate, args);
    }
}
