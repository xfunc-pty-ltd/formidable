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

    // This page is where the summary's region-identity contract is browser-pinned, because it has
    // the right rule shape for it: one Show="All" summary whose advisories region can hold a
    // warning while a later submit inserts an error ABOVE it. The regions must be persistent DOM
    // NODES, not merely persistent markup: Blazor's diff matches sibling frames by SEQUENCE
    // NUMBER, so a summary that numbered the advisories region after the errors region's
    // variable-length content would silently REPLACE the advisories element whenever error
    // content changed ahead of it — putting the warning band inside a role="status" element
    // created in that same render, the announcement-dropping shape the fixed-role regions exist
    // to retire. Each region therefore builds inside its own sequence space
    // (RenderTreeBuilder.OpenRegion), which bUnit cannot see — markup is identical either way —
    // so the pin is a JS expando stamped on the live node: it survives the diff only if the diff
    // kept the node. Mutation that must break this: number the regions from one running counter
    // threaded through the band content (the stamp vanishes with the replaced element).
    [E2EFact]
    public async Task The_status_region_survives_an_error_arriving_above_it()
    {
        await using var session = await app.NewPageAsync("/css-colours");
        var page = session.Page;
        var statusRegion = page.Locator(".formidable-summary__region--advisories");

        // A committed "!" fills the status region while the errors region stays empty.
        await TypeAsync(Field(page, "description"), "Great synth!");
        await TabAsync(page);
        await Expect(statusRegion.Locator(".formidable-summary__band--warning")).ToHaveCountAsync(1);

        await statusRegion.EvaluateAsync("el => { el._formidableRegionPin = 'stamped'; }");

        // The blocked submit inserts the Title error into the alert region, ABOVE the stamped node.
        await page.GetByRole(AriaRole.Button, new() { Name = "Submit", Exact = true }).ClickAsync();
        await Expect(page.Locator(".formidable-summary__region--errors .formidable-summary__band--error"))
            .ToHaveCountAsync(1);

        var pin = await statusRegion.EvaluateAsync<string?>("el => el._formidableRegionPin ?? null");
        Assert.Equal("stamped", pin);
    }
}
