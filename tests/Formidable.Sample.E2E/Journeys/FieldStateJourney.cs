using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static Formidable.Sample.E2E.SamplePage;
using static Microsoft.Playwright.Assertions;

namespace Formidable.Sample.E2E;

/// <summary>
/// <c>FieldState</c> read straight off the DOM: the state-table's booleans and the rendered
/// valid-class agree, for a field touched by blur alone as much as for one a real edit modifies —
/// both fields on the page are Formidable inputs, so both take the touched-or-modified valid rule
/// (<c>FormidableCss.Compute</c>) the same way, the alignment the class provider shares with a
/// native input under the same EditContext.
/// </summary>
[Collection("e2e")]
public sealed class FieldStateJourney(SampleAppFixture app)
{
    [E2EFact]
    public async Task The_visualizer_tells_the_truth_while_a_real_user_types()
    {
        await using var session = await app.NewPageAsync("/field-state");
        var page = session.Page;
        var usernameRow = page.Locator(".state-table tbody tr:has-text('Username')");
        var displayNameRow = page.Locator(".state-table tbody tr:has-text('Display name')");

        // Touched flips on blur alone — the page's MarkTouched splat (its whole lesson). With
        // nothing typed and no rule to fail, touched alone already earns formidable-valid.
        await page.GetByLabel("Username", new() { Exact = true }).PressAsync("Tab");
        await Expect(usernameRow).ToHaveTextAsync(new Regex("^UsernameTrueFalse"));
        await Expect(page.GetByLabel("Username", new() { Exact = true }))
            .ToHaveClassAsync(new Regex(@"\bformidable-valid\b"));

        // Modified flips on a real edit; the valid class is touched/modified plus HasErrors
        // clearing, computed independent of IsValidating — the timeout here is for HasErrors to
        // update once the async check resolves, and both the table's read and the class settle
        // on that same update.
        await TypeAsync(page.GetByLabel("Username", new() { Exact = true }), "ada");
        await TabAsync(page);
        await Expect(usernameRow).ToHaveTextAsync(new Regex("^UsernameTrueTrue"), new() { Timeout = AsyncTimeoutMs });
        await Expect(page.GetByLabel("Username", new() { Exact = true }))
            .ToHaveClassAsync(new Regex(@"\bformidable-valid\b"), new() { Timeout = AsyncTimeoutMs });

        // Display name never takes a keystroke here, only a blur — the same touched-alone path,
        // on the page's other field, lands on the identical class: the alignment holds per field,
        // not just for the one already exercised above.
        await page.GetByLabel("Display name", new() { Exact = true }).PressAsync("Tab");
        await Expect(displayNameRow).ToHaveTextAsync(new Regex("^Display nameTrueFalse"));
        await Expect(page.GetByLabel("Display name", new() { Exact = true }))
            .ToHaveClassAsync(new Regex(@"\bformidable-valid\b"));
    }
}
