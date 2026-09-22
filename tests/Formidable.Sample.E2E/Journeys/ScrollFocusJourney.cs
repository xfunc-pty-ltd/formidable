using Microsoft.Playwright;
using static Formidable.Sample.E2E.SamplePage;
using static Microsoft.Playwright.Assertions;

namespace Formidable.Sample.E2E;

/// <summary>
/// Everything on this page is already in the DOM, so click-to-focus needs no
/// <c>FocusFallback</c> — the focus service's own scroll-into-view is the whole story. The roster
/// seeds its very last row empty, so the last summary entry is the form's last field: the longest
/// ride the page can offer, pinned by both the resulting focus AND an actual page-scroll delta.
/// </summary>
[Collection("e2e")]
public sealed class ScrollFocusJourney(SampleAppFixture app)
{
    [E2EFact]
    public async Task Click_to_focus_scrolls_the_page_and_lands_on_the_last_row()
    {
        await using var session = await app.NewPageAsync("/scroll-focus");
        var page = session.Page;

        var before = await page.EvaluateAsync<double>("() => window.scrollY");
        await page.GetByRole(AriaRole.Button, new() { Name = "Submit", Exact = true }).ClickAsync();

        // The roster seeds its very last row empty, so the summary's last entry is the form's
        // last field — the longest ride click-to-focus can be asked for on this page.
        await page.Locator(".formidable-summary__link").Last.ClickAsync();
        await Expect(page.Locator("form fieldset").Last.Locator("input").Last).ToBeFocusedAsync();

        // The focus service scrolls the target into view, centred — with twelve teams of three
        // members the last row sits well below the fold, so the ride actually moves the page.
        Assert.NotEqual(before, await page.EvaluateAsync<double>("() => window.scrollY"));
    }
}
