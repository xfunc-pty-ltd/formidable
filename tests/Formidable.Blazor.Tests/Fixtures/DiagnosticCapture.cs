using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Formidable.Blazor.Tests.Fixtures;

/// <summary>
/// Captures the Trace lines written while one call runs. The listener collection is
/// process-wide, so a class running in parallel can write a line while the listener is attached:
/// a test either passes <c>keep</c> to hold only the lines its own call writes, or matches the
/// lines it reads by content rather than by count.
/// </summary>
internal static class TraceCapture
{
    /// <summary>Runs <paramref name="act"/> with a listener attached only for its duration.</summary>
    /// <param name="act">The call whose Trace lines are wanted.</param>
    /// <param name="keep">The lines to return; every line when <see langword="null"/>.</param>
    /// <returns>The kept lines, in the order they were written.</returns>
    public static List<string> Run(Action act, Func<string, bool>? keep = null)
    {
        var listener = new QueueListener();
        Trace.Listeners.Add(listener);
        try
        {
            act();
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }

        return listener.Lines(keep);
    }

    /// <summary>Runs <paramref name="act"/> to completion with a listener attached only for its duration.</summary>
    /// <param name="act">The call whose Trace lines are wanted.</param>
    /// <param name="keep">The lines to return; every line when <see langword="null"/>.</param>
    /// <returns>The kept lines, in the order they were written.</returns>
    public static async Task<List<string>> RunAsync(Func<Task> act, Func<string, bool>? keep = null)
    {
        var listener = new QueueListener();
        Trace.Listeners.Add(listener);
        try
        {
            await act();
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }

        return listener.Lines(keep);
    }

    /// <summary>Records each line in a queue, because a write can arrive from any thread.</summary>
    private sealed class QueueListener : TraceListener
    {
        private readonly ConcurrentQueue<string> _lines = new();

        public override void Write(string? message)
        {
        }

        public override void WriteLine(string? message) => _lines.Enqueue(message ?? string.Empty);

        public List<string> Lines(Func<string, bool>? keep) => [.. _lines.Where(line => keep is null || keep(line))];
    }
}

/// <summary>A logger that records every entry's level and formatted message, from any thread.</summary>
internal sealed class CapturingLogger : ILogger
{
    private readonly ConcurrentQueue<(LogLevel Level, string Message)> _entries = new();

    /// <summary>The entries logged so far, in order.</summary>
    public IReadOnlyList<(LogLevel Level, string Message)> Entries => [.. _entries];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) =>
        _entries.Enqueue((logLevel, formatter(state, exception)));
}

/// <summary>
/// Hands every category the same <see cref="CapturingLogger"/>, for a test that registers logging
/// through <c>LoggerFactory.Create(builder => builder.AddProvider(provider))</c>.
/// </summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly CapturingLogger _logger = new();

    /// <summary>The entries logged through any category so far, in order.</summary>
    public IReadOnlyList<(LogLevel Level, string Message)> Entries => _logger.Entries;

    public ILogger CreateLogger(string categoryName) => _logger;

    public void Dispose()
    {
    }
}
