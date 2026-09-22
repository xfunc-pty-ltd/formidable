using Microsoft.Playwright;
using static Formidable.Sample.E2E.SamplePage;
using static Microsoft.Playwright.Assertions;

namespace Formidable.Sample.E2E;

/// <summary>
/// Row identity, not position, is what keeps a message glued to its row: clearing one team's
/// name and then removing the OTHER team leaves the surviving message right where it belongs.
/// The second test is the collection-level counterpart — a rule against the list itself, with
/// nothing to focus but the container that carries the collection's own id. The third removes a
/// row that itself carries an error and checks the SUMMARY, not just the row's own inline
/// message — the removed row's entry must leave with it, and a surviving row's distinct entry
/// must stay.
/// </summary>
[Collection("e2e")]
public sealed class CollectionsJourney(SampleAppFixture app)
{
    [E2EFact]
    public async Task Messages_stick_to_their_row_across_removal()
    {
        await using var session = await app.NewPageAsync("/collections");
        var page = session.Page;

        // Clear the FIRST team's name with real keystrokes (select-all, then delete), then a
        // real blur — the typing policy's fully real-typed, real-blurred path for this journey.
        var firstTeam = page.Locator("form fieldset").First;
        var name = Field(firstTeam, "name");
        await name.ClickAsync();
        await page.Keyboard.PressAsync("Control+a");
        await page.Keyboard.PressAsync("Delete");
        await TabAsync(page);
        await page.GetByRole(AriaRole.Button, new() { Name = "Submit", Exact = true }).ClickAsync();
        await Expect(MessagesFor(firstTeam, "name")).ToHaveTextAsync(["Team name is required"]);

        // Remove the OTHER team: the message survives on the row it belongs to.
        await page.GetByRole(AriaRole.Button, new() { Name = "Remove team", Exact = true }).Last.ClickAsync();
        await Expect(page.Locator("form fieldset")).ToHaveCountAsync(1);
        await Expect(MessagesFor(page.Locator("form fieldset").First, "name"))
            .ToHaveTextAsync(["Team name is required"]);
    }

    [E2EFact]
    public async Task The_collection_rule_speaks_from_the_container()
    {
        await using var session = await app.NewPageAsync("/collections");
        var page = session.Page;
        var removeTeam = page.GetByRole(AriaRole.Button, new() { Name = "Remove team", Exact = true });

        // The page seeds two teams, so the roster's own rule only speaks once both are gone.
        await removeTeam.First.ClickAsync();
        await removeTeam.First.ClickAsync();
        await Expect(page.Locator("form fieldset")).ToHaveCountAsync(0);
        await page.GetByRole(AriaRole.Button, new() { Name = "Submit", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Add at least one team", Exact = true }).ClickAsync();

        // A collection rule fails against the list, so its entry focuses the container carrying
        // the collection's id — the only element on the page whose id ends that way.
        await Expect(page.Locator("[id$='-teams']")).ToBeFocusedAsync();
    }

    [E2EFact]
    public async Task Removing_a_row_clears_it_from_the_summary()
    {
        await using var session = await app.NewPageAsync("/collections");
        var page = session.Page;
        var firstTeam = page.Locator("form fieldset").First;
        var secondTeam = page.Locator("form fieldset").Last;

        // Clear team one's name — its own distinct failure — and fill its short member's alias
        // so team one contributes exactly one summary entry.
        var name = Field(firstTeam, "name");
        await name.ClickAsync();
        await page.Keyboard.PressAsync("Control+a");
        await page.Keyboard.PressAsync("Delete");
        await TabAsync(page);
        await TypeAsync(Field(firstTeam, "alias").Last, "bee");
        await TabAsync(page);

        // Give team two a name, so its only failure is the seeded empty alias — the summary now
        // carries exactly two entries, one per team, with no overlapping text between them.
        await TypeAsync(Field(secondTeam, "name"), "Beta");
        await TabAsync(page);

        await page.GetByRole(AriaRole.Button, new() { Name = "Submit", Exact = true }).ClickAsync();
        await Expect(SummaryEntry(page, "Team name is required")).ToBeVisibleAsync();
        await Expect(SummaryEntry(page, "Alias is required")).ToBeVisibleAsync();

        // Remove the failing team: its message must leave the SUMMARY with it, not linger there
        // for a row that no longer exists — the surviving team's distinct entry stays put.
        await firstTeam.GetByRole(AriaRole.Button, new() { Name = "Remove team", Exact = true }).ClickAsync();

        await Expect(page.Locator("form fieldset")).ToHaveCountAsync(1);
        await Expect(SummaryEntry(page, "Team name is required")).ToHaveCountAsync(0);
        await Expect(SummaryEntry(page, "Alias is required")).ToBeVisibleAsync();
    }
}
