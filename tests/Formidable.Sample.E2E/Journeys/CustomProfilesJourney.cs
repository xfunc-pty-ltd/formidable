using Microsoft.Playwright;
using static Formidable.Sample.E2E.SamplePage;
using static Microsoft.Playwright.Assertions;

namespace Formidable.Sample.E2E;

/// <summary>
/// A <c>ProfiledValidator</c> picking an arbitrary named ruleset at runtime, not just the
/// built-in Draft/Submit pair: Standard submit passes without a review note, and switching to
/// Admin review resets the form (a fresh model is what makes the new <c>Options</c> take effect)
/// and adds a third ruleset's own requirement on top of Submit's.
/// </summary>
[Collection("e2e")]
public sealed class CustomProfilesJourney(SampleAppFixture app)
{
    [E2EFact]
    public async Task Standard_submit_blocks_then_passes_without_a_review_note()
    {
        await using var session = await app.NewPageAsync("/custom-profiles");
        var page = session.Page;

        await page.GetByRole(AriaRole.Button, new() { Name = "Submit", Exact = true }).ClickAsync();
        await Expect(Summary(page)).ToContainTextAsync("Title is required");
        await Expect(Summary(page)).ToContainTextAsync("Publish date is required");

        // Real-typed path: the number field takes keystrokes, the range rule answers at submit.
        await TypeAsync(Field(page, "readminutes"), "0");
        await TabAsync(page);
        await page.GetByLabel("Title", new() { Exact = true }).FillAsync("Release notes");
        await page.GetByLabel("Slug", new() { Exact = true }).FillAsync("release-notes");
        await Field(page, "category").SelectOptionAsync("Tutorial");
        await page.GetByRole(AriaRole.Button, new() { Name = "Submit", Exact = true }).ClickAsync();
        await Expect(MessagesFor(page, "readminutes"))
            .ToHaveTextAsync(["Read time must be between 1 and 180 minutes"]);

        await Field(page, "readminutes").FillAsync("5");

        // The date input carries UpdateOn="OnBlur": type segments for real, commit on Tab.
        // Order-agnostic segment trick (same as InputRegressions): every non-year segment types
        // the same two digits, so the final value is identical whichever segment is day and
        // which is month on this machine's locale.
        await Field(page, "publishdate").ClickAsync();
        foreach (var key in new[] { "0", "5", "0", "5", "2", "0", "2", "6" })
        {
            await page.Keyboard.PressAsync(key);
        }

        await Expect(Field(page, "publishdate")).ToHaveValueAsync("2026-05-05");
        await TabAsync(page);

        await page.GetByRole(AriaRole.Button, new() { Name = "Submit", Exact = true }).ClickAsync();
        // The page's own status line, not GetByRole(AriaRole.Status): the summary's persistent
        // advisories region carries role="status" too, so the role alone is two elements here.
        await Expect(page.Locator("p[role='status']"))
            .ToHaveTextAsync("Submitted under Standard submit — accepted.");
    }

    // Category carries UpdateOn="OnBlur": picking an option commits it at once, but a stale
    // verdict already on screen (from the blocked submit below) survives until the control is
    // actually left, proving the notification really did defer to blur rather than change.
    [E2EFact]
    public async Task Category_select_holds_its_stale_verdict_until_blur()
    {
        await using var session = await app.NewPageAsync("/custom-profiles");
        var page = session.Page;

        await page.GetByRole(AriaRole.Button, new() { Name = "Submit", Exact = true }).ClickAsync();
        await Expect(MessagesFor(page, "category")).ToHaveTextAsync(["Category is required"]);

        // Blocked submit auto-focuses the first failing field (Title), not Category, so focus is
        // put on the select explicitly before picking — otherwise Tab below would blur whichever
        // field the auto-focus landed on instead.
        await Field(page, "category").FocusAsync();
        await Field(page, "category").SelectOptionAsync("Tutorial");
        await Expect(MessagesFor(page, "category")).ToHaveTextAsync(["Category is required"]);

        await TabAsync(page);
        await Expect(MessagesFor(page, "category")).ToHaveCountAsync(0);
    }

    [E2EFact]
    public async Task Admin_review_resets_the_form_and_demands_the_note()
    {
        await using var session = await app.NewPageAsync("/custom-profiles");
        var page = session.Page;

        await page.GetByLabel("Title", new() { Exact = true }).FillAsync("Release notes");

        // GetByText("Admin review") would also match the TryIt paragraph's own use of the same
        // words, so the radio is addressed by its label association instead — unambiguous, and
        // the same idiom Smoke_server already uses for a radio.
        await page.GetByLabel("Admin review", new() { Exact = true }).CheckAsync();

        // The picker swaps Options, which only takes effect with a fresh model: the form resets.
        await Expect(page.GetByLabel("Title", new() { Exact = true })).ToHaveValueAsync("");

        // Fill everything Standard submit needs; the third ruleset still blocks without the note.
        // (fill() is setup here; the real-typed path lives in the first test.)
        await page.GetByLabel("Title", new() { Exact = true }).FillAsync("Release notes");
        await page.GetByLabel("Slug", new() { Exact = true }).FillAsync("release-notes");
        await Field(page, "category").SelectOptionAsync("Tutorial");
        await Field(page, "readminutes").FillAsync("5");
        await Field(page, "publishdate").FillAsync("2026-09-01");
        await TabAsync(page);
        await page.GetByRole(AriaRole.Button, new() { Name = "Submit", Exact = true }).ClickAsync();
        await Expect(Summary(page)).ToContainTextAsync("A review note is required for admin review");

        await page.GetByLabel("Review note", new() { Exact = true }).FillAsync("Checked.");
        await TabAsync(page);
        await page.GetByRole(AriaRole.Button, new() { Name = "Submit", Exact = true }).ClickAsync();
        await Expect(SummaryBands(page)).ToHaveCountAsync(0);
        await Expect(page.Locator("p[role='status']"))
            .ToHaveTextAsync("Submitted under Admin review — accepted.");
    }
}
