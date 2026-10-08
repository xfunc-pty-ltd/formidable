using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Formidable.Sample.E2E;

/// <summary>
/// The locator vocabulary the browser tests share, expressed in the library's own observable
/// shapes: the <c>formidable-*</c> classes, the deterministic element ids, and the message
/// literals the shipped validators write. An element id embeds a hash of the owning model
/// instance, so a field is always addressed by the tail of its id — never by a spelled-out id,
/// and never by position.
/// </summary>
internal static class SamplePage
{
    /// <summary>
    /// Room for a simulated server call (600 ms on /async, 300 ms on /workout), the round trip to
    /// the sample API behind it, and the validation pass it feeds, on top of a cold context's
    /// WASM boot.
    /// </summary>
    public const int AsyncTimeoutMs = 15_000;

    /// <summary>The form-wide issue summary's persistent wrapper — always in the DOM while a
    /// summary component is on the page, its fixed-role regions standing empty when the form has
    /// nothing to show. Those regions also mean a bare role locator (GetByRole Status/Alert) is
    /// ambiguous on summary pages; address the page's own status line as p[role='status'].</summary>
    public static ILocator Summary(IPage page) => page.Locator(".formidable-summary");

    /// <summary>The summary's severity bands — present exactly while the summary has something
    /// to show, so a zero count here is what "the summary has nothing to say" looks like.</summary>
    public static ILocator SummaryBands(IPage page) => page.Locator(".formidable-summary__band");

    /// <summary>A summary entry, addressed by the message it carries — the only thing a reader of
    /// the summary can see, and what they would click.</summary>
    public static ILocator SummaryEntry(IPage page, string message) =>
        Summary(page).GetByRole(AriaRole.Button, new() { Name = message, Exact = true });

    /// <summary>A field's element, addressed by the tail of the id the kit gives it.</summary>
    public static ILocator Field(IPage page, string field) => page.Locator($"[id$='-{field}']");

    /// <summary>The same field, inside one collection row. Every row of a collection renders the
    /// same field names, so the row — located by content of its own — is what tells them apart.</summary>
    public static ILocator Field(ILocator row, string field) => row.Locator($"[id$='-{field}']");

    /// <summary>A text field, addressed by its accessible name: the label's text without the
    /// required mark, which is <c>aria-hidden</c>. An exact <c>GetByLabel</c> reads the label's
    /// text content, mark included, so it stops matching a field once the page marks it.</summary>
    public static ILocator TextBox(IPage page, string name) =>
        page.GetByRole(AriaRole.Textbox, new() { Name = name, Exact = true });

    /// <summary>A field's message list, addressed the way the kit builds it: the field's element
    /// id with "-messages" appended.</summary>
    public static ILocator MessagesFor(IPage page, string field) =>
        page.Locator($"ul[id$='-{field}-messages'] .formidable-message");

    /// <summary>The same message list, inside one collection row (see <see cref="Field(ILocator, string)"/>).</summary>
    public static ILocator MessagesFor(ILocator row, string field) =>
        row.Locator($"ul[id$='-{field}-messages'] .formidable-message");

    /// <summary>A field's state class, <c>formidable-</c> followed by <paramref name="state"/>
    /// (<c>valid</c>, <c>invalid</c>, <c>warning</c>, <c>info</c> or <c>pending</c>), matched as a
    /// whole name in the element's class list, so it never matches part of another class.</summary>
    public static Regex StateClass(string state) =>
        new(@"(^|\s)" + Regex.Escape("formidable-" + state) + @"(\s|$)");

    /// <summary>The stylesheet links of the two UI libraries the sample fits. Each belongs to its
    /// own page, so every other page has none.</summary>
    public static ILocator LibraryStylesheets(IPage page) =>
        page.Locator("link[href*='MudBlazor.min.css'], link[href*='bootstrap.min.css']");

    /// <summary>Opens a sample page from the sidebar, as a visitor moves between pages, and waits
    /// for its heading, which on the pages this serves reads as the sidebar's link. The app keeps
    /// running across the move, so a file an earlier page loaded stays loaded.</summary>
    public static async Task OpenFromSidebarAsync(IPage page, string name)
    {
        await page.GetByRole(AriaRole.Navigation, new() { Name = "Sample pages" })
            .GetByRole(AriaRole.Link, new() { Name = name, Exact = true })
            .ClickAsync();
        await Assertions.Expect(page.Locator("h1")).ToHaveTextAsync(name);
    }

