using Microsoft.Playwright;
using static Formidable.Sample.E2E.SamplePage;
using static Microsoft.Playwright.Assertions;

namespace Formidable.Sample.E2E;

/// <summary>
/// A lock bound to the form's submit flag, on a server circuit: the Blazor Web App fixture's page
/// whose fieldset and status line follow the flag, behind a rule that waits a second and a save
/// that waits half a second more. The lock lands after the click and lifts once the save returns.
/// A blocked submit lifts it before focus moves, so focus reaches the first error. An unlocked form
/// proceeds the same way. A keyboard submit whose lock takes focus from the field starts no check
/// of its own. Each case reads how often the slow rule ran: once, the submit's own check.
/// </summary>
[Collection("e2e")]
public sealed class SubmittingLockJourney(SampleAppFixture app)
{
    private static ILocator Submitting(IPage page) => page.Locator("#submitting");

    private static ILocator Saving(IPage page) => page.Locator("#saving");

    private static ILocator SubmitButton(IPage page) =>
        page.GetByRole(AriaRole.Button, new() { Name = "Submit", Exact = true });

    private async Task<IPage> OpenAsync(SampleSession session, string query)
    {
        var page = session.Page;
        await page.GotoAsync(app.WebAppOrigin + "/submitting-lock" + query);

        // Prerendering is off, so the button appearing is the circuit connecting.
        await Expect(SubmitButton(page)).ToHaveCountAsync(1, new() { Timeout = AsyncTimeoutMs });
        return page;
    }

    /// <summary>Waits until <paramref name="saving"/> shows and, at that same moment, the lock stands as <paramref name="locked"/> says.</summary>
    private static Task WhileSavingAsync(IPage page, string saving, bool locked) =>
        page.WaitForFunctionAsync(
            "([text, locked]) => document.querySelector('#saving').textContent === text"
            + " && document.querySelector('#lock').disabled === locked",
            new object[] { saving, locked },
            new() { Timeout = AsyncTimeoutMs });

    // The fieldset carries the lock, and Playwright reads disabled on native controls alone, so
    // the controls inside it are what each case asks about.
    private static async Task ExpectLockedAsync(IPage page, bool locked)
    {
        foreach (var control in new[] { SubmitButton(page), Field(page, "name") })
        {
            await (locked ? Expect(control).ToBeDisabledAsync() : Expect(control).ToBeEnabledAsync());
        }
    }

    private static async Task ExpectRunsAsync(IPage page, string runs)
    {
        await page.Locator("#show-runs").ClickAsync();
        await Expect(page.Locator("#runs")).ToHaveTextAsync(runs);
    }

    [E2EFact]
    public async Task The_lock_lands_after_the_click_and_lifts_once_the_save_returns()
    {
        await using var session = await app.NewSessionAsync();
        var page = await OpenAsync(session, "?name=Ada");

        await SubmitButton(page).ClickAsync();
        await ExpectLockedAsync(page, locked: true);
        await Expect(Submitting(page)).ToHaveTextAsync("Submitting…");

        // The check has answered and the save is under way: the lock still stands.
        await WhileSavingAsync(page, "Saving Ada", locked: true);

        await Expect(Saving(page)).ToHaveTextAsync("Saved Ada", new() { Timeout = AsyncTimeoutMs });
        await ExpectLockedAsync(page, locked: false);
        await Expect(Submitting(page)).ToBeEmptyAsync();
        await ExpectRunsAsync(page, "1");
    }

    // The lock disables the field the first error is on. Held through the focus move, the move
    // would find a disabled control, which cannot take focus, and focus would stay on the page.
    [E2EFact]
    public async Task A_blocked_submit_lifts_the_lock_before_focus_moves_to_the_first_error()
    {
        await using var session = await app.NewSessionAsync();
        var page = await OpenAsync(session, string.Empty);
        var name = Field(page, "name");

        await SubmitButton(page).ClickAsync();
        await ExpectLockedAsync(page, locked: true);

        await Expect(MessagesFor(page, "name")).ToHaveTextAsync(["Name is required"], new() { Timeout = AsyncTimeoutMs });
        await Expect(name).ToBeFocusedAsync();
        await ExpectLockedAsync(page, locked: false);
        await Expect(Submitting(page)).ToBeEmptyAsync();
        await ExpectRunsAsync(page, "1");
    }

    [E2EFact]
    public async Task An_unlocked_form_proceeds_with_nothing_locked()
    {
        await using var session = await app.NewSessionAsync();
        var page = await OpenAsync(session, "?lock=off&name=Ada");

        await SubmitButton(page).ClickAsync();
        await WhileSavingAsync(page, "Saving Ada", locked: false);
        await Expect(Submitting(page)).ToBeEmptyAsync();

        await Expect(Saving(page)).ToHaveTextAsync("Saved Ada", new() { Timeout = AsyncTimeoutMs });
        await ExpectRunsAsync(page, "1");
    }

    // Enter in the field submits, and the lock takes focus from the field as it lands. Leaving a
    // field without changing it checks nothing, so the slow rule runs once, for the submit. A check
    // the leaving had started would count as it began, before the save ended, so the count is final
    // once "Saved Ada" shows; the fixed wait is only a margin. Focus is left on the page, since a
    // passing submit moves none.
    [E2EFact]
    public async Task A_keyboard_submit_under_the_lock_starts_no_check_as_the_field_loses_focus()
    {
        await using var session = await app.NewSessionAsync();
        var page = await OpenAsync(session, "?name=Ada");
        var name = Field(page, "name");

        await name.FocusAsync();
        await page.Keyboard.PressAsync("Enter");
        await ExpectLockedAsync(page, locked: true);
        await Expect(name).Not.ToBeFocusedAsync();

        await Expect(Saving(page)).ToHaveTextAsync("Saved Ada", new() { Timeout = AsyncTimeoutMs });
        await page.WaitForTimeoutAsync(1500);
        Assert.True(await page.EvaluateAsync<bool>("() => document.activeElement === document.body"));
        await ExpectRunsAsync(page, "1");
    }
}
