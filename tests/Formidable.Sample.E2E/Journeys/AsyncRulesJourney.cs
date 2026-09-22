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

    // Records every open/close of the username field's "checking…" indicator as a [openedAt,
    // closedAt] pair, in wall-clock milliseconds, from the moment it is installed. A refresh's
    // own pending indicator is never suppressed — it flips on and off around whatever the
    // refresh validates, even a synchronous rule that resolves within the same instant — so a raw
    // open/close COUNT cannot tell a real second round trip apart from that harmless flash.
    // Timestamped pairs can: a real check spans roughly the simulated delay, the flash spans
    // essentially nothing.
    private const string InstallCheckWindowProbe = """
        () => {
            window.__checkWindows = [];
            let openedAt = null;
            const observer = new MutationObserver((mutations) => {
                for (const mutation of mutations) {
                    if (mutation.type !== "childList") continue;
                    const container = mutation.target;
                    if (!(container instanceof Element) || !container.classList.contains("field")) continue;
                    const username = document.querySelector("[id$='-username']");
                    if (username === null || !container.contains(username)) continue;
                    for (const node of mutation.addedNodes) {
                        if (node instanceof Element && node.tagName === "EM" && node.getAttribute("role") === "status") {
                            openedAt = performance.now();
                        }
                    }
                    for (const node of mutation.removedNodes) {
                        if (node instanceof Element && node.tagName === "EM" && node.getAttribute("role") === "status") {
                            const closedAt = performance.now();
                            window.__checkWindows.push([openedAt ?? closedAt, closedAt]);
                            openedAt = null;
                        }
                    }
                }
            });
            observer.observe(document.body, { childList: true, subtree: true });
            return true;
        }
        """;

    // Polls until the username field's indicator has closed and stayed closed for a beat,
    // rather than sleeping a fixed duration: "settled" means no indicator is on screen right
    // now AND at least 600 ms has passed since the last recorded close. A delayed reopen (the
    // double check returning) keeps this predicate false and the wait open, instead of racing a
    // guessed clock the way a fixed WaitForTimeoutAsync would.
    private const string SettledAfterLastClose = """
        () => {
            if (document.querySelectorAll("em[role='status']").length > 0) return false;
            const windows = window.__checkWindows;
            if (!windows || windows.length === 0) return false;
            return performance.now() - windows[windows.length - 1][1] >= 600;
        }
        """;

    // Property: after a submit, editing the username produces exactly one REAL checking window
    // (one lasting a healthy fraction of the simulated delay), not two. A window count alone
    // cannot establish this: FormidableEngine's RunPassAsync wraps every pass, including a
    // refresh that validates nothing async at all, in the same SetValidating(true)/(false) pair
    // — the refresh's own indicator is deliberately never suppressed — so even correct code
    // shows a second, instantaneous open/close here. Duration is what discriminates a genuine
    // round trip from that harmless flash; see InstallCheckWindowProbe below.
    //
    // What must break this test is reverting FormidableEngine's per-rule verdict store —
    // whole-profile refresh execution, every Submit rule re-run instead of only the rules still
    // owed an answer at the edit's stamp. That revert ALONE does not turn this red, though:
    // MemoizedHandleValidator's memo backstops it, since the refresh's redundant re-check is for
    // the value the live pass just answered, well inside the memo's window, so it resolves
    // instantly from the memo instead of re-running the delay. Only removing BOTH the store and
    // the memo together produces the symptom: two real, roughly 900 ms windows instead of one.
    // The engine-level mechanism (does the refresh execute only the rules without a fresh
    // verdict) is independently pinned by
    // FormidableEngineProfileSplitTests.A_post_submit_edit_runs_each_draft_rule_once, which
    // counts rule invocations directly and does not depend on the sample or its memo; this test
    // proves the end-to-end, user-visible contract instead, and is a weaker (but real) guard on
    // the engine mechanism specifically because the memo can and does cover for it here.
    [E2EFact]
    public async Task A_post_submit_edit_checks_once()
    {
        await using var session = await app.NewPageAsync("/async");
        var page = session.Page;

        // High enough that a genuine second round trip cannot be confused with the refresh's own
        // instantaneous indicator flash (see InstallCheckWindowProbe), yet low enough to keep the
        // test's own wait reasonable.
        const int delayMs = 900;
        await page.Locator("input[type=range]").FillAsync(delayMs.ToString());

        // An available username, checked and settled once, then submitted — the ordinary path to
        // a submitted form, not yet the edit under test.
        await TypeAsync(page.GetByLabel("Username", new() { Exact = true }), "ada");
        await Expect(page.Locator("em[role='status']")).ToHaveCountAsync(0, new() { Timeout = AsyncTimeoutMs });
        await page.GetByRole(AriaRole.Button, new() { Name = "Submit", Exact = true }).ClickAsync();
        await Expect(page.Locator("p[role='status']"))
            .ToHaveTextAsync("Submitted — username checks passed.", new() { Timeout = AsyncTimeoutMs });

        // Armed only after the form is submitted: everything above (the live pass on "ada", the
        // submit's own pass) is setup, not the edit under test.
        await page.EvaluateAsync(InstallCheckWindowProbe);

        await page.GetByLabel("Username", new() { Exact = true }).FillAsync("adam");

        // Waits out the edit's own live pass, the refresh's defer-and-recheck cycle, and — were
        // the double check to return — a second full round trip, by polling rather than
        // guessing a fixed duration (see SettledAfterLastClose).
        await page.WaitForFunctionAsync(
            SettledAfterLastClose,
            options: new PageWaitForFunctionOptions { Timeout = AsyncTimeoutMs });

        var windows = await page.EvaluateAsync<double[][]>("() => window.__checkWindows");

        // A window lasting at least half the simulated delay is a real round trip; anything
        // shorter is the refresh's own indicator, open and closed within the same instant because
        // what it validated needed no round trip at all.
        var realChecks = windows.Count(w => w[1] - w[0] >= delayMs / 2.0);
        Assert.True(
            realChecks == 1,
            $"expected exactly one real checking window, found {realChecks} of {windows.Length} " +
            $"total window(s): [{string.Join(", ", windows.Select(w => $"{w[1] - w[0]:F0}ms"))}]");
    }

    // Property: with LiveDebounce ticked, editing after a submit still produces exactly one REAL
    // checking window, not two — the refresh window (300 ms) is narrower than the debounce
    // window (400 ms), so the refresh comes due first and pays the single round trip, and the
    // debounced live pass that follows finds every rule it selects already answered at the
    // edit's stamp and publishes from the verdict store without a round trip of its own — the
    // same outcome A_post_submit_edit_checks_once pins above without LiveDebounce set at all,
    // reached in the opposite pass order.
    //
    // What must break this test is the same store revert A_post_submit_edit_checks_once names —
    // and here too the revert ALONE does not turn it red. The refresh behaves identically either
    // way in this staging (nothing is answered at the edit's stamp when it fires, so its full
    // selection IS the stale set); what the revert changes is the deferred live pass, which
    // stands down while the refresh is in flight (RunDebouncedLivePassAsync's RefreshInFlight
    // guard), proceeds once it clears, and then re-runs the uniqueness check instead of finding
    // it answered in the store. That redundant re-check is for the value the refresh just
    // answered and memoized, so it resolves instantly from MemoizedHandleValidator's memo and
    // the wall-clock cost stays flat — this test cannot see the regression. In correct code the
    // memo sits idle here (the store leaves the live pass nothing to execute); its designed job
    // on this page is the submit-side skip, a Submit pressed shortly after a live pass.
    // FormidableEngineProfileSplitTests.A_debounced_edit_runs_each_common_rule_once pins the
    // exact mutation directly, counting rule invocations on a validator with no memo to hide
    // behind; this test proves the weaker, end-to-end, user-visible claim that holds given the
    // memo the shipped sample actually has — the same relationship
    // A_post_submit_edit_checks_once has with its own engine-level pin.
    [E2EFact]
    public async Task A_debounced_post_submit_edit_checks_once()
    {
        await using var session = await app.NewPageAsync("/async");
        var page = session.Page;

        // High enough that a genuine second round trip cannot be confused with the refresh's own
        // instantaneous indicator flash (see InstallCheckWindowProbe), yet low enough to keep the
        // test's own wait reasonable.
        const int delayMs = 900;
        // Mirrors FormidableOptions.RefreshDebounce's default, which the page leaves unset — the
        // earliest timer that can start any pass after the edit while LiveDebounce holds the
        // live pass back; the lower-bound assert at the end reasons about when it can fire.
        const int refreshDebounceMs = 300;
        await page.Locator("input[type=range]").FillAsync(delayMs.ToString());
        await page.GetByLabel("Debounce live checks (batch fast typing into one pass)", new() { Exact = true })
            .CheckAsync();

        // An available username, checked through the debounced live path and settled once, then
        // submitted — the ordinary path to a submitted form, not yet the edit under test. Typing
        // finishes well inside the still-open 400 ms window, so nothing shows yet; waiting for the
        // indicator to open before waiting for it to close is what makes this a genuine
        // debounced-live-path check rather than a count(0) that was already true before the window
        // ever closed — without it, Submit lands mid-window and the submit pass itself, not the
        // live path, ends up being what answers "ada".
        await TypeAsync(page.GetByLabel("Username", new() { Exact = true }), "ada");
        await Expect(page.Locator("em[role='status']")).ToHaveCountAsync(1, new() { Timeout = AsyncTimeoutMs });
        await Expect(page.Locator("em[role='status']")).ToHaveCountAsync(0, new() { Timeout = AsyncTimeoutMs });
        await page.GetByRole(AriaRole.Button, new() { Name = "Submit", Exact = true }).ClickAsync();
        await Expect(page.Locator("p[role='status']"))
            .ToHaveTextAsync("Submitted — username checks passed.", new() { Timeout = AsyncTimeoutMs });

        // Armed only after the form is submitted: everything above (the debounced live pass on
        // "ada", the submit's own pass) is setup, not the edit under test.
        await page.EvaluateAsync(InstallCheckWindowProbe);

        // The page's own clock, read just before the edit commits — the probe stamps openedAt
        // with performance.now() too, so the lower-bound assert below compares like with like.
        var beforeFill = await page.EvaluateAsync<double>("() => performance.now()");
        await page.GetByLabel("Username", new() { Exact = true }).FillAsync("adam");

        // Waits out the refresh window and its round trip, the deferred live pass's landing
        // behind it, and — were the double check to return — a second full round trip, by polling
        // rather than guessing a fixed duration (see SettledAfterLastClose).
        await page.WaitForFunctionAsync(
            SettledAfterLastClose,
            options: new PageWaitForFunctionOptions { Timeout = AsyncTimeoutMs });

        var windows = await page.EvaluateAsync<double[][]>("() => window.__checkWindows");

        var realWindows = windows.Where(w => w[1] - w[0] >= delayMs / 2.0).ToArray();
        Assert.True(
            realWindows.Length == 1,
            $"expected exactly one real checking window, found {realWindows.Length} of {windows.Length} " +
            $"total window(s): [{string.Join(", ", windows.Select(w => $"{w[1] - w[0]:F0}ms"))}]");

        // The count alone does not discriminate this journey's own precondition: were LiveDebounce
        // not applied at all (the checkbox wiring inert), the edit's live pass would start
        // immediately and open its one real window within milliseconds of the fill — still a
        // count of one. The opening TIME is what trips on that mutation, and only as a lower
        // bound is it flake-safe: every measurement bias runs late (beforeFill is captured before
        // the fill commits, timers fire at or after their due time, the observer records the
        // indicator after the pass opens it), so correct code cannot come in under the bound. The
        // bound is the refresh window rather than the wider debounce window because it must hold
        // under either pass order: with LiveDebounce applied, no pass can start before the
        // narrower refresh timer comes due — whichever pass then pays, the first real window
        // opens no earlier than that.
        var firstRealOpensAfterMs = realWindows[0][0] - beforeFill;
        Assert.True(
            firstRealOpensAfterMs >= refreshDebounceMs,
            $"expected the first real checking window to open no earlier than {refreshDebounceMs} ms " +
            $"(the refresh window) after the fill, but it opened {firstRealOpensAfterMs:F0} ms after");
    }
}
