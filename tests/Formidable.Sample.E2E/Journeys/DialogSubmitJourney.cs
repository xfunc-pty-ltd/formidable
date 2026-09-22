using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static Formidable.Sample.E2E.SamplePage;
using static Microsoft.Playwright.Assertions;

namespace Formidable.Sample.E2E;

/// <summary>
/// The dialog-first blocked submit in a real browser. Two of the claims this page makes are about
/// ORDER rather than about output, and neither is visible to a unit test: what the form does
/// immediately after the handler that opened the dialog, and whether the focus move waits for the
/// dialog to finish closing. Each is pinned here against its own opposite, so a pass means the
/// ordering held rather than that something plausible appeared.
/// </summary>
[Collection("e2e")]
public sealed class DialogSubmitJourney(SampleAppFixture app)
{
    private const string WaitToggle = "Wait for the dialog to finish closing before moving focus";
    private const string AutoFocusToggle = "Focus the first error automatically on a blocked submit";

    [E2EFact]
    public async Task A_dialog_announces_a_blocked_submit_and_a_name_in_it_lands_on_the_field()
    {
        await using var session = await app.NewPageAsync("/dialog-submit");
        var page = session.Page;
        var panel = page.Locator(".announcement__panel");
        var entries = page.Locator("button.formidable-summary__link");
        var submit = page.GetByRole(AriaRole.Button, new() { Name = "Submit", Exact = true });

        // Nothing is announced before a submit is blocked.
        await Expect(panel).ToBeHiddenAsync();

        // Step 1: the blocked submit opens the dialog and the dialog takes focus. With the
        // handler having suppressed the form's own move, nothing has gone into a field behind the
        // overlay — the toggled-on case below is what proves this assertion is discriminating.
        await submit.ClickAsync();
        await Expect(panel).ToBeVisibleAsync();
        await Expect(panel).ToBeFocusedAsync();
        await Expect(Field(page, "reference")).Not.ToBeFocusedAsync();

        // Step 2: six fields are wrong; the list names four of them, one entry per FIELD, and
        // counts the rest. Invoice reference fails two rules and is named once, which is what
        // separates a list of names from the list of messages the same issues would produce.
        await Expect(panel).ToContainTextAsync("6 fields need attention");
        await Expect(entries).ToHaveTextAsync(
            ["Invoice reference", "Supplier", "Supplier email", "Cost centre"]);
        await Expect(page.Locator(".formidable-summary__overflow")).ToHaveTextAsync("2 more to fix");

        // Step 3: clicking a name closes the dialog and lands on the field. The field keeps both
        // of its messages — the collapsing happened in the summary, not in the form.
        await SummaryEntry(page, "Invoice reference").ClickAsync();
        await Expect(panel).ToBeHiddenAsync();
        await Expect(Field(page, "reference")).ToBeFocusedAsync();
        await Expect(MessagesFor(page, "reference")).ToHaveCountAsync(2);
    }

    [E2EFact]
    public async Task Tab_off_either_end_of_the_open_dialog_comes_back_into_it()
    {
        await using var session = await app.NewPageAsync("/dialog-submit");
        var page = session.Page;
        var panel = page.Locator(".announcement__panel");
        var close = panel.GetByRole(AriaRole.Button, new() { Name = "Close", Exact = true });
        var submit = page.GetByRole(AriaRole.Button, new() { Name = "Submit", Exact = true });

        await submit.ClickAsync();
        await Expect(panel).ToBeVisibleAsync();
        await Expect(panel).ToBeFocusedAsync();

        // Backwards, from the panel itself, which is where focus sits the moment the dialog
        // opens: without a cycle this reaches the form behind the overlay, and the panel's
        // aria-modal="true" says the form is unreachable.
        await page.Keyboard.PressAsync("Shift+Tab");
        await Expect(close).ToBeFocusedAsync();

        // Forwards off the far end: Close is the last thing in the panel, so the next Tab has
        // nowhere left inside to go.
        await TabAsync(page);
        await Expect(SummaryEntry(page, "Invoice reference")).ToBeFocusedAsync();

        // Containment is a KEY handler, so it cannot and must not touch a programmatic move —
        // which is what the auto-focus and the summary's own click both make.
        await SummaryEntry(page, "Invoice reference").ClickAsync();
        await Expect(panel).ToBeHiddenAsync();
        await Expect(Field(page, "reference")).ToBeFocusedAsync();
    }

