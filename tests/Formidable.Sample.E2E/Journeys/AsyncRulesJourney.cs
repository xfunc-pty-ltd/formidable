using Microsoft.Playwright;
using static Formidable.Sample.E2E.SamplePage;
using static Microsoft.Playwright.Assertions;

namespace Formidable.Sample.E2E;

/// <summary>
/// A username uniqueness check that runs per keystroke, honours cancellation, and keeps its
/// pending state scoped to the field being edited — proven in the browser with the delay pinned
/// low first, so the transient "checking…" window is short but still real.
/// </summary>
[Collection("e2e")]
public sealed class AsyncRulesJourney(SampleAppFixture app)
{
    // The pending window has to be caught while it is open, and pinning the delay low (see the
    // test) shortens that window, so the wait is armed before the triggering keystrokes rather
    // than raced against afterward — the same idiom WorkoutFocusAndAsync uses for its own
    // field-scoped async check.
    private const string PendingScopedToUsername = """
        () => {
            const username = document.querySelector("[id$='-username']");
            const indicators = document.querySelectorAll("em[role='status']");
            const pending = document.querySelectorAll(".formidable-pending");
            return username !== null
                && indicators.length === 1
                && indicators[0].closest(".field")?.contains(username) === true
                && pending.length === 1
                && pending[0] === username;
        }
        """;

    [E2EFact]
    public async Task Pending_scopes_to_the_typed_field_and_the_verdict_lands()
    {
        await using var session = await app.NewPageAsync("/async");
        var page = session.Page;

        // Determinism and speed: pin the simulated delay low before typing anything.
        await page.Locator("input[type=range]").FillAsync("100");

        // Real keystrokes: the pending indicator is field-scoped — it lights on the username,
        // and on nothing else, while the check is in flight. Every keystroke restarts the check,
        // so the wait is armed first and can resolve on any one of them.
        var pendingScoped = page.WaitForFunctionAsync(
            PendingScopedToUsername,
            options: new PageWaitForFunctionOptions { Timeout = AsyncTimeoutMs });
        await TypeAsync(page.GetByLabel("Username", new() { Exact = true }), "admin");
        await pendingScoped;

        await Expect(MessagesFor(page, "username"))
            .ToHaveTextAsync(["That username is taken"], new() { Timeout = AsyncTimeoutMs });

        // An available name clears the verdict through the same live path. Clearing the field
        // alone would already clear the stale "taken" message (the When guard skips empty), so
        // retyping proves nothing on its own unless the SECOND check is actually observed: the
        // same pending-scoped wait is armed again, a real Tab blurs the field once it resolves,
        // and the assertion waits for the indicator to close before reading the message list —
        // a regression that made "ada" wrongly taken would show up here instead of being missed
        // behind a still-in-flight check.
        await page.GetByLabel("Username", new() { Exact = true }).FillAsync("");
        var pendingScopedAgain = page.WaitForFunctionAsync(
            PendingScopedToUsername,
            options: new PageWaitForFunctionOptions { Timeout = AsyncTimeoutMs });
        await TypeAsync(page.GetByLabel("Username", new() { Exact = true }), "ada");
        await pendingScopedAgain;
        await TabAsync(page);

        await Expect(page.Locator("em[role='status']")).ToHaveCountAsync(0, new() { Timeout = AsyncTimeoutMs });
        await Expect(MessagesFor(page, "username")).ToHaveCountAsync(0, new() { Timeout = AsyncTimeoutMs });
    }
}
