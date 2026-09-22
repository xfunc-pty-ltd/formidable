using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static Formidable.Sample.E2E.SamplePage;
using static Microsoft.Playwright.Assertions;

namespace Formidable.Sample.E2E;

/// <summary>
/// The page's whole lesson: the state class on the input is Bootstrap's own, not the kit's —
/// <c>FormidableCssClasses</c> only remaps the class NAMES, so <c>is-invalid</c> comes and goes
/// exactly the way it would under Bootstrap alone once the field is actually fixed.
/// </summary>
[Collection("e2e")]
public sealed class BootstrapJourney(SampleAppFixture app)
{
    [E2EFact]
    public async Task Names_invalid_class_clears_after_a_real_fix()
    {
        await using var session = await app.NewPageAsync("/bootstrap");
        var page = session.Page;
        var name = page.GetByLabel("Name", new() { Exact = true });

        await page.GetByRole(AriaRole.Button, new() { Name = "Submit", Exact = true }).ClickAsync();
        await Expect(name).ToHaveClassAsync(new Regex(@"\bis-invalid\b"));

        // Real-typed fix, blurred for real: this page's asserted path is the class itself.
        await TypeAsync(name, "Ada Lovelace");
        await TabAsync(page);
        await page.GetByRole(AriaRole.Button, new() { Name = "Submit", Exact = true }).ClickAsync();
        await Expect(name).Not.ToHaveClassAsync(new Regex(@"\bis-invalid\b"));
    }
}
