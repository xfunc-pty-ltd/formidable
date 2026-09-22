using Microsoft.Playwright;
using static Formidable.Sample.E2E.SamplePage;
using static Microsoft.Playwright.Assertions;

namespace Formidable.Sample.E2E;

/// <summary>
/// The front-door cycle every other page builds on: an empty submit is blocked and discloses,
/// the summary and the field messages agree, fixing the fields by real typing clears them, and
/// the resubmit goes through.
/// </summary>
[Collection("e2e")]
public sealed class QuickstartJourney(SampleAppFixture app)
{
    [E2EFact]
    public async Task Blocked_submit_discloses_then_a_fixed_form_goes_through()
    {
        await using var session = await app.NewPageAsync("/");
        var page = session.Page;

        await page.GetByRole(AriaRole.Button, new() { Name = "Submit", Exact = true }).ClickAsync();

        await Expect(Summary(page)).ToContainTextAsync("Name is required");
        await Expect(MessagesFor(page, "name")).ToHaveTextAsync(["Name is required"]);

        // The fully real-typed path: keystrokes and a real blur, per the typing policy.
        await TypeAsync(Field(page, "name"), "Ada Lovelace");
        await TabAsync(page);
        await Expect(MessagesFor(page, "name")).ToHaveCountAsync(0);

        await TypeAsync(Field(page, "email"), "ada@example.test");
        await TabAsync(page);

        await page.GetByRole(AriaRole.Button, new() { Name = "Submit", Exact = true }).ClickAsync();
        await Expect(Summary(page)).ToHaveCountAsync(0);
        await Expect(page.Locator("p[role='status']")).ToHaveTextAsync("Submitted — thanks, Ada Lovelace!");
    }
}
