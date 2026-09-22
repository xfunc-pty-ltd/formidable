using Microsoft.Playwright;
using static Formidable.Sample.E2E.SamplePage;
using static Microsoft.Playwright.Assertions;

namespace Formidable.Sample.E2E;

/// <summary>
/// The page's two disclosure mechanisms in one pass: a UI-collapsed section's unconditional rule
/// is suppressed until the section renders, the accommodation cascade's own <c>When</c> keeps its
/// rules from ever producing a suppressed issue in the first place, and collapsing a field back
/// down when it is the ONLY remaining failure trips the all-suppressed defensive gate — a
/// model-level entry that focuses the form itself.
/// </summary>
[Collection("e2e")]
public sealed class DisclosureJourney(SampleAppFixture app)
{
    [E2EFact]
    public async Task Suppression_reveal_and_the_defensive_gate()
    {
        await using var session = await app.NewPageAsync("/disclosure");
        var page = session.Page;

        // Rendered fields disclose; the collapsed traveler section's issue is suppressed.
        await page.GetByRole(AriaRole.Button, new() { Name = "Submit", Exact = true }).ClickAsync();
        await Expect(Summary(page)).ToContainTextAsync("Destination is required");
        await Expect(Summary(page)).ToContainTextAsync("Answer the accommodation question");
        await Expect(Summary(page)).Not.ToContainTextAsync("Traveler name is required");

        // The NotifyChanged-wired controls run passes without a submit: answering the
        // accommodation question updates its message in place. The "Yes" radio is the one
        // element the fieldset gives an id, so it is addressed the same way a wrapped input is.
        await Field(page, "needsaccommodation").CheckAsync();
        await Expect(MessagesFor(page, "needsaccommodation")).ToHaveCountAsync(0);

        // Reveal the traveler section: its rule now registers and the next submit discloses it.
        await page.GetByRole(AriaRole.Button, new() { Name = "Show traveler details", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Submit", Exact = true }).ClickAsync();
        await Expect(Summary(page)).ToContainTextAsync("Traveler name is required");

        // Real-typed path: fill the other visible fields validly, then collapse the traveler
        // section again so ONLY its (still-empty) rule remains — suppressed, with nowhere else
        // for the submit to fail, which trips the defensive gate.
        await TypeAsync(Field(page, "destination"), "Paris");
        await TabAsync(page);
        await Field(page, "accommodationtype").SelectOptionAsync("Hotel");
        await page.GetByRole(AriaRole.Button, new() { Name = "Hide traveler details", Exact = true }).ClickAsync();

        await page.GetByRole(AriaRole.Button, new() { Name = "Submit", Exact = true }).ClickAsync();
        await SummaryEntry(
                page,
                "The form cannot be submitted because information that is not currently displayed is invalid.")
            .ClickAsync();
        await Expect(page.Locator("[id$='-form']")).ToBeFocusedAsync();
    }
}
