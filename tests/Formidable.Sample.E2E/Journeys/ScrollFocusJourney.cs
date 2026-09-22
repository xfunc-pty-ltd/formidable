using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static Formidable.Sample.E2E.SamplePage;
using static Microsoft.Playwright.Assertions;

namespace Formidable.Sample.E2E;

/// <summary>
/// Every target on this page is an ordinary input that is rendered and will take focus, so
/// click-to-focus needs no <c>FocusFallback</c> — the focus service's own scroll-into-view is the
/// whole story. The roster
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

    [E2EFact]
    public async Task A_blocked_submit_auto_focuses_the_first_summary_entrys_field()
    {
        await using var session = await app.NewPageAsync("/scroll-focus");
        var page = session.Page;

        await page.GetByRole(AriaRole.Button, new() { Name = "Submit", Exact = true }).ClickAsync();

        // No click yet — the field now focused is whatever FormidableForm's own default auto-focus
        // chose, addressed the same way every other focus target on this page is: by id. The focus
        // call is a JS interop round trip after the summary's own render, so wait for the id rather
        // than reading it the instant the summary appears.
        var focused = page.Locator(":focus");
        await Expect(focused).ToHaveAttributeAsync("id", new Regex("^formidable-"));
        var autoFocusedId = await focused.GetAttributeAsync("id");

        // Clicking the summary's FIRST entry focuses the same field the summary itself considers
        // the first visible issue — so the auto-focus above and the summary agree on what "first"
        // means, without this test having to know which team or member that field belongs to.
        await page.Locator(".formidable-summary__link").First.ClickAsync();
        await Expect(page.Locator(":focus")).ToHaveAttributeAsync("id", autoFocusedId!);
    }

    [E2EFact]
    public async Task Unticking_the_toggle_leaves_focus_where_it_was_on_a_blocked_submit()
    {
        await using var session = await app.NewPageAsync("/scroll-focus");
        var page = session.Page;

        await page.GetByLabel("Focus the first error automatically on a blocked submit", new() { Exact = true })
            .UncheckAsync();
        var submit = page.GetByRole(AriaRole.Button, new() { Name = "Submit", Exact = true });
        await submit.ClickAsync();
        await Expect(page.Locator(".formidable-summary")).ToBeVisibleAsync();

        await Expect(submit).ToBeFocusedAsync();
    }
}
