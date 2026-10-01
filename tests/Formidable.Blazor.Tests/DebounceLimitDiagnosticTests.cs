using System.Collections.Concurrent;
using System.Diagnostics;
using FluentValidation;
using Formidable.Introspection;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Formidable.Blazor.Tests;

/// <summary>
/// The note an engine writes as it is built when <see cref="FormidableOptions.RefreshDebounce"/>
/// or <see cref="FormidableOptions.LiveDebounce"/> is negative other than
/// <see cref="Timeout.InfiniteTimeSpan"/>, or past the longest wait the system timer takes, which
/// is <c>uint.MaxValue - 1</c> milliseconds once any fraction is truncated. The Trace listener is
/// process-wide, so every capture is filtered to lines naming <see cref="DebounceLimitModel"/>, a
/// model no other test class uses. Each test only builds the engine: the fake clock's timers
/// take the same range as the system timer's, so arming one would throw.
/// </summary>
public class DebounceLimitDiagnosticTests
{
    // The longest due time the system timer accepts, and the shortest it rejects.
    private static readonly TimeSpan AtTheLimit = TimeSpan.FromMilliseconds(uint.MaxValue - 1);
    private static readonly TimeSpan PastTheLimit = TimeSpan.FromMilliseconds(uint.MaxValue);

    // Mutation: drop the check, and nothing is written.
    [Fact]
    public void A_RefreshDebounce_beyond_the_timer_limit_is_named_once_at_build()
    {
        var logger = new CapturingLogger();

        var lines = CaptureTrace(() =>
        {
            using var engine = BuildEngine(new FormidableOptions { RefreshDebounce = PastTheLimit }, logger);
        });

        var line = Assert.Single(lines);
        AssertNames(line, "FormidableOptions.RefreshDebounce", "49.17:02:47.2950000");

        var warning = Assert.Single(logger.Entries, entry => entry.Message.Contains("cannot use as a wait"));
        Assert.Equal(LogLevel.Warning, warning.Level);
        AssertNames(warning.Message, "FormidableOptions.RefreshDebounce", "49.17:02:47.2950000");
    }

    // Mutation: check RefreshDebounce only, and a LiveDebounce past the limit goes unnamed.
    [Fact]
    public void A_LiveDebounce_beyond_the_timer_limit_is_named_once_at_build()
    {
        var logger = new CapturingLogger();

        var lines = CaptureTrace(() =>
        {
            using var engine = BuildEngine(new FormidableOptions { LiveDebounce = TimeSpan.FromDays(50) }, logger);
        });

        var line = Assert.Single(lines);
        AssertNames(line, "FormidableOptions.LiveDebounce", "50.00:00:00");
        Assert.DoesNotContain("RefreshDebounce", line);

        var warning = Assert.Single(logger.Entries, entry => entry.Message.Contains("cannot use as a wait"));
        Assert.Equal(LogLevel.Warning, warning.Level);
        AssertNames(warning.Message, "FormidableOptions.LiveDebounce", "50.00:00:00");
    }

    // Every negative value but Timeout.InfiniteTimeSpan itself: -2 ms and below, which the timer
    // rejects; a sliver above -2 ms or below -1 ms, which it reads as the infinite wait while the
    // form reads a finite one; and a sliver below zero, which it reads as zero. Mutation: check
    // the upper limit only, and none is named; or take the timer's own lower bound (whole
    // milliseconds down to -1), and the three rows above -2 ms are not.
    [Theory]
    [InlineData(nameof(FormidableOptions.RefreshDebounce), -20000L, "-00:00:00.0020000")]
    [InlineData(nameof(FormidableOptions.LiveDebounce), -10000000L, "-00:00:01")]
    [InlineData(nameof(FormidableOptions.LiveDebounce), -19999L, "-00:00:00.0019999")]
    [InlineData(nameof(FormidableOptions.RefreshDebounce), -10001L, "-00:00:00.0010001")]
    [InlineData(nameof(FormidableOptions.LiveDebounce), -1L, "-00:00:00.0000001")]
    public void A_negative_debounce_other_than_infinite_is_named_once_at_build(
        string option, long ticks, string rendered)
    {
        var logger = new CapturingLogger();
        var options = With(option, TimeSpan.FromTicks(ticks));

        var lines = CaptureTrace(() =>
        {
            using var engine = BuildEngine(options, logger);
        });

        AssertNames(Assert.Single(lines), $"FormidableOptions.{option}", rendered);
        AssertNames(
            Assert.Single(logger.Entries, entry => entry.Message.Contains("cannot use as a wait")).Message,
            $"FormidableOptions.{option}",
            rendered);
    }

