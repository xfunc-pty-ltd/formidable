namespace Formidable.Blazor;

/// <summary>Posts one callback past the current call stack, once for however many requests arrive before that post runs.</summary>
/// <remarks>
/// A render batch raises its registry changes one call at a time and never says when it ends, so
/// a post is the first point at which the whole batch is known to have landed. One post per batch
/// does the work a post per change would, without queueing the rest to find nothing to do.
/// </remarks>
// Requested and run on the renderer's dispatcher: every request comes from inside a render batch
// or an event handler, and the post runs back on the context the request was made on.
internal sealed class BatchPost
{
    private readonly Action _callback;

    // Set as a post is made and cleared as it starts to run, so every request a batch makes while
    // its post waits is answered by that post.
    private bool _pending;

    /// <summary>Creates the helper over the callback each post runs.</summary>
    /// <param name="callback">What each post runs.</param>
    internal BatchPost(Action callback) => _callback = callback;

    /// <summary>How many posts have been made, so a test can tell one post per batch from one per request.</summary>
    internal int PostCount { get; private set; }

    /// <summary>Posts the callback unless a post is already waiting: through the <see cref="SynchronizationContext"/> current at the request where there is one, else after <see cref="Task.Yield"/>.</summary>
    /// <remarks>
    /// Blazor Server and bUnit install a context, and posting to it runs the callback once the
    /// batch that requested it has returned. WebAssembly installs none, and the yield queues the
    /// callback behind the call stack in the same way. A component's own <c>InvokeAsync</c> would
    /// not: it runs inline when the caller is already on the dispatcher.
    /// </remarks>
    internal void Request()
    {
        if (_pending)
        {
            return;
        }

        _pending = true;
        PostCount++;
        var context = SynchronizationContext.Current;
        if (context is null)
        {
            _ = RunAfterYieldAsync();
            return;
        }

        context.Post(static state => ((BatchPost)state!).Run(), this);
    }

    /// <summary>Runs the callback once the call stack that requested it has unwound, on a host with no <see cref="SynchronizationContext"/>.</summary>
    /// <returns>The task, which nothing awaits.</returns>
    // A throw from the callback surfaces as an unobserved task exception, as the post branch's
    // reaches the context's own handler: neither is caught and discarded here.
    private async Task RunAfterYieldAsync()
    {
        await Task.Yield();
        Run();
    }

    /// <summary>Clears the pending flag, then runs the callback.</summary>
    // Cleared first, so a callback that throws leaves the next request free to post, and a request
    // the callback itself causes gets a post of its own.
    private void Run()
    {
        _pending = false;
        _callback();
    }
}
