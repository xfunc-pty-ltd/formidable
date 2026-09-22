using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static Formidable.Sample.E2E.SamplePage;
using static Microsoft.Playwright.Assertions;

namespace Formidable.Sample.E2E;

/// <summary>
/// The kit paints nothing itself: every border, message, and spinner reads a handful of CSS
/// custom properties from the sample's own stylesheet. Picking a colour writes an inline style
/// on the wrapper — zero library involvement, the kit never sees a colour.
/// </summary>
[Collection("e2e")]
public sealed class CssColoursJourney(SampleAppFixture app)
{
    [E2EFact]
    public async Task Picking_a_colour_recolours_the_wrapper_token()
    {
        await using var session = await app.NewPageAsync("/css-colours");
        var page = session.Page;

        await page.GetByRole(AriaRole.Button, new() { Name = "Submit", Exact = true }).ClickAsync();
        await Expect(MessagesFor(page, "title")).ToHaveTextAsync(["Title is required"]);

        // The Error picker is the first of the five colour inputs (Error, Accent, Warning,
        // Info, Valid); colour inputs accept fill().
        await page.Locator("input[type=color]").First.FillAsync("#ff0000");

        // The wrapper is the div that directly holds the form; WrapperStyle writes the picked
        // colour onto its own --error custom property (and --error-text, the same value).
        var wrapper = page.Locator("div:has(> form)");
        await Expect(wrapper).ToHaveAttributeAsync("style", new Regex(@"--error:\s*#ff0000"));
    }

    [E2EFact]
    public async Task An_exclamation_mark_in_description_earns_the_warning_class()
    {
        await using var session = await app.NewPageAsync("/css-colours");
        var page = session.Page;

        await TypeAsync(Field(page, "description"), "Great synth!");
        await TabAsync(page);

        await Expect(Field(page, "description")).ToHaveClassAsync(new Regex(@"\bformidable-warning\b"));
    }
}
