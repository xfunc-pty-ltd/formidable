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
/// message: the submit-selected coverage must be fresh and hold nothing against the field. This
/// page's <c>TrackFormValidity</c> probe is what keeps that coverage fresh between submits, so
/// green follows what the store can vouch for, not the interaction alone.
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
        // is not green, though: the tracking probe has already answered the Submit profile for
        // the pristine model, and that fresh answer carries the required-Username failure — a
        // field the store knows would fail submit wears no formidable-valid, disclosed message
        // or none. The row's cells sit one per line in the markup, so the assertions below
        // tolerate the whitespace a normalized read leaves between them.
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

        // Empty the field again: touched and modified both stand and no message appears — the
        // required rule belongs to Submit and nothing has disclosed it — yet green leaves all
        // the same, because the probe rides the same change cadence and the store's fresh
        // answer now names Username as failing. Mutation that must break this: a valid class
        // computed from touched/modified and current issues alone — the emptied box then keeps
        // its confirmation border while the store knows submit would fail. Same property
        // FormValidationEngineSubmitCoverageTests.A_touched_then_emptied_required_field_earns_no_valid_class_while_its_submit_rule_is_stale
        // pins at the engine level.
        await username.FillAsync("");
        await TabAsync(page);
        await Expect(username).Not.ToHaveClassAsync(Valid, new() { Timeout = AsyncTimeoutMs });
        await Expect(MessagesFor(page, "username")).ToHaveCountAsync(0);

        // Display name never takes a keystroke here, only a blur — and touched alone DOES land
        // green on this field, because the same fresh coverage holds nothing against it. The
        // pair is the page's honest contrast: valid paints the moment the store can vouch for a
        // field and is withheld exactly where it cannot, which is also what makes the Username
        // absences above discriminating rather than a page that never paints green at all.
        await displayName.PressAsync("Tab");
        await Expect(displayNameRow).ToHaveTextAsync(new Regex(@"^Display name\s*True\s*False"));
        await Expect(displayName).ToHaveClassAsync(Valid, new() { Timeout = AsyncTimeoutMs });
    }
}
