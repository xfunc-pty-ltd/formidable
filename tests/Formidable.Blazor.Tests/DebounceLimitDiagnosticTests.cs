using System.Globalization;
using FluentValidation;
using Formidable.Blazor.Tests.Fixtures;
using Formidable.Introspection;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Formidable.Blazor.Tests;

/// <summary>
/// The note an engine writes when <see cref="FormidableOptions.RefreshDebounce"/> or
/// <see cref="FormidableOptions.LiveDebounce"/> is negative other than
/// <see cref="Timeout.InfiniteTimeSpan"/>, or past the longest wait the system timer takes, which
/// is <c>uint.MaxValue - 1</c> milliseconds once any fraction is truncated. The form reads a wait
/// past the limit as one that never ends and a negative one as zero, and names the option once,
/// whether the value was there as the engine was built or set later and met as a wait starts. The
/// Trace listener is process-wide, so every Trace capture is filtered to lines naming
/// <see cref="DebounceLimitModel"/>, a model no other test class uses; a test that arms a timer
/// counts the entries of its own logger instead.
/// </summary>
public class DebounceLimitDiagnosticTests
{
    // The longest due time the system timer accepts, and the shortest it rejects.
    private static readonly TimeSpan AtTheLimit = TimeSpan.FromMilliseconds(uint.MaxValue - 1);
    private static readonly TimeSpan PastTheLimit = TimeSpan.FromMilliseconds(uint.MaxValue);

    // A negative wait the fake clock's timer rejects, as the system timer does.
    private static readonly TimeSpan Negative = TimeSpan.FromMilliseconds(-5);

    // Each wait starts twice with a value set after the build: the refresh by a field-set change,
    // the live window by a committed change, and the validity check (tracking on, a window that
    // never closes, RefreshDebounce the unusable value) by a committed change before any submit.
    // Mutations: (a) drop the arm-time check, so the value reaches the timer as it stands, and the
    // first wait throws ArgumentOutOfRangeException with nothing named; (b) drop the latch from the
    // arm-time check, so every arm names the value, and the second wait writes a second warning.
    [Theory]
    [InlineData("Refresh", false)]
    [InlineData("Refresh", true)]
    [InlineData("Live", false)]
    [InlineData("Live", true)]
    [InlineData("Validity", false)]
    [InlineData("Validity", true)]
    public void A_debounce_made_unusable_after_the_build_is_named_once_and_never_throws(string wait, bool negative)
    {
        var logger = new CapturingLogger();
        var options = wait == "Validity"
            ? new FormidableOptions { TrackFormValidity = true, LiveDebounce = Timeout.InfiniteTimeSpan }
            : new FormidableOptions();
        var model = new DebounceLimitModel();
        var editContext = new EditContext(model);
        using var engine = BuildEngine(model, editContext, options, logger, new FakeTimeProvider());
        var value = negative ? Negative : TimeSpan.MaxValue;
        var option = wait == "Live" ? nameof(FormidableOptions.LiveDebounce) : nameof(FormidableOptions.RefreshDebounce);
        if (wait == "Live")
        {
            options.LiveDebounce = value;
        }
        else
        {
            options.RefreshDebounce = value;
        }

        var thrown = Record.Exception(() =>
        {
            StartTheWait();
            StartTheWait();
        });

        Assert.Null(thrown);
        var warning = Assert.Single(logger.Entries, entry => entry.Message.Contains("cannot use as a wait"));
        Assert.Equal(LogLevel.Warning, warning.Level);
        AssertNames(warning.Message, $"FormidableOptions.{option}", value.ToString("c", CultureInfo.InvariantCulture));
        Assert.Contains(negative ? "treats it as zero" : "treats it as Timeout.InfiniteTimeSpan", warning.Message);

        void StartTheWait()
        {
            if (wait == "Refresh")
            {
                engine.OnRenderedFieldsChanged();
                return;
            }

            model.Name += "x";
            editContext.NotifyFieldChanged(new FieldIdentifier(model, nameof(DebounceLimitModel.Name)));
        }
    }

    // Mutation: read a negative value as a wait that never ends, and the refresh never runs.
    [Fact]
    public void A_negative_debounce_set_after_the_build_arms_at_zero()
    {
        var model = new DebounceLimitModel();
        var editContext = new EditContext(model);
        var options = new FormidableOptions();
        var time = new FakeTimeProvider();
        var counting = new CountingValidator<DebounceLimitModel>(
            new FluentValidationModelValidator<DebounceLimitModel>(new DebounceLimitValidator()));
        using var engine = new FormidableEngine<DebounceLimitModel>(
            model, editContext, counting, new ReflectionModelIntrospector(), options, time,
            logger: new CapturingLogger());
        options.RefreshDebounce = Negative;
        var before = counting.CallCount;

        engine.OnRenderedFieldsChanged();
        time.Advance(TimeSpan.Zero);

        Assert.Equal(before + 1, counting.CallCount);
    }

