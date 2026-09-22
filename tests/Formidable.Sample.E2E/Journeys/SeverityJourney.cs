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

        // Symmetry: fixing the field lets the debounced refresh withdraw the advisory.
        await Field(page, "description").FillAsync("Great synth");
        await TabAsync(page);
        await Expect(MessagesFor(page, "description")).ToHaveCountAsync(0, new() { Timeout = AsyncTimeoutMs });
    }
}