    // A pin, passing before the check existed because nothing was named then. The limit itself
    // and Timeout.InfiniteTimeSpan both arm. Mutation: compare with >= against the limit, and the
    // limit is named; or name every negative, and the infinite wait is.
    [Theory]
    [InlineData(nameof(FormidableOptions.RefreshDebounce), false)]
    [InlineData(nameof(FormidableOptions.RefreshDebounce), true)]
    [InlineData(nameof(FormidableOptions.LiveDebounce), false)]
    [InlineData(nameof(FormidableOptions.LiveDebounce), true)]
    public void A_debounce_at_the_limit_or_infinite_is_not_named(string option, bool infinite)
    {
        var logger = new CapturingLogger();
        var options = With(option, infinite ? Timeout.InfiniteTimeSpan : AtTheLimit);

        var lines = CaptureTrace(() =>
        {
            using var engine = BuildEngine(options, logger);
        });

        Assert.Empty(lines);
        Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains("cannot use as a wait"));
    }

    // A pin, passing before the check existed because nothing was named then. The timer
    // truncates a due time to whole milliseconds before it checks its upper limit, so a wait just
    // under a millisecond past the limit arms as the limit. Mutation: compare the TimeSpan with
    // the limit rather than its whole milliseconds, and it is named.
    [Theory]
    [InlineData(nameof(FormidableOptions.RefreshDebounce), 42949672949999L)]
    [InlineData(nameof(FormidableOptions.LiveDebounce), 42949672940001L)]
    public void A_debounce_the_timer_truncates_to_its_limit_is_not_named(string option, long ticks)
    {
        var logger = new CapturingLogger();
        var options = With(option, TimeSpan.FromTicks(ticks));

        var lines = CaptureTrace(() =>
        {
            using var engine = BuildEngine(options, logger);
        });

        Assert.Empty(lines);
        Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains("cannot use as a wait"));
    }

    private static void AssertNames(string message, string option, string rendered)
    {
        Assert.Contains(option, message);
        Assert.Contains(nameof(DebounceLimitModel), message);
        Assert.Contains($"is {rendered},", message);
        Assert.Contains("4294967294 milliseconds", message);
        Assert.Contains("Timeout.InfiniteTimeSpan", message);
    }

    private static FormidableOptions With(string option, TimeSpan value) =>
        option == nameof(FormidableOptions.RefreshDebounce)
            ? new FormidableOptions { RefreshDebounce = value }
            : new FormidableOptions { LiveDebounce = value };

    private static FormidableEngine<DebounceLimitModel> BuildEngine(FormidableOptions options, ILogger logger)
    {
        var model = new DebounceLimitModel();
        return new FormidableEngine<DebounceLimitModel>(
            model,
            new EditContext(model),
            new FluentValidationModelValidator<DebounceLimitModel>(new DebounceLimitValidator()),
            new ReflectionModelIntrospector(),
            options,
            new FakeTimeProvider(),
            logger: logger);
    }

    /// <summary>Runs <paramref name="act"/> with a listener attached only for its duration and returns the note's lines for this class's model.</summary>
    private static List<string> CaptureTrace(Action act)
    {
        var listener = new CapturingTraceListener();
        Trace.Listeners.Add(listener);
        try
        {
            act();
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }

        return listener.NoteLines();
    }

    public sealed class DebounceLimitModel
    {
        public string Name { get; set; } = string.Empty;
    }

    public sealed class DebounceLimitValidator : AbstractValidator<DebounceLimitModel>
    {
        public DebounceLimitValidator() => RuleFor(x => x.Name).NotEmpty();
    }

    private sealed class CapturingTraceListener : TraceListener
    {
        private readonly ConcurrentQueue<string> _lines = new();

        public override void Write(string? message)
        {
        }

        public override void WriteLine(string? message) => _lines.Enqueue(message ?? string.Empty);

        public List<string> NoteLines() =>
            [.. _lines.Where(line => line.Contains(nameof(DebounceLimitModel)) && line.Contains("cannot use as a wait"))];
    }

    private sealed class CapturingLogger : ILogger
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Message)> _entries = new();

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
}
