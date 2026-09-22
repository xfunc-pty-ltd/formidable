using Microsoft.Playwright;
using static Formidable.Sample.E2E.SamplePage;
using static Microsoft.Playwright.Assertions;

namespace Formidable.Sample.E2E;

/// <summary>
/// Errors block, warnings never do. The warning rule lives on Description, not Title — Title
/// carries only the page's error rule — so a title that satisfies its own rule leaves the
/// Description advisory as the one thing standing between a blocked and an accepted submit.
/// </summary>
[Collection("e2e")]
public sealed class SeverityJourney(SampleAppFixture app)
{
    [E2EFact]
    public async Task Warnings_disclose_but_never_block()
    {
        await using var session = await app.NewPageAsync("/severity");
        var page = session.Page;

        // Errors block: the empty submit lands in the summary's error group.
        await page.GetByRole(AriaRole.Button, new() { Name = "Submit", Exact = true }).ClickAsync();
        await Expect(page.Locator(".formidable-summary__group--error")).ToContainTextAsync("Title is required");

        // Real-typed path: a description that trips the warning rule (Title carries no such rule).
        await TypeAsync(Field(page, "description"), "Great synth!");
        await TabAsync(page);
        await Expect(page.Locator("ul[id$='-description-messages'] .formidable-message--warning"))
            .ToContainTextAsync("Exclamation marks read as shouty — consider removing them");

        // Warnings never block: fill the field the error rule still needs, then submit goes
        // through and the advisory stays disclosed.
        await page.GetByLabel("Title", new() { Exact = true }).FillAsync("Launch day");
        await TabAsync(page);
        await page.GetByRole(AriaRole.Button, new() { Name = "Submit", Exact = true }).ClickAsync();
        await Expect(page.Locator(".formidable-summary__group--error")).ToHaveCountAsync(0);
        await Expect(MessagesFor(page, "description"))
            .ToContainTextAsync("Exclamation marks read as shouty — consider removing them");

        // Symmetry: fixing the field lets the live pass that commit starts withdraw the advisory
        // — this page runs the default LiveProfile, so that pass rebuilds the submit channel's
        // own source; the refresh behind it only reconfirms what already cleared.
        await Field(page, "description").FillAsync("Great synth");
        await TabAsync(page);
        await Expect(MessagesFor(page, "description")).ToHaveCountAsync(0, new() { Timeout = AsyncTimeoutMs });
    }

    // The page renders two FormidableSummary instances, Show="SummaryFilter.Errors" and
    // Show="SummaryFilter.Advisories". Show decides which fixed-role region a summary renders —
    // the Errors instance carries the page's only role="alert" region, the Advisories instance
    // its only role="status" one — so addressing the two regions proves the filters are doing
    // the work rather than merely that both messages appear somewhere on the page: an
    // unfiltered pair would render two regions of each role, and either locator would fail
    // with a strict-mode violation instead of quietly finding text.
    [E2EFact]
    public async Task Errors_and_advisories_render_as_separate_summaries()
    {
        await using var session = await app.NewPageAsync("/severity");
        var page = session.Page;

        await TypeAsync(Field(page, "description"), "Great synth!");
        await TabAsync(page);
        await page.GetByRole(AriaRole.Button, new() { Name = "Submit", Exact = true }).ClickAsync();

        var errorSummary = page.Locator(".formidable-summary__region--errors[role='alert']");
        var advisorySummary = page.Locator(".formidable-summary__region--advisories[role='status']");

        await Expect(errorSummary).ToContainTextAsync("Title is required");
        await Expect(errorSummary).Not.ToContainTextAsync("Exclamation marks read as shouty — consider removing them");

        await Expect(advisorySummary).ToContainTextAsync("Exclamation marks read as shouty — consider removing them");
        await Expect(advisorySummary).Not.ToContainTextAsync("Title is required");
    }
}
