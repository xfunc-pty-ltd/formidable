using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static Formidable.Sample.E2E.SamplePage;
using static Microsoft.Playwright.Assertions;

namespace Formidable.Sample.E2E;

/// <summary>
/// A hand-rolled native input carries the same contract a wrapped Formidable input gets for
/// free: the framework's own ValidationMessage speaks the engine's verdict, the field's id is
/// what the summary's click-to-focus looks for, and aria-invalid/aria-describedby track the
/// engine's state — conditional, not permanent, so a real fix clears them together.
/// </summary>
[Collection("e2e")]
public sealed class VanillaInteropJourney(SampleAppFixture app)
{
    [E2EFact]
    public async Task Native_input_speaks_formidables_verdict_with_the_aria_contract()
    {
        await using var session = await app.NewPageAsync("/vanilla");
        var page = session.Page;
        var nickname = page.Locator("[id$='-nickname']");

        await page.GetByRole(AriaRole.Button, new() { Name = "Submit", Exact = true }).ClickAsync();

        // Formidable's verdict rendered by the framework's own ValidationMessage, and the
        // conditional aria pair on the NATIVE input (the deterministic focus-id convention).
        await Expect(page.Locator(".validation-message")).ToHaveTextAsync("Nickname is required");
        await Expect(nickname).ToHaveAttributeAsync("aria-invalid", "true");
        await Expect(nickname).ToHaveAttributeAsync("aria-describedby", new Regex("-nickname-messages$"));

        await SummaryEntry(page, "Nickname is required").ClickAsync();
        await Expect(nickname).ToBeFocusedAsync();

        // Real-typed fix: the message leaves and aria-invalid leaves WITH it — conditional,
        // not permanent.
        await TypeAsync(nickname, "Ada");
        await TabAsync(page);
        await Expect(page.Locator(".validation-message")).ToHaveCountAsync(0);
        await Expect(nickname).Not.ToHaveAttributeAsync("aria-invalid", "true");

        // The aligned provider's modified-gated leg: a native input earns formidable-valid too.
        await Expect(nickname).ToHaveClassAsync(new Regex(@"\bformidable-valid\b"));
    }

    /// <summary>
    /// The page renders Nickname above Colour; the validator declares Colour first. A blocked
    /// submit follows the page: the summary leads with Nickname and the auto-focus lands there.
    /// </summary>
    [E2EFact]
    public async Task Blocked_submit_leads_with_the_first_field_on_the_page()
    {
        await using var session = await app.NewPageAsync("/vanilla");
        var page = session.Page;

        await page.GetByRole(AriaRole.Button, new() { Name = "Submit", Exact = true }).ClickAsync();

        await Expect(Summary(page)).ToBeVisibleAsync();
        await Expect(Summary(page).Locator("button.formidable-summary__link").First)
            .ToHaveTextAsync("Nickname is required");
        await Expect(page.Locator("[id$='-nickname']")).ToBeFocusedAsync();
    }
}
