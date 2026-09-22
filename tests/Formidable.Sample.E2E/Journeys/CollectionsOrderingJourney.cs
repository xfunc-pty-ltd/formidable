using Microsoft.Playwright;
using static Formidable.Sample.E2E.SamplePage;
using static Microsoft.Playwright.Assertions;

namespace Formidable.Sample.E2E;

/// <summary>
/// The summary's read of "where things are on the page" has to track a row that MOVES, not just
/// one that leaves — the sibling case to <see cref="CollectionsJourney"/>'s removal coverage.
/// Two teams, each carrying exactly one failure with distinct text, prove the summary's order
/// follows the new on-screen order rather than the order the two teams last validated in. The
/// second test goes one level deeper: a reordered row's own member actions have to stay bound to
/// the team actually shown there, not to whichever team first rendered in that screen slot.
/// </summary>
[Collection("e2e")]
public sealed class CollectionsOrderingJourney(SampleAppFixture app)
{
    [E2EFact]
    public async Task Moving_a_team_up_re_sorts_the_summary()
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

        // Give team two a name, so its only failure is the seeded empty alias — two entries, one
        // per team, with no overlapping text to make the order ambiguous.
        await TypeAsync(Field(secondTeam, "name"), "Beta");
        await TabAsync(page);

        await page.GetByRole(AriaRole.Button, new() { Name = "Submit", Exact = true }).ClickAsync();

        var entries = page.Locator(".formidable-summary__link");
        await Expect(entries).ToHaveTextAsync(["Team name is required", "Alias is required"]);

        // Move team two above team one: the summary must re-list in the new on-screen order, not
        // the order the two teams last validated in.
        await secondTeam.GetByRole(AriaRole.Button, new() { Name = "Move team up", Exact = true }).ClickAsync();
        await Expect(entries).ToHaveTextAsync(["Alias is required", "Team name is required"]);
    }

    [E2EFact]
    public async Task Moving_a_team_up_keeps_member_actions_bound_to_its_own_row()
    {
        await using var session = await app.NewPageAsync("/collections");
        var page = session.Page;

        await page.GetByRole(AriaRole.Button, new() { Name = "Submit", Exact = true }).ClickAsync();

        // Move team two above team one: the fieldset now shown FIRST is team two's, not team
        // one's — a reordered row, not a freshly-built one.
        await page.Locator("form fieldset").Last
            .GetByRole(AriaRole.Button, new() { Name = "Move team up", Exact = true }).ClickAsync();
        var firstFieldset = page.Locator("form fieldset").First;

        // Team two seeds exactly one member, so removing it empties team two's OWN Members list.
        // A member action taken on the row now shown first must land on the team actually shown
        // there, not on whichever team originally occupied that screen position.
        await firstFieldset.GetByRole(AriaRole.Button, new() { Name = "Remove", Exact = true }).ClickAsync();

        await Expect(MessagesFor(firstFieldset, "members"))
            .ToHaveTextAsync(["Every team needs at least one member"]);
    }
}
