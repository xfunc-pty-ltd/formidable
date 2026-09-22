using Microsoft.Extensions.Time.Testing;

namespace Formidable.Tests;

public class AsyncRuleMemoTests
{
    [Fact]
    public async Task A_repeat_within_the_window_does_not_run_the_factory_again()
    {
        var time = new FakeTimeProvider();
        var calls = 0;
        var memo = new AsyncRuleMemo<string, bool>(TimeSpan.FromSeconds(1), timeProvider: time);

        await memo.GetAsync("tim", (_, _) => { calls++; return Task.FromResult(true); });
        await memo.GetAsync("tim", (_, _) => { calls++; return Task.FromResult(true); });

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task A_repeat_after_the_window_runs_the_factory_again()
    {
        var time = new FakeTimeProvider();
        var calls = 0;
        var memo = new AsyncRuleMemo<string, bool>(TimeSpan.FromSeconds(1), timeProvider: time);

        await memo.GetAsync("tim", (_, _) => { calls++; return Task.FromResult(true); });
        time.Advance(TimeSpan.FromSeconds(2));
        await memo.GetAsync("tim", (_, _) => { calls++; return Task.FromResult(true); });

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task A_hit_does_not_extend_the_window()
    {
        var time = new FakeTimeProvider();
        var calls = 0;
        var memo = new AsyncRuleMemo<string, bool>(TimeSpan.FromSeconds(1), timeProvider: time);

        await memo.GetAsync("tim", (_, _) => { calls++; return Task.FromResult(true); });

        time.Advance(TimeSpan.FromMilliseconds(600));
        await memo.GetAsync("tim", (_, _) => { calls++; return Task.FromResult(true); });
        Assert.Equal(1, calls);

        // The window runs from the check that produced the answer, not from the last time the
        // answer was handed out, so a steadily re-checked value still gets a fresh answer.
        time.Advance(TimeSpan.FromMilliseconds(600));
        await memo.GetAsync("tim", (_, _) => { calls++; return Task.FromResult(true); });
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Every_value_in_a_collection_hits_on_the_second_round()
    {
        // The test that a single-entry memo fails: a RuleForEach visits every item in turn, so an
        // entry holding only the last value would miss on every item of the second pass.
        var time = new FakeTimeProvider();
        var calls = 0;
        var memo = new AsyncRuleMemo<string, bool>(TimeSpan.FromSeconds(1), timeProvider: time);
        var values = new[] { "a", "b", "c", "d", "e" };

        foreach (var value in values)
        {
            await memo.GetAsync(value, (_, _) => { calls++; return Task.FromResult(true); });
        }

        foreach (var value in values)
        {
            await memo.GetAsync(value, (_, _) => { calls++; return Task.FromResult(true); });
        }

        Assert.Equal(values.Length, calls);
    }

    [Fact]
    public async Task An_overlapping_call_joins_the_one_already_running()
    {
        var time = new FakeTimeProvider();
        var calls = 0;
        var gate = new TaskCompletionSource<bool>();
        var memo = new AsyncRuleMemo<string, bool>(TimeSpan.FromSeconds(1), timeProvider: time);

        var first = memo.GetAsync("tim", (_, _) => { calls++; return gate.Task; });
        var second = memo.GetAsync("tim", (_, _) => { calls++; return gate.Task; });

        gate.SetResult(true);
        Assert.True(await first);
        Assert.True(await second);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task One_callers_cancellation_does_not_cancel_the_shared_work()
    {
        var time = new FakeTimeProvider();
        var gate = new TaskCompletionSource<bool>();
        var memo = new AsyncRuleMemo<string, bool>(TimeSpan.FromSeconds(1), timeProvider: time);
        using var cts = new CancellationTokenSource();

        // The factory honours the token it is handed, so an implementation that passed a caller's
        // token through to it would leave the shared work cancelled for everyone else too.
        var abandoned = memo.GetAsync("tim", (_, ct) => gate.Task.WaitAsync(ct), cts.Token);
        var kept = memo.GetAsync("tim", (_, ct) => gate.Task.WaitAsync(ct));

        await cts.CancelAsync();
        await Assert.ThrowsAsync<TaskCanceledException>(() => abandoned);

        gate.SetResult(true);
        Assert.True(await kept);
    }

    [Fact]
    public async Task A_faulted_result_is_not_served_to_the_next_caller()
    {
        var time = new FakeTimeProvider();
        var calls = 0;
        var memo = new AsyncRuleMemo<string, bool>(TimeSpan.FromSeconds(1), timeProvider: time);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            memo.GetAsync("tim", (_, _) => { calls++; return Task.FromException<bool>(new InvalidOperationException()); }));

        var second = await memo.GetAsync("tim", (_, _) => { calls++; return Task.FromResult(true); });

        Assert.True(second);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task A_cancelled_result_is_not_served_to_the_next_caller()
    {
        var time = new FakeTimeProvider();
        var calls = 0;
        var memo = new AsyncRuleMemo<string, bool>(TimeSpan.FromSeconds(1), timeProvider: time);

        // The other half of "a check that did not answer is never held": a check abandoned before
        // it produced a verdict is no more usable than one that blew up.
        await Assert.ThrowsAsync<TaskCanceledException>(() =>
            memo.GetAsync("tim", (_, _) => { calls++; return Task.FromCanceled<bool>(new CancellationToken(true)); }));

        var second = await memo.GetAsync("tim", (_, _) => { calls++; return Task.FromResult(true); });

        Assert.True(second);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task The_oldest_entry_goes_once_the_cap_is_reached()
    {
        var time = new FakeTimeProvider();
        var calls = 0;
        var memo = new AsyncRuleMemo<int, bool>(TimeSpan.FromSeconds(30), capacity: 2, timeProvider: time);

        await memo.GetAsync(1, (_, _) => { calls++; return Task.FromResult(true); });
        time.Advance(TimeSpan.FromMilliseconds(1));
        await memo.GetAsync(2, (_, _) => { calls++; return Task.FromResult(true); });
        time.Advance(TimeSpan.FromMilliseconds(1));
        await memo.GetAsync(3, (_, _) => { calls++; return Task.FromResult(true); });

        // 1 was the oldest when 3 arrived, so it is the one that went.
        await memo.GetAsync(1, (_, _) => { calls++; return Task.FromResult(true); });
        Assert.Equal(4, calls);

        await memo.GetAsync(3, (_, _) => { calls++; return Task.FromResult(true); });
        Assert.Equal(4, calls);
    }

    [Fact]
    public async Task A_comparer_decides_what_counts_as_the_same_value()
    {
        var time = new FakeTimeProvider();
        var calls = 0;
        var memo = new AsyncRuleMemo<string, bool>(
            TimeSpan.FromSeconds(1),
            comparer: StringComparer.OrdinalIgnoreCase,
            timeProvider: time);

        await memo.GetAsync("Tim", (_, _) => { calls++; return Task.FromResult(true); });
        await memo.GetAsync("tim", (_, _) => { calls++; return Task.FromResult(true); });

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task A_projection_reaches_the_same_answer_through_a_created_comparer()
    {
        // What a key selector would be for: default equality of a reference type is wrong here,
        // so equality is defined by the part of the value the check actually reads.
        var time = new FakeTimeProvider();
        var calls = 0;
        var memo = new AsyncRuleMemo<Address, bool>(
            TimeSpan.FromSeconds(1),
            comparer: EqualityComparer<Address>.Create(
                (a, b) => a?.PostCode == b?.PostCode,
                a => a.PostCode.GetHashCode(StringComparison.Ordinal)),
            timeProvider: time);

        await memo.GetAsync(new Address("5000", "Adelaide"), (_, _) => { calls++; return Task.FromResult(true); });
        await memo.GetAsync(new Address("5000", "Kent Town"), (_, _) => { calls++; return Task.FromResult(true); });

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Invalidate_forces_the_next_lookup_to_run_the_check_again()
    {
        var time = new FakeTimeProvider();
        var calls = 0;
        var memo = new AsyncRuleMemo<string, int>(TimeSpan.FromHours(1), timeProvider: time);

        var first = await memo.GetAsync("tim", (_, _) => { calls++; return Task.FromResult(calls); });
        memo.Invalidate("tim");
        var second = await memo.GetAsync("tim", (_, _) => { calls++; return Task.FromResult(calls); });

        Assert.Equal(2, calls);
        Assert.Equal(1, first);
        Assert.Equal(2, second);
    }

    [Fact]
    public async Task Invalidate_leaves_other_keys_untouched()
    {
        var time = new FakeTimeProvider();
        var calls = 0;
        var memo = new AsyncRuleMemo<string, bool>(TimeSpan.FromHours(1), timeProvider: time);

        await memo.GetAsync("tim", (_, _) => { calls++; return Task.FromResult(true); });
        await memo.GetAsync("kate", (_, _) => { calls++; return Task.FromResult(true); });
        Assert.Equal(2, calls);

        memo.Invalidate("tim");

        await memo.GetAsync("tim", (_, _) => { calls++; return Task.FromResult(true); });
        await memo.GetAsync("kate", (_, _) => { calls++; return Task.FromResult(true); });

        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Clear_forces_every_key_to_run_again()
    {
        var time = new FakeTimeProvider();
        var calls = 0;
        var memo = new AsyncRuleMemo<string, bool>(TimeSpan.FromHours(1), timeProvider: time);

        await memo.GetAsync("tim", (_, _) => { calls++; return Task.FromResult(true); });
        await memo.GetAsync("kate", (_, _) => { calls++; return Task.FromResult(true); });
        Assert.Equal(2, calls);

        memo.Clear();

        await memo.GetAsync("tim", (_, _) => { calls++; return Task.FromResult(true); });
        await memo.GetAsync("kate", (_, _) => { calls++; return Task.FromResult(true); });

        Assert.Equal(4, calls);
    }

    [Fact]
    public void A_window_that_cannot_hold_an_answer_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AsyncRuleMemo<string, bool>(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AsyncRuleMemo<string, bool>(TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void A_capacity_that_cannot_hold_an_entry_is_rejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AsyncRuleMemo<string, bool>(TimeSpan.FromSeconds(1), capacity: 0));

    [Fact]
    public void A_missing_factory_is_rejected()
    {
        var memo = new AsyncRuleMemo<string, bool>(TimeSpan.FromSeconds(1));

        // Eagerly, rather than as a faulted task: a caller that never awaits still hears about it.
        Assert.Throws<ArgumentNullException>(() => { _ = memo.GetAsync("tim", null!); });
    }

    private sealed class Address(string postCode, string suburb)
    {
        public string PostCode { get; } = postCode;

        public string Suburb { get; } = suburb;
    }
}
