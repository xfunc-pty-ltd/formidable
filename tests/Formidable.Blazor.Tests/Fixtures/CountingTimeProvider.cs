using Microsoft.Extensions.Time.Testing;

namespace Formidable.Blazor.Tests.Fixtures;

/// <summary>
/// A <see cref="TimeProvider"/> over a <see cref="FakeTimeProvider"/> that counts each timer's
/// callbacks and <c>Change</c> calls, and stops a timer that keeps re-arming itself at zero. The
/// fake clock fires a zero due time inside <c>Change</c>, and a callback that re-arms its own timer
/// at zero is fired again inside that same <c>Change</c>, for as long as it keeps doing so. A fire
/// that polled at a zero wait would therefore never return to the test.
/// </summary>
/// <remarks>
/// The cap is per arm. A timer's callback stops being delegated after <see cref="Cap"/> fires
/// since the last <c>Change</c> made outside one of its own callbacks, and the timer records that
/// it was capped. A polling loop is stopped and reported, while a later arm from anywhere else (a
/// pass's end, an edit) resets the count and fires as normal.
/// </remarks>
/// <param name="clock">The fake clock every timer runs on and every reading comes from.</param>
public sealed class CountingTimeProvider(FakeTimeProvider clock) : TimeProvider
{
    /// <summary>How many fires one arm may deliver before the callback stops being delegated.</summary>
    public const int Cap = 50;

    private readonly List<CountingTimer> _timers = [];

    /// <summary>The timers created so far, in creation order.</summary>
    public IReadOnlyList<CountingTimer> Timers => _timers;

    /// <summary>Every fire the clock has delivered to any timer, capped ones included.</summary>
    public int Callbacks => _timers.Sum(timer => timer.Callbacks);

    /// <summary>Every <c>Change</c> call on any timer: each arm, and each disarm by a wait that never passes.</summary>
    /// <remarks>
    /// A pass that ends on a worker thread arms the fires that waited for it in a dispatch of its
    /// own after the end, so a test that advances the clock to let such a fire run first waits for
    /// this count to move.
    /// </remarks>
    public int Changes => _timers.Sum(timer => timer.Changes);

    /// <summary>Whether any timer reached the cap.</summary>
    public bool AnyCapped => _timers.Any(timer => timer.Capped);

    /// <summary>Advances the fake clock, firing every timer that comes due.</summary>
    /// <param name="by">How far to move the clock.</param>
    public void Advance(TimeSpan by) => clock.Advance(by);

    /// <inheritdoc />
    public override long GetTimestamp() => clock.GetTimestamp();

    /// <inheritdoc />
    public override long TimestampFrequency => clock.TimestampFrequency;

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => clock.GetUtcNow();

    /// <inheritdoc />
    public override TimeZoneInfo LocalTimeZone => clock.LocalTimeZone;

    /// <inheritdoc />
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new CountingTimer(callback);
        timer.Inner = clock.CreateTimer(timer.Fire, state, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _timers.Add(timer);
        if (dueTime != Timeout.InfiniteTimeSpan)
        {
            timer.Change(dueTime, period);
        }

        return timer;
    }

    /// <summary>One timer's counts, around the fake clock's own timer.</summary>
    public sealed class CountingTimer : ITimer
    {
        // The timer whose callback is running on this thread, so a Change can tell whether its own
        // callback made it. The fake clock runs callbacks on the thread that advanced it or armed
        // the timer, so a thread-static answers that exactly.
        [ThreadStatic]
        private static CountingTimer? t_firing;

        private readonly TimerCallback _callback;
        private int _firesSinceArm;
        private int _callbacks;
        private int _changes;

        internal CountingTimer(TimerCallback callback) => _callback = callback;

        internal ITimer Inner { get; set; } = null!;

        /// <summary>Every fire the clock delivered, capped ones included.</summary>
        public int Callbacks => Volatile.Read(ref _callbacks);

        /// <summary>Every <c>Change</c> call, from any thread.</summary>
        public int Changes => Volatile.Read(ref _changes);

        /// <summary>Whether an arm delivered more than <see cref="Cap"/> fires.</summary>
        public bool Capped { get; private set; }

        internal void Fire(object? state)
        {
            Interlocked.Increment(ref _callbacks);
            if (++_firesSinceArm > Cap)
            {
                Capped = true;
                return;
            }

            var outer = t_firing;
            t_firing = this;
            try
            {
                _callback(state);
            }
            finally
            {
                t_firing = outer;
            }
        }

        /// <inheritdoc />
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            Interlocked.Increment(ref _changes);
            if (!ReferenceEquals(t_firing, this))
            {
                _firesSinceArm = 0;
            }

            return Inner.Change(dueTime, period);
        }

        /// <inheritdoc />
        public void Dispose() => Inner.Dispose();

        /// <inheritdoc />
        public ValueTask DisposeAsync() => Inner.DisposeAsync();
    }
}
