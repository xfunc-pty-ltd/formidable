namespace Formidable;

/// <summary>
/// Reuses the answer an async check already gave for a value, for a short window, so a check that
/// runs more than once over unchanged input costs a lookup instead of a round trip. Built for
/// async validation rules, where a value is genuinely asked about twice: retyped after being
/// cleared, or checked again by a submit that follows shortly after a live pass already answered
/// the same field.
/// </summary>
/// <remarks>
/// <para>
/// Hold one of these as a field on the validator that uses it. It has to outlive a single
/// validation pass to be worth anything, and a validator instance does: the engine resolves its
/// <see cref="IModelValidator{TModel}"/> once and keeps it, and FluentValidation registers
/// validators per scope. Constructed inside a rule's own lambda it is rebuilt on every call and
/// never once hits.
/// </para>
/// <para>
/// Only for a check that is pure with respect to its input — the same value giving the same answer
/// for as long as the window lasts. A predicate with side effects, or one whose answer can change
/// within the window for reasons other than the value, does not belong here. Nothing about a
/// rule's outcome changes: this only avoids asking the same question twice.
/// </para>
/// <para>
/// The work is shared, so it is not cancelled by any one caller. A caller that cancels stops
/// waiting; the call it was waiting on carries on for whoever else wants it.
/// </para>
/// <para>
/// Safe to use from several flows at once, which is what makes joining an in-flight call possible:
/// two passes that end up validating around the same time share one in-flight check instead of
/// each starting its own, and a consumer may validate away from the UI thread.
/// </para>
/// <para>
/// A failed check is never served. The next caller runs it again, so a transient network failure
/// does not stick for the rest of the window. A check still running is joined only while its window
/// lasts: one that has outlived the window is left to the callers already waiting on it, and a
/// fresh check starts.
/// </para>
/// </remarks>
/// <typeparam name="TKey">The value the answer is keyed by.</typeparam>
/// <typeparam name="TResult">The answer.</typeparam>
public sealed class AsyncRuleMemo<TKey, TResult>
    where TKey : notnull
{
    private readonly TimeSpan _window;
    private readonly int _capacity;
    private readonly TimeProvider _time;
    private readonly Dictionary<TKey, Entry> _entries;
    private readonly Lock _gate = new();

    /// <summary>Creates a memo.</summary>
    /// <param name="window">
    /// How long an answer stays usable, measured from the moment its check starts. Size it to the
    /// pause it has to survive rather than to any of the engine's own scheduling windows: a value
    /// retyped, or a submit pressed shortly after a live pass already answered the same field, are
    /// both paced by the person at the keyboard, not by a timer, so a useful window is seconds
    /// long and a judgement call rather than a derived value.
    /// </param>
    /// <param name="capacity">
    /// The most entries kept at once. A leak guard for a long-lived validator, not a tuning knob —
    /// the default is far above any realistic collection. Entries are only released on a miss, so
    /// an idle memo keeps up to this many answers until it is next asked something it cannot answer.
    /// </param>
    /// <param name="comparer">
    /// Decides what counts as the same value. Defaults to <see cref="EqualityComparer{T}.Default"/>.
    /// To key on part of a value instead of the whole of it — the usual reason a reference type
    /// needs one — build a comparer over that part with <see cref="EqualityComparer{T}.Create"/>.
    /// </param>
    /// <param name="timeProvider">Defaults to <see cref="TimeProvider.System"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="window"/> is not positive, or <paramref name="capacity"/> is below one.
    /// </exception>
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

    /// <summary>
    /// Returns the answer for <paramref name="key"/>, running <paramref name="factory"/> only if
    /// no usable answer is held. A call that arrives while an earlier one is still running joins
    /// it rather than starting a second.
    /// </summary>
    /// <param name="key">The value being checked.</param>
    /// <param name="factory">
    /// Runs the check. Handed <see cref="CancellationToken.None"/>, because its result is shared.
    /// Must return promptly: it is called while the memo is locked, so that two callers arriving
    /// together cannot both start a check, and one that blocks before handing back its task holds
    /// up lookups of every other key too. The check itself runs on after the lock is released.
    /// </param>
    /// <param name="cancellationToken">Cancels this caller's wait, not the shared work.</param>
    /// <returns>The answer, once the check producing it has finished.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/> is <see langword="null"/>.</exception>
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
                shared = factory(key, CancellationToken.None);
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

    /// <summary>
    /// Drops the held answer for <paramref name="key"/>, so the next <see cref="GetAsync"/> for it
    /// runs the check again regardless of how much of the window remains.
    /// </summary>
    /// <remarks>
    /// Governs future lookups only: a call already sharing this key's in-flight task is not
    /// cancelled and still receives that task's answer. For news that outdates one held answer
    /// specifically — a server reporting that a value this memo still holds as available has just
    /// been taken — this is the way to stop that answer being served without waiting out the
    /// window.
    /// </remarks>
    /// <param name="key">The value whose held answer should no longer be served.</param>
    public void Invalidate(TKey key)
    {
        lock (_gate)
        {
            _entries.Remove(key);
        }
    }

    /// <summary>
    /// Drops every held answer, so the next <see cref="GetAsync"/> for any key runs the check
    /// again regardless of how much of the window remains.
    /// </summary>
    /// <remarks>
    /// Governs future lookups only: a call already sharing an in-flight task for any key is not
    /// cancelled and still receives that task's answer.
    /// </remarks>
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
