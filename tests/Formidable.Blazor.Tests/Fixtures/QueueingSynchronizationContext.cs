namespace Formidable.Blazor.Tests.Fixtures;

/// <summary>
/// A synchronization context whose posts wait in a queue until <see cref="Drain"/> runs them on
/// the test's own thread. A test that drives <c>BatchPost</c> or a directly built engine installs
/// it, so a posted round runs exactly when the test drains it. xunit's own context posts to worker
/// threads, so a test that only awaited would race the round it means to observe.
/// </summary>
public sealed class QueueingSynchronizationContext : SynchronizationContext
{
    private readonly Queue<(SendOrPostCallback Callback, object? State)> _queue = new();

    /// <summary>How many posts are waiting for <see cref="Drain"/>.</summary>
    public int Pending => _queue.Count;

    /// <summary>Queues the callback; nothing runs until <see cref="Drain"/>.</summary>
    public override void Post(SendOrPostCallback d, object? state) => _queue.Enqueue((d, state));

    /// <summary>
    /// Makes this the current context until the returned scope is disposed, which restores the one
    /// it replaced. Await nothing inside the scope: a continuation would be posted here and wait
    /// for a drain that never comes.
    /// </summary>
    public IDisposable Install()
    {
        var replaced = Current;
        SetSynchronizationContext(this);
        return new Scope(replaced);
    }

    /// <summary>
    /// Runs the queued posts in order on the calling thread, with this context current, including
    /// any a callback posts while it runs. A callback that throws stops the drain there: the
    /// exception reaches the caller and the posts behind it stay queued.
    /// </summary>
    public void Drain()
    {
        var ambient = Current;
        SetSynchronizationContext(this);
        try
        {
            while (_queue.TryDequeue(out var post))
            {
                post.Callback(post.State);
            }
        }
        finally
        {
            SetSynchronizationContext(ambient);
        }
    }

    private sealed class Scope(SynchronizationContext? replaced) : IDisposable
    {
        public void Dispose() => SetSynchronizationContext(replaced);
    }
}