    // The value is set before the build, so the build names it, and a wait then starts twice.
    // Mutation: give the arm-time check a latch of its own rather than the build's, and the first
    // wait names the option a second time.
    [Theory]
    [InlineData(nameof(FormidableOptions.RefreshDebounce), false)]
    [InlineData(nameof(FormidableOptions.RefreshDebounce), true)]
    [InlineData(nameof(FormidableOptions.LiveDebounce), false)]
    [InlineData(nameof(FormidableOptions.LiveDebounce), true)]
    public void A_debounce_named_at_the_build_is_not_named_again_at_the_arm(string option, bool negative)
    {
        var logger = new CapturingLogger();
        var options = With(option, negative ? Negative : TimeSpan.MaxValue);
        var model = new DebounceLimitModel();
        var editContext = new EditContext(model);
        using var engine = BuildEngine(model, editContext, options, logger, new FakeTimeProvider());
        Assert.Single(logger.Entries, entry => entry.Message.Contains("cannot use as a wait"));

        var thrown = Record.Exception(() =>
        {
            for (var arm = 0; arm < 2; arm++)
            {
                if (option == nameof(FormidableOptions.RefreshDebounce))
                {
                    engine.OnRenderedFieldsChanged();
                }
                else
                {
                    model.Name += "x";
                    editContext.NotifyFieldChanged(new FieldIdentifier(model, nameof(DebounceLimitModel.Name)));
                }
            }
        });

        Assert.Null(thrown);
        Assert.Single(logger.Entries, entry => entry.Message.Contains("cannot use as a wait"));
    }

    // Mutation: drop the check, and nothing is written.
    [Fact]
    public void A_RefreshDebounce_beyond_the_timer_limit_is_named_once_at_build()
    {
        var logger = new CapturingLogger();

        var lines = TraceCapture.Run(() =>
        {
            using var engine = BuildEngine(new FormidableOptions { RefreshDebounce = PastTheLimit }, logger);
        }, IsNote);

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

        var lines = TraceCapture.Run(() =>
        {
            using var engine = BuildEngine(new FormidableOptions { LiveDebounce = TimeSpan.FromDays(50) }, logger);
        }, IsNote);

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

        var lines = TraceCapture.Run(() =>
        {
            using var engine = BuildEngine(options, logger);
        }, IsNote);

        AssertNames(Assert.Single(lines), $"FormidableOptions.{option}", rendered);
        AssertNames(
            Assert.Single(logger.Entries, entry => entry.Message.Contains("cannot use as a wait")).Message,
            $"FormidableOptions.{option}",
            rendered);
    }

    // A pin: neither value is named. The limit itself arms as given, and Timeout.InfiniteTimeSpan
    // is the wait that never passes. Mutation: compare with >= against the limit, and the limit
    // is named; or name every negative, and the infinite wait is.
    [Theory]
    [InlineData(nameof(FormidableOptions.RefreshDebounce), false)]
    [InlineData(nameof(FormidableOptions.RefreshDebounce), true)]
    [InlineData(nameof(FormidableOptions.LiveDebounce), false)]
    [InlineData(nameof(FormidableOptions.LiveDebounce), true)]
    public void A_debounce_at_the_limit_or_infinite_is_not_named(string option, bool infinite)
    {
        var logger = new CapturingLogger();
        var options = With(option, infinite ? Timeout.InfiniteTimeSpan : AtTheLimit);

        var lines = TraceCapture.Run(() =>
        {
            using var engine = BuildEngine(options, logger);
        }, IsNote);

        Assert.Empty(lines);
        Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains("cannot use as a wait"));
    }

    // A pin: the timer truncates a due time to whole milliseconds before it checks its upper
    // limit, so a wait just under a millisecond past the limit arms as the limit and is not named.
    // Mutation: compare the TimeSpan with the limit rather than its whole milliseconds, and it is
    // named.
    [Theory]
    [InlineData(nameof(FormidableOptions.RefreshDebounce), 42949672949999L)]
    [InlineData(nameof(FormidableOptions.LiveDebounce), 42949672940001L)]
    public void A_debounce_the_timer_truncates_to_its_limit_is_not_named(string option, long ticks)
    {
        var logger = new CapturingLogger();
        var options = With(option, TimeSpan.FromTicks(ticks));

        var lines = TraceCapture.Run(() =>
        {
            using var engine = BuildEngine(options, logger);
        }, IsNote);

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
        return BuildEngine(model, new EditContext(model), options, logger, new FakeTimeProvider());
    }

    private static FormidableEngine<DebounceLimitModel> BuildEngine(
        DebounceLimitModel model,
        EditContext editContext,
        FormidableOptions options,
        ILogger logger,
        TimeProvider time) =>
        new(
            model,
            editContext,
            new FluentValidationModelValidator<DebounceLimitModel>(new DebounceLimitValidator()),
            new ReflectionModelIntrospector(),
            options,
            time,
            logger: logger);

    /// <summary>Whether a Trace line is the note, written for this class's model.</summary>
    private static bool IsNote(string line) =>
        line.Contains(nameof(DebounceLimitModel)) && line.Contains("cannot use as a wait");

    public sealed class DebounceLimitModel
    {
        public string Name { get; set; } = string.Empty;
    }

    public sealed class DebounceLimitValidator : AbstractValidator<DebounceLimitModel>
    {
        public DebounceLimitValidator() => RuleFor(x => x.Name).NotEmpty();
    }
}
