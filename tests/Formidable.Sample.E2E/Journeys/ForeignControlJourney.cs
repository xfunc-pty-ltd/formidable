using Microsoft.Playwright;
using static Formidable.Sample.E2E.SamplePage;
using static Microsoft.Playwright.Assertions;

namespace Formidable.Sample.E2E;

/// <summary>
/// A control Formidable does not wrap still carries the full seam: the renderless
/// FormidableField hands its template the element id, css class, and aria attributes a wrapper
/// would otherwise supply, the summary's click-to-focus reaches it exactly like a wrapped input,
/// and NotifyChanged runs a live pass with no submit involved.
/// </summary>
[Collection("e2e")]
public sealed class ForeignControlJourney(SampleAppFixture app)
{
    [E2EFact]
    public async Task The_seam_carries_messages_focus_and_live_passes()
    {
        await using var session = await app.NewPageAsync("/foreign");
        var page = session.Page;

        await page.GetByRole(AriaRole.Button, new() { Name = "Submit", Exact = true }).ClickAsync();
        await Expect(MessagesFor(page, "colour")).ToHaveTextAsync(["Colour is required"]);

        // Summary click-to-focus reaches a foreign control exactly like a wrapped input.
        await SummaryEntry(page, "Colour is required").ClickAsync();
        await Expect(page.Locator("[id$='-colour']")).ToBeFocusedAsync();

        // NotifyChanged is the seam: picking an option runs a pass with no submit involved.
        await Field(page, "colour").SelectOptionAsync(new SelectOptionValue { Index = 1 });
        await Expect(MessagesFor(page, "colour")).ToHaveCountAsync(0);

        await page.GetByRole(AriaRole.Button, new() { Name = "Submit", Exact = true }).ClickAsync();
        await Expect(SummaryBands(page)).ToHaveCountAsync(0);
    }
}
