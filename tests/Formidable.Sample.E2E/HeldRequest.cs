using Microsoft.Playwright;

namespace Formidable.Sample.E2E;

/// <summary>
/// One file the page asks for, or one request it sends, held back until the test lets it through. While it is held the
/// page can be read in the state it shows while that file downloads, for as long as the test
/// needs, rather than caught on the way past. Disposing lets it through, so a failed assertion
/// never leaves the request parked.
/// </summary>
internal sealed class HeldRequest : IAsyncDisposable
{
    private readonly TaskCompletionSource _requested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private HeldRequest()
    {
    }

    /// <summary>Holds every request whose URL matches <paramref name="pattern"/> from here on.</summary>
    public static async Task<HeldRequest> HoldAsync(IPage page, string pattern)
    {
        var held = new HeldRequest();
        await page.RouteAsync(pattern, async route =>
        {
            held._requested.TrySetResult();
            await held._released.Task;
            try
            {
                await route.ContinueAsync();
            }
            catch (PlaywrightException)
            {
                // The page dropped the request while it was held: it left the page that asked.
            }
        });
        return held;
    }

    /// <summary>Holds only the next request whose URL matches <paramref name="pattern"/> and whose
    /// method is <paramref name="method"/>, so a test can make one reply arrive after a later one.
    /// Every other request, a CORS preflight included, goes straight through.</summary>
    public static async Task<HeldRequest> HoldNextAsync(IPage page, string pattern, string method)
    {
        var held = new HeldRequest();
        var taken = 0;
        await page.RouteAsync(pattern, async route =>
        {
            if (route.Request.Method != method || Interlocked.Exchange(ref taken, 1) == 1)
            {
                await route.ContinueAsync();
                return;
            }

            held._requested.TrySetResult();
            await held._released.Task;
            try
            {
                await route.ContinueAsync();
            }
            catch (PlaywrightException)
            {
                // The page dropped the request while it was held: it left the page that asked.
            }
        });
        return held;
    }

    /// <summary>Completes once the page has asked for the file, which is the moment the state to
    /// read exists.</summary>
    public Task RequestedAsync() =>
        _requested.Task.WaitAsync(TimeSpan.FromMilliseconds(SamplePage.AsyncTimeoutMs));

    /// <summary>Lets the held request through, and every later one.</summary>
    public void Release() => _released.TrySetResult();

    public ValueTask DisposeAsync()
    {
        Release();
        return ValueTask.CompletedTask;
    }
}
