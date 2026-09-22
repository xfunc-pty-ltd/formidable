using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static Formidable.Sample.E2E.SamplePage;
using static Microsoft.Playwright.Assertions;

namespace Formidable.Sample.E2E;

/// <summary>
/// <c>FieldState</c> read straight off the DOM: the state-table's booleans and the rendered
/// valid class agree, for a field touched by blur alone as much as for one a real edit modifies —
/// both fields on the page are Formidable inputs, so both take the same valid rule
/// (<c>FormidableCss.Compute</c>), the alignment the class provider shares with a native input
/// under the same EditContext. The valid class asks for more than touched-or-modified with no
/// message: the submit-selected coverage must be fresh and hold nothing against the field. Two
/// things put that coverage in the store on this page — the live pass a committed change runs,
/// and the <c>TrackFormValidity</c> probe. Neither is scoped to the field that moved: a live
/// pass plans over its whole selection, so an edit to Username files Display name's verdicts
/// too, and it is disclosure, not coverage, that narrows to the fields a visitor has engaged.
/// What sets the probe apart is that it needs no edit at all — the opening reading below is its
/// alone, since nothing has been edited yet; from the first commit onwards either may have got
/// there first. Green follows what the store can vouch for, never the interaction alone.
/// </summary>
[Collection("e2e")]
public sealed class FieldStateJourney(SampleAppFixture app)
{
    private static readonly Regex Valid = new(@"\bformidable-valid\b");

    [E2EFact]
    public async Task The_visualizer_tells_the_truth_while_a_real_user_types()
    {
        await using var session = await app.NewPageAsync("/field-state");
        var page = session.Page;
        var username = page.GetByLabel("Username", new() { Exact = true });
        var displayName = page.GetByLabel("Display name", new() { Exact = true });
        var usernameRow = page.Locator(".state-table tbody tr:has-text('Username')");
        var displayNameRow = page.Locator(".state-table tbody tr:has-text('Display name')");

        // Touched flips on blur alone — the page's MarkTouched splat (its whole lesson). Touched
        // is not green, though: nothing has been edited yet, so no live pass has run and the
        // tracking probe is the only thing that can have answered — which it has, for the
        // pristine model, and that fresh answer carries the required-Username failure. A field
        // the store knows would fail submit wears no formidable-valid, disclosed message or none.
        // The row's cells sit one per line in the markup, so the assertions below tolerate the
        // whitespace a normalized read leaves between them.
        await username.PressAsync("Tab");
        await Expect(usernameRow).ToHaveTextAsync(new Regex(@"^Username\s*True\s*False"));
        await Expect(username).Not.ToHaveClassAsync(Valid);

        // A real edit: Modified flips, and once the async taken-check lands the store holds a
        // fresh, clean submit answer for the field — that answer is what green waits for. The
        // timeout covers the check's simulated latency; the table's read and the class settle
        // on the same landing.
        await TypeAsync(username, "ada");
        await TabAsync(page);
        await Expect(usernameRow).ToHaveTextAsync(
            new Regex(@"^Username\s*True\s*True"), new() { Timeout = AsyncTimeoutMs });
        await Expect(username).ToHaveClassAsync(Valid, new() { Timeout = AsyncTimeoutMs });

        // Empty the field again and it does not merely lose its green, it says why: the live
        // channel evaluates whatever would block a submit, and the edit that emptied the box put
        // Username in the engaged set, so the submit bucket's own presence rule answers on the
        // spot. An error paints ungated, and the five state classes are exclusive, so
        // formidable-invalid on the box is also the statement that green has gone. Mutation that
        // must break this: restoring ValidationProfile.Draft as the live channel's default — the
        // required rule then never runs live, and an emptied box a visitor has just been typing
        // in reports nothing until a submit is blocked. Same property
        // FormidableEngineLiveDefaultTests.An_engaged_then_emptied_required_field_discloses_with_no_submit
        // pins at the engine level.
        await username.FillAsync("");
        await TabAsync(page);
        await Expect(MessagesFor(page, "username"))
            .ToHaveTextAsync(["Username is required"], new() { Timeout = AsyncTimeoutMs });
        await Expect(username).ToHaveClassAsync(
            new Regex(@"\bformidable-invalid\b"), new() { Timeout = AsyncTimeoutMs });

        // Display name never takes a keystroke here, only a blur — and touched alone DOES land
        // green on this field, because nothing in the submit-selected set fails for it: no
        // presence rule names it, and its own async rule is in the always-on bucket the submit
        // profile selects too, gated off on an empty value rather than absent. The pair is the
        // page's honest contrast — green paints the moment the store can vouch for a field and
        // is withheld exactly where it cannot, which is what makes Username's bare box at the
        // top of this journey a real absence rather than a page that never paints green at all.
        await displayName.PressAsync("Tab");
        await Expect(displayNameRow).ToHaveTextAsync(new Regex(@"^Display name\s*True\s*False"));
        await Expect(displayName).ToHaveClassAsync(Valid, new() { Timeout = AsyncTimeoutMs });
    }
}
