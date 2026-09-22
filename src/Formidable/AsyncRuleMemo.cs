namespace Formidable;

/// <summary>Reuses an async check's answer for the same value within a window, so a value asked about twice costs one call.</summary>
/// <typeparam name="TKey">The value an answer is keyed by.</typeparam>
/// <typeparam name="TResult">The answer.</typeparam>
/// <remarks>
/// Hold one as a field on the validator; one built inside a rule's lambda is rebuilt on every
/// call and never hits. Use it only for a check that is pure over its input, because a held
/// answer stands in for the check for the rest of the window. Concurrent callers for one key
/// share the running check, and a caller that cancels stops waiting while the check continues.
/// </remarks>
public sealed class AsyncRuleMemo<TKey, TResult>
    where TKey : notnull
{
    private readonly TimeSpan _window;
    private readonly int _capacity;
    private readonly TimeProvider _time;
    private readonly Dictionary<TKey, Entry> _entries;
    private readonly Lock _gate = new();

    /// <summary>Creates a memo whose answers stay usable for <paramref name="window"/>, with <paramref name="comparer"/> deciding what counts as the same value.</summary>
    /// <param name="window">How long an answer stays usable, counted from the moment its check starts; size it to the pause a person makes between asks, seconds rather than milliseconds.</param>
    /// <param name="capacity">The most entries held at once, a leak guard rather than a tuning knob; an entry leaves only when a miss makes room for a new one, or through <see cref="Invalidate"/> or <see cref="Clear"/>. Defaults to 256.</param>
    /// <param name="comparer">What counts as the same value; to key on part of a value, build one over that part with <see cref="EqualityComparer{T}.Create"/>. Defaults to <see cref="EqualityComparer{T}.Default"/>.</param>
    /// <param name="timeProvider">The clock the window is measured by. Defaults to <see cref="TimeProvider.System"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="window"/> is zero or negative, or <paramref name="capacity"/> is below one.</exception>
    public AsyncRuleMemo(
        TimeSpan window,
        int capacity = 256,
        IEqualityComparer<TKey>? comparer = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        _window = window;
        _capacity = capacity;
        _time = timeProvider ?? TimeProvider.System;
        _entries = new Dictionary<TKey, Entry>(comparer);
    }

    /// <summary>Returns the answer for <paramref name="key"/>, running <paramref name="factory"/> only when no usable answer is held; a concurrent call for the key joins the running check.</summary>
    /// <param name="key">The value being checked.</param>
    /// <param name="factory">Runs the check; handed <see cref="CancellationToken.None"/> because its answer is shared, and called under the memo's lock, so one that blocks before returning its task holds up every other key's lookup too.</param>
    /// <param name="cancellationToken">Cancels this caller's wait, not the shared check.</param>
    /// <returns>The answer, once the check producing it completes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="factory"/> returned <see langword="null"/> instead of a task.</exception>
    /// <remarks>
    /// A held answer is usable while its window lasts and its check neither faulted nor was
    /// cancelled, so a failed check is run again by the next caller; a check still running counts
    /// and is joined only while its window lasts.
    /// </remarks>
    public Task<TResult> GetAsync(
        TKey key,
        Func<TKey, CancellationToken, Task<TResult>> factory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(factory);

        Task<TResult> shared;
        lock (_gate)
        {
            // Monotonic, so an answer's age is what it says it is even across a system clock
            // adjustment — a window this short would otherwise be trivial to knock over.
            var now = _time.GetTimestamp();

            if (_entries.TryGetValue(key, out var entry) && IsUsable(entry, now))
            {
                // The stamp stays where it is. The window measures how old an answer is, not how
                // recently it was asked for, so a value re-checked on every keystroke still gets a
                // fresh answer once the window is out.
                shared = entry.Task;
            }
            else
            {
                MakeRoom(now);

                // Started under the lock, so two callers arriving together cannot both start one.
                // Holding the gate across the check's synchronous prefix is the price of that
                // guarantee — releasing first would leave a window for a duplicate call. The lock
                // is never held across an await: this returns as soon as the check has begun, not
                // when it finishes.
                shared = factory(key, CancellationToken.None)
                    ?? throw new InvalidOperationException(
                        "The factory returned no task. A memo entry is joined, awaited and asked whether it " +
                        "faulted, so there is nothing to store — return a task, faulted if the check itself " +
                        "cannot run.");

                // Checked before the store, or the null outlives this call: every later miss
                // sweeps the entries it holds and asks each one whether it faulted, so one
                // null entry fails lookups of every other key too.
                _entries[key] = new Entry(shared, now);

                // Every caller reaches a failure through its own wait below, and every one of those
                // waits can be cancelled. Without this, a failure nobody is left waiting for
                // surfaces as an unobserved task exception.
                _ = shared.ContinueWith(
                    static task => _ = task.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }

        return shared.WaitAsync(cancellationToken);
    }

    /// <summary>Drops the held answer for <paramref name="key"/>, so the next <see cref="GetAsync"/> for it runs the check again, however much of the window remains.</summary>
    /// <param name="key">The value whose held answer is no longer to be served.</param>
    /// <remarks>
    /// A caller already awaiting this key's running check still receives its answer. Call it when
    /// news outdates one held answer (a value the memo holds as available has just been taken), so
    /// that answer stops being served without waiting out the window.
    /// </remarks>
    public void Invalidate(TKey key)
    {
        lock (_gate)
        {
            _entries.Remove(key);
        }
    }

    /// <summary>Drops every held answer, so the next <see cref="GetAsync"/> for any key runs the check again, however much of the window remains.</summary>
    /// <remarks>A caller already awaiting a running check for any key still receives its answer.</remarks>
    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
        }
    }

    // A check still running is usable: joining it is the whole point, and it is how the two passes
    // of one edit share a single call. A failed one never is — a transient fault must not stick for
    // the rest of the window, and a cached exception is indistinguishable from a real verdict.
    private bool IsUsable(Entry entry, long now) =>
        !entry.Task.IsFaulted
        && !entry.Task.IsCanceled
        && _time.GetElapsedTime(entry.Stamp, now) < _window;

    private void MakeRoom(long now)
    {
        // Dictionary allows removal while it is being enumerated, so the sweep needs no
        // intermediate list.
        foreach (var pair in _entries)
        {
            if (!IsUsable(pair.Value, now))
            {
                _entries.Remove(pair.Key);
            }
        }

        // Room for the entry about to be added. The sweep above usually leaves nothing to do here;
        // this is for a validator that has seen more values inside one window than the cap holds.
        while (_entries.Count >= _capacity)
        {
            if (!RemoveOldest())
            {
                // Unreachable as the two stand today: the loop only runs with entries held (the
                // cap is at least one), and a search over a non-empty dictionary always picks a
                // key it can then remove. Structural rather than defensive — it is what makes the
                // loop's termination readable from the loop itself, without the reader having to
                // go and prove the search never comes back empty-handed.
                break;
            }
        }
    }

    // Reports whether it removed one, so a search that comes back empty-handed ends the caller's
    // loop instead of spinning it against a count that will not drop.
    private bool RemoveOldest()
    {
        TKey? oldest = default;
        var oldestStamp = 0L;
        var found = false;

        foreach (var pair in _entries)
        {
            if (!found || pair.Value.Stamp < oldestStamp)
            {
                oldest = pair.Key;
                oldestStamp = pair.Value.Stamp;
                found = true;
            }
        }

        return found && _entries.Remove(oldest!);
    }

    private readonly record struct Entry(Task<TResult> Task, long Stamp);
}
