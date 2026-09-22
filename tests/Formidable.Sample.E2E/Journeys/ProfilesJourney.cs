using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static Formidable.Sample.E2E.SamplePage;
using static Microsoft.Playwright.Assertions;

namespace Formidable.Sample.E2E;

/// <summary>Draft vs Submit: format rules answer live while typing; presence rules stay silent
/// until submit disclosure; a draft save never enforces completeness.</summary>
[Collection("e2e")]
public sealed class ProfilesJourney(SampleAppFixture app)
{
    [E2EFact]
    public async Task Formats_answer_live_and_presence_waits_for_submit()
    {
        await using var session = await app.NewPageAsync("/profiles");
        var page = session.Page;

        // Typing past the draft profile's length rule speaks live...
        await TypeAsync(Field(page, "title"), new string('x', 61));
        await TabAsync(page);
        await Expect(MessagesFor(page, "title")).ToHaveTextAsync(["Title is 60 characters max"]);

        // ...while the empty Summary says nothing until submit is the disclosure event.
        await Expect(MessagesFor(page, "summary")).ToHaveCountAsync(0);

        // The length rule is a Draft-profile format rule too, so a title over 60 characters
        // blocks Save draft as well — shorten to a valid title before saving.
        await Field(page, "title").FillAsync("A valid title");
        await TabAsync(page);

        await page.GetByRole(AriaRole.Button, new() { Name = "Save draft", Exact = true }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Status))
            .ToHaveTextAsync("Draft saved — completeness rules were not enforced.");

        await page.GetByRole(AriaRole.Button, new() { Name = "Submit", Exact = true }).ClickAsync();
        await Expect(Summary(page)).ToContainTextAsync("Summary is required to submit");
    }

    [E2EFact]
    public async Task Reset_returns_the_form_to_pristine()
    {
        await using var session = await app.NewPageAsync("/profiles");
        var page = session.Page;
        var title = Field(page, "title");

        await page.GetByRole(AriaRole.Button, new() { Name = "Submit", Exact = true }).ClickAsync();
        await Expect(Summary(page)).ToBeVisibleAsync();
        await Expect(title).ToHaveClassAsync(new Regex(@"\bformidable-invalid\b"));

        await page.GetByRole(AriaRole.Button, new() { Name = "Reset", Exact = true }).ClickAsync();

        await Expect(Summary(page)).ToHaveCountAsync(0);
        await Expect(title).Not.ToHaveClassAsync(new Regex(@"\bformidable-invalid\b"));
    }
}
