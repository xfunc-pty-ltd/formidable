using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static Formidable.Sample.E2E.SamplePage;
using static Microsoft.Playwright.Assertions;

namespace Formidable.Sample.E2E;

/// <summary>
/// A hand-rolled native input carries the same contract a wrapped Formidable input gets for
/// free: the framework's own ValidationMessage speaks the engine's verdict, the field's id is
/// what the summary's click-to-focus looks for, and both aria attributes are conditional rather
/// than permanent — aria-invalid on the field carrying an error, aria-describedby on the message
/// element it names being rendered — so neither is on the input before the first message arrives,
/// and a real fix clears them together.
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
        var messagesId = new Regex("-nickname-messages$");

        // On a pristine form the native ValidationMessage has rendered no element at all, so
        // there is nothing for aria-describedby to name and the attribute is withheld — the
        // reading a permanent one would make impossible.
        await Expect(nickname).Not.ToHaveAttributeAsync("aria-describedby", messagesId);

        await page.GetByRole(AriaRole.Button, new() { Name = "Submit", Exact = true }).ClickAsync();

        // Formidable's verdict rendered by the framework's own ValidationMessage, and the two aria
        // attributes this page renders by hand on the NATIVE input: aria-invalid, which the page
        // reads off the engine and a native InputText also answers from the field's messages, both
        // with the same "true" or nothing; and aria-describedby (addressed through the
        // deterministic focus-id convention), which stands only while the element it names is on
        // the page. The page renders no aria-required, since its requirement is the one aria
        // attribute a Formidable input answers from the rules rather than from a pass.
        await Expect(page.Locator(".validation-message")).ToHaveTextAsync("Nickname is required");
        await Expect(nickname).ToHaveAttributeAsync("aria-invalid", "true");
        await Expect(nickname).ToHaveAttributeAsync("aria-describedby", messagesId);

        await SummaryEntry(page, "Nickname is required").ClickAsync();
        await Expect(nickname).ToBeFocusedAsync();

        // Real-typed fix: the message leaves and both aria attributes leave WITH it — conditional,
        // not permanent. The message element goes with the verdict, so an aria-describedby that
        // outlived it would name nothing.
        await TypeAsync(nickname, "Ada");
        await TabAsync(page);
        await Expect(page.Locator(".validation-message")).ToHaveCountAsync(0);
        await Expect(nickname).Not.ToHaveAttributeAsync("aria-invalid", "true");
        await Expect(nickname).Not.ToHaveAttributeAsync("aria-describedby", messagesId);

        // The aligned provider's modified-gated leg: a native input earns formidable-valid too.
        // Green asks for fresh submit coverage on top of touched/modified — on this page every
        // rule lives in the always-on bucket, so the live pass that cleared the message also
        // re-answered the whole submit selection, and Colour's still-failing verdict names
        // Colour alone: this field reads fresh and clean.
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
