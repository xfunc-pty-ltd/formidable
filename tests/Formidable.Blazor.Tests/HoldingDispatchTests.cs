using Formidable.Blazor.Tests.Fixtures;

namespace Formidable.Blazor.Tests;

/// <summary>
/// The test dispatch that holds work until <see cref="HoldingDispatch.Drain"/>. Each waiting
/// caller's task ends as its delegate's task did, as an unheld dispatch hands that task straight
/// back, and a delegate that throws before it returns a task faults its own caller's task while
/// the rest of the queue still runs.
/// </summary>
public class HoldingDispatchTests
{
    // Mutation: complete each waiting caller's task once its delegate's task ends, whatever the
    // outcome, and the three rows below this control fail.
    [Fact]
    public async Task A_held_delegate_that_completes_completes_its_callers_task()
    {
        var dispatch = new HoldingDispatch { Holding = true };
        var ran = false;
        var waiting = dispatch.Dispatch(async () =>
        {
            await Task.Yield();
            ran = true;
        });

        dispatch.Drain();
        await waiting;

        Assert.True(ran);
        Assert.Equal(0, dispatch.Waiting);
    }

    [Fact]
    public async Task A_held_delegate_that_faults_faults_its_callers_task()
    {
        var dispatch = new HoldingDispatch { Holding = true };
        var thrown = new InvalidOperationException("held");
        var waiting = dispatch.Dispatch(async () =>
        {
            await Task.Yield();
            throw thrown;
        });

        dispatch.Drain();

        Assert.Same(thrown, await Assert.ThrowsAsync<InvalidOperationException>(() => waiting));
    }

    [Fact]
    public async Task A_held_delegate_that_is_cancelled_cancels_its_callers_task()
    {
        var dispatch = new HoldingDispatch { Holding = true };
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var waiting = dispatch.Dispatch(() => Task.FromCanceled(cts.Token));

        dispatch.Drain();

        var cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.True(waiting.IsCanceled);
        Assert.Equal(cts.Token, cancelled.CancellationToken);
    }

    // Mutation: let a synchronous throw leave Drain, and the throw reaches the test while the
    // delegate queued after it never runs.
    [Fact]
    public async Task A_held_delegate_that_throws_before_returning_a_task_faults_its_callers_task_and_the_queue_runs_on()
    {
        var dispatch = new HoldingDispatch { Holding = true };
        var thrown = new InvalidOperationException("sync");
        var ranAfter = false;
        var first = dispatch.Dispatch(() => throw thrown);
        var second = dispatch.Dispatch(() =>
        {
            ranAfter = true;
            return Task.CompletedTask;
        });

        dispatch.Drain();

        Assert.Same(thrown, await Assert.ThrowsAsync<InvalidOperationException>(() => first));
        await second;
        Assert.True(ranAfter);
        Assert.Equal(0, dispatch.Waiting);
    }
}