    /// <summary>
    /// The ways out of the dialog that name no field. Nothing in the kit moves focus for them —
    /// no entry was clicked — so what lands the visitor on a field is the page calling
    /// <c>FocusFirstErrorAsync()</c> once the dialog has gone, and this is the only browser test
    /// that exercises that call. Both routes are here because they run different code: the
    /// button's click handler and the panel's key handler. The assertion discriminates on the
    /// dialog's own hand-back: with the call absent, focus rests on the Submit button that
    /// opened the dialog, which is a different element from the one asserted here.
    /// </summary>
    [E2EFact]
    public async Task Closing_the_dialog_without_picking_a_name_lands_on_the_first_error()
    {
        await using var session = await app.NewPageAsync("/dialog-submit");
        var page = session.Page;
        var panel = page.Locator(".announcement__panel");
        var close = panel.GetByRole(AriaRole.Button, new() { Name = "Close", Exact = true });
        var submit = page.GetByRole(AriaRole.Button, new() { Name = "Submit", Exact = true });

        await submit.ClickAsync();
        await Expect(panel).ToBeVisibleAsync();
        await close.ClickAsync();
        await Expect(panel).ToBeHiddenAsync();
        await Expect(Field(page, "reference")).ToBeFocusedAsync();
        await Expect(submit).Not.ToBeFocusedAsync();

        // The same landing through the key rather than the button. The containment that keeps Tab
        // inside the panel is a Tab handler and has nothing to say about Escape, and the move
        // itself happens after the panel is gone, so neither can reach the other.
        await submit.ClickAsync();
        await Expect(panel).ToBeVisibleAsync();
        await Expect(panel).ToBeFocusedAsync();
        await page.Keyboard.PressAsync("Escape");
        await Expect(panel).ToBeHiddenAsync();
        await Expect(Field(page, "reference")).ToBeFocusedAsync();
    }

    [E2EFact]
    public async Task A_move_that_does_not_wait_for_the_dialog_is_taken_back_by_it()
    {
        await using var session = await app.NewPageAsync("/dialog-submit");
        var page = session.Page;
        var panel = page.Locator(".announcement__panel");
        var submit = page.GetByRole(AriaRole.Button, new() { Name = "Submit", Exact = true });

        await page.GetByLabel(WaitToggle, new() { Exact = true }).UncheckAsync();
        await submit.ClickAsync();
        await Expect(panel).ToBeVisibleAsync();

        // The callback starts the close and reports ready at once, so the summary's focus move
        // happens while the dialog is still on screen — and the dialog's own hand-back to the
        // button that opened it lands after it. The visitor ends on the button they pressed
        // rather than the field they chose.
        await SummaryEntry(page, "Supplier").ClickAsync();
        await Expect(panel).ToBeHiddenAsync();
        await Expect(submit).ToBeFocusedAsync();
        await Expect(Field(page, "suppliername")).Not.ToBeFocusedAsync();
    }

    [E2EFact]
    public async Task Leaving_the_auto_focus_on_puts_the_caret_behind_the_overlay()
    {
        await using var session = await app.NewPageAsync("/dialog-submit");
        var page = session.Page;
        var panel = page.Locator(".announcement__panel");
        var submit = page.GetByRole(AriaRole.Button, new() { Name = "Submit", Exact = true });

        await page.GetByLabel(AutoFocusToggle, new() { Exact = true }).CheckAsync();
        await submit.ClickAsync();

        // The form's own move runs immediately after the handler that opened the dialog, inside
        // the same submit call, so the dialog is up and the caret is in the first error's box
        // underneath it. That is the whole reason the handler has to suppress it.
        await Expect(panel).ToBeVisibleAsync();
        await Expect(Field(page, "reference")).ToBeFocusedAsync();
    }

    [E2EFact]
    public async Task A_form_with_nothing_left_to_announce_submits_without_a_dialog()
    {
        await using var session = await app.NewPageAsync("/dialog-submit");
        var page = session.Page;
        var panel = page.Locator(".announcement__panel");
        var submit = page.GetByRole(AriaRole.Button, new() { Name = "Submit", Exact = true });

        await TypeAsync(Field(page, "reference"), "INV-2031");
        await TypeAsync(Field(page, "suppliername"), "Ada Systems");
        await TypeAsync(Field(page, "supplieremail"), "billing@ada.example");
        await TypeAsync(Field(page, "costcentre"), "CC-14");
        await TypeAsync(Field(page, "amount"), "1250");
        await TypeAsync(Field(page, "description"), "Quarterly maintenance");

        // Tab out of the last field before pressing anything: a fill followed straight by a click
        // is this suite's known source of lost first clicks.
        await TabAsync(page);

        await submit.ClickAsync();
        await Expect(page.Locator("p[role='status']")).ToHaveTextAsync(new Regex("on its way"));
        await Expect(panel).ToBeHiddenAsync();
    }
}
