using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static Formidable.Sample.E2E.SamplePage;
using static Microsoft.Playwright.Assertions;

namespace Formidable.Sample.E2E;

/// <summary>
/// The draft-load API in a real browser: one call, three answers. A saved draft arrives holding
/// a good value, a wrong value and a field nobody ever filled in, and the form says something
/// different about each — confirmed, disclosed, and deliberately silent. The silent one is the
/// claim worth a browser: it is the difference between a form that reports what it knows and one
/// that nags about every empty required box the moment it opens.
/// </summary>
[Collection("e2e")]
public sealed class DraftLoadJourney(SampleAppFixture app)
{
    private static readonly Regex Valid = new(@"\bformidable-valid\b");
    private static readonly Regex Invalid = new(@"\bformidable-invalid\b");
    private static readonly Regex AnyStateClass =
        new(@"\bformidable-(valid|invalid|warning|info)\b");

    [E2EFact]
    public async Task A_loaded_draft_speaks_for_what_it_holds_and_stays_quiet_about_what_it_does_not()
    {
        await using var session = await app.NewPageAsync("/draft-load");
        var page = session.Page;
        var title = Field(page, "title");
        var email = Field(page, "contactemail");
        var summary = Field(page, "summary");

        // Step 1: three empty boxes and nothing said about any of them.
        await Expect(title).ToHaveValueAsync(string.Empty);
        await Expect(title).Not.ToHaveClassAsync(AnyStateClass);
        await Expect(email).Not.ToHaveClassAsync(AnyStateClass);
        await Expect(summary).Not.ToHaveClassAsync(AnyStateClass);

        // Step 2: one click loads all three values, and three different answers arrive with them.
        await page.GetByRole(AriaRole.Button, new() { Name = "Load saved draft" }).ClickAsync();

        await Expect(title).ToHaveValueAsync("Progressive disclosure in practice");
        await Expect(title).ToHaveClassAsync(Valid);
        await Expect(email).ToHaveClassAsync(Invalid);
        await Expect(MessagesFor(page, "contactemail"))
            .ToHaveTextAsync("That is not a valid email address");

        // Steps 3 and 4: the unfilled required field is marked and silent at once, and the
        // message the wrong value carries is the format rule's rather than the required rule's.
        await Expect(summary).ToHaveValueAsync(string.Empty);
        await Expect(summary).Not.ToHaveClassAsync(AnyStateClass);
        await Expect(MessagesFor(page, "summary")).ToHaveCountAsync(0);
        await Expect(page.Locator("label:has-text('Summary') .formidable-required")).ToBeVisibleAsync();
        await Expect(MessagesFor(page, "contactemail")).ToHaveCountAsync(1);

        // Step 5: finishing the address clears the message and confirms the field, the ordinary
        // way — the load left it engaged, so nothing here waits for a submit.
        await email.ClickAsync();
        await page.Keyboard.PressAsync("Control+a");
        await email.PressSequentiallyAsync("ada@example.com");
        await TabAsync(page);

        await Expect(MessagesFor(page, "contactemail")).ToHaveCountAsync(0);
        await Expect(email).ToHaveClassAsync(Valid);

        // Step 6: the field the load stayed quiet about confirms once it is genuinely filled in.
        await TypeAsync(summary, "Three answers from one call.");
        await TabAsync(page);
        await Expect(summary).ToHaveClassAsync(Valid);

        // Step 7: with all three answered, the submit goes through.
        await page.GetByRole(AriaRole.Button, new() { Name = "Submit" }).ClickAsync();
        await Expect(page.Locator("p[role='status']"))
            .ToHaveTextAsync(new Regex("the proposal is complete"));

        // Step 8: starting blank empties the boxes and takes every border with them.
        await page.GetByRole(AriaRole.Button, new() { Name = "Start blank" }).ClickAsync();
        await Expect(title).ToHaveValueAsync(string.Empty);
        await Expect(email).ToHaveValueAsync(string.Empty);
        await Expect(summary).ToHaveValueAsync(string.Empty);
        await Expect(title).Not.ToHaveClassAsync(AnyStateClass);
        await Expect(email).Not.ToHaveClassAsync(AnyStateClass);
        await Expect(summary).Not.ToHaveClassAsync(AnyStateClass);
    }
}