    /// <summary>Starts recording what the page reports as broken: an uncaught script error, or an
    /// error written to the console, which is where Blazor reports an exception no component
    /// handled. Read it once the step under test has had its chance to fail.</summary>
    public static ConcurrentQueue<string> RecordErrors(IPage page)
    {
        var errors = new ConcurrentQueue<string>();
        page.PageError += (_, error) => errors.Enqueue(error);
        page.Console += (_, message) =>
        {
            if (message.Type == "error")
            {
                errors.Enqueue(message.Text);
            }
        };
        return errors;
    }

    /// <summary>Real keystrokes into a field: click to focus, then type character by character.
    /// The typing policy's tool — fill() sets a whole value in one event and cannot exercise
    /// caret movement, per-keystroke passes, segment editors, or ghost text.</summary>
    public static async Task TypeAsync(ILocator field, string text)
    {
        await field.ClickAsync();
        await field.PressSequentiallyAsync(text);
    }

    /// <summary>A real blur: Tab moves focus the way a visitor leaves a field.</summary>
    public static Task TabAsync(IPage page) => page.Keyboard.PressAsync("Tab");

    /// <summary>
    /// A real press-hold-release on <paramref name="target"/>: the pointer moves to the element's
    /// centre, presses, holds, and releases where it pressed. <c>ClickAsync</c> cannot stand in
    /// for it — a Playwright click moves, presses and releases as one command over a target it
    /// resolves once, so a page that moves the element out from under the pointer between down
    /// and up is a shape that command cannot express, and the browser's own
    /// cancel-on-different-target rule is invisible to it.
    /// </summary>
    public static async Task PressHoldReleaseAsync(IPage page, ILocator target, int holdMilliseconds)
    {
        var (x, y) = await CentreOfAsync(target);
        await page.Mouse.MoveAsync(x, y);
        await page.Mouse.DownAsync();
        await page.WaitForTimeoutAsync(holdMilliseconds);
        await page.Mouse.UpAsync();
    }

    /// <summary>
    /// The viewport coordinates of an element's centre. The element is scrolled to the middle of
    /// the viewport first, so a gesture built on these has room on every side of it — a pointer
    /// move measured from an element sitting against the bottom edge would be clamped instead of
    /// landing where the test asked for.
    /// </summary>
    public static async Task<(float X, float Y)> CentreOfAsync(ILocator target)
    {
        await target.EvaluateAsync("element => element.scrollIntoView({ block: 'center' })");
        await WaitForScrollToRestAsync(target);
        var box = await target.BoundingBoxAsync()
            ?? throw new InvalidOperationException(
                "The element has no layout box, so there is nowhere to aim a pointer gesture at it.");
        return (box.X + (box.Width / 2), box.Y + (box.Height / 2));
    }

    /// <summary>
    /// Waits until the scroll started above has come to rest, so a centre measured from the
    /// element describes where the pointer will actually land. The sample asks for
    /// <c>scroll-behavior: smooth</c>, so <c>scrollIntoView</c> returns while the element is still
    /// travelling: a box read straight after it aims the gesture at a position the element leaves,
    /// and the page then keeps moving under a pointer the test is holding still — which is a
    /// displacement, and the displaced-click guard is built to recover exactly that. A gesture
    /// meant to stand for a stationary press has to start from a stationary page.
    /// Rest is three consecutive frames whose top edge has not moved, rather than a fixed delay:
    /// a delay long enough for one machine is a guess on every other, and the resting position is
    /// the thing actually being waited for.
    /// </summary>
    private static Task WaitForScrollToRestAsync(ILocator target) => target.EvaluateAsync(
        @"element => new Promise(resolve => {
            let last = null;
            let still = 0;
            let frames = 0;
            const tick = () => {
                const top = element.getBoundingClientRect().top;
                still = last !== null && Math.abs(top - last) < 0.5 ? still + 1 : 0;
                last = top;
                // Bounded, so a page that never settles fails on the assertion the test came for
                // rather than hanging here with nothing to read.
                if (still >= 3 || ++frames > 180) {
                    resolve();
                    return;
                }
                requestAnimationFrame(tick);
            };
            requestAnimationFrame(tick);
        })");
}
