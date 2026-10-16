using Microsoft.Playwright;
using static Formidable.Sample.E2E.SamplePage;
using static Microsoft.Playwright.Assertions;

namespace Formidable.Sample.E2E;

/// <summary>
/// The browser reports every change of shape under the form, and the form answers each by
/// measuring where its fields sit. Only a browser holds the observer that sends those reports, so
/// only a browser can show that a report redraws nothing: the page's own renders re-set a
/// consumer-shaped component's parameters once each, and the reports they cause add none.
/// </summary>
[Collection("e2e")]
public sealed class LayoutReportJourney(SampleAppFixture app)
{
    // Three changes of shape with no registration among them: a note added, the note removed, and
    // the two fields swapped by key. The swap is also the clock. Its report is answered after any
    // report the toggles sent, and the summary following the new order is what says it was
    // answered, so the count read then covers all three. Mutation that must break this: answer a
    // report with a render of the form, and the count climbs by one per report.
    [E2EFact]
    public async Task A_layout_report_measures_the_fields_without_redrawing_the_form()
    {
        await using var session = await app.NewSessionAsync();
        var page = session.Page;

        await page.GotoAsync(app.WebAppOrigin + "/layout-report");
        var submit = page.GetByRole(AriaRole.Button, new() { Name = "Submit", Exact = true });
        await Expect(submit).ToHaveCountAsync(1, new() { Timeout = AsyncTimeoutMs });

        // Errors on both fields, listed in the page's order, with focus on the first. The submit
        // renders the form once more after the focus move, but the browser answers the focus call
        // before Playwright can see the focus, the click follows that answer on the same
        // connection, and the circuit handles them in order. So the toggle's render, after which
        // the baseline is read, lands behind every render the submit makes.
        await submit.ClickAsync();
        await Expect(SummaryLinks(page)).ToHaveTextAsync(["Name is required", "Email is required"]);
        await Expect(Field(page, "name")).ToBeFocusedAsync();

        var counter = page.Locator("#parameter-sets");
        await page.ClickAsync("#toggle-note");
        await Expect(page.Locator("#note")).ToHaveCountAsync(1);
        var baseline = int.Parse((await counter.GetAttributeAsync("data-parameter-sets"))!);

        await page.ClickAsync("#toggle-note");
        await Expect(page.Locator("#note")).ToHaveCountAsync(0);

        await page.ClickAsync("#swap-fields");
        await Expect(SummaryLinks(page)).ToHaveTextAsync(["Email is required", "Name is required"]);

        // Two renders of the page since the baseline, one per click, and nothing else.
        await Expect(counter).ToHaveAttributeAsync("data-parameter-sets", (baseline + 2).ToString());
    }

    private static ILocator SummaryLinks(IPage page) => page.Locator(".formidable-summary__link");
}
