using Microsoft.Playwright;
using static Formidable.Sample.E2E.SamplePage;
using static Microsoft.Playwright.Assertions;

namespace Formidable.Sample.E2E;

/// <summary>
/// The <c>DisclosureOverride</c> makes the summary speak for rows Virtualize has never rendered —
/// proven by counting entries against rendered inputs, not just reading one message — and clicking
/// a late entry exercises <c>FocusFallback</c>: the panel scrolls, the row materializes, and the
/// summary's retry lands focus on it.
/// </summary>
[Collection("e2e")]
public sealed class VirtualizedJourney(SampleAppFixture app)
{
    [E2EFact]
    public async Task First_submit_discloses_beyond_the_rendered_window_and_focus_falls_back()
    {
        await using var session = await app.NewPageAsync("/virtualized");
        var page = session.Page;

        await page.GetByRole(AriaRole.Button, new() { Name = "Submit", Exact = true }).ClickAsync();

        // The DisclosureOverride reports rows Virtualize has never rendered, from the first
        // submit — a pass over the whole collection, so the generous timeout.
        await Expect(Summary(page)).ToBeVisibleAsync(new() { Timeout = AsyncTimeoutMs });
        await Expect(Summary(page)).ToContainTextAsync("Serial is required");

        // More entries disclosed than inputs rendered: the override's whole point. The panel is
        // 20rem tall against a 96px row height, so only a handful of rows are ever in the DOM at
        // once — far fewer than the 28 empty serials (every 7th of 200 rows) the override reveals.
        var entries = await page.Locator(".formidable-summary__link").CountAsync();
        var rendered = await page.Locator("form input").CountAsync();
        Assert.True(entries > rendered,
            $"expected disclosure past the rendered window, got {entries} entries over {rendered} inputs");

        // A LATE entry's element does not exist yet: FocusFallback scrolls, the summary
        // retries once, and focus lands on a row Virtualize has now materialized.
        await page.Locator(".formidable-summary__link").Last.ClickAsync();
        await Expect(page.Locator("input:focus")).ToBeVisibleAsync(new() { Timeout = AsyncTimeoutMs });
    }
}
