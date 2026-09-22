using Microsoft.Playwright;
using static Formidable.Sample.E2E.SamplePage;
using static Microsoft.Playwright.Assertions;

namespace Formidable.Sample.E2E;

/// <summary>
/// Pins the two symptoms of a stale-cascade regression: a non-fixed cascade could re-notify a
/// kit input with a snapshot of its parameters taken before the current keystroke, handing the
/// input back the value it had just replaced — one render after the fresh commit.
/// <see cref="SamplePage.Field"/>'s <c>FillAsync</c>-based helpers never exercise this —
/// <c>fill()</c> sets the whole value in one shot and dispatches a single event, so both tests
/// here drive the keyboard directly instead.
/// </summary>
[Collection("e2e")]
public sealed class InputRegressions(SampleAppFixture app)
{
    [E2EFact]
    public async Task Typing_into_the_middle_of_a_field_does_not_move_the_caret()
    {
        var page = await app.NewPageAsync("/normalize");
        var title = Field(page, "title");

        await title.ClickAsync();
        await title.PressSequentiallyAsync("Meeting");
        await Expect(title).ToHaveValueAsync("Meeting");

        // Caret at the start, then one character typed there — a stale re-supply hands the input
        // back its own pre-keystroke value one render after it committed the fresh one, and a
        // browser applies that write by moving the caret to the end.
        await page.Keyboard.PressAsync("Home");
        await page.Keyboard.TypeAsync(" ");
        await Expect(title).ToHaveValueAsync(" Meeting");

        // Every write a stale re-supply produces lands within ~1 ms of the keystroke, so a
        // value-based wait alone could win the race before the stale write lands and pass under
        // the bug — settle first and then read the caret.
        await page.WaitForTimeoutAsync(250);
        Assert.Equal(1, await title.EvaluateAsync<int>("el => el.selectionStart"));
    }

    [E2EFact]
    public async Task A_date_can_be_typed_segment_by_segment()
    {
        var page = await app.NewPageAsync("/workout");
        var eventDate = Field(page, "eventdate");

        // A date input's segment order (day-first, month-first, ...) follows the OS/browser
        // locale, and Playwright's context Locale does not reliably control it (verified: it
        // left the native widget's own segment order unchanged on this machine). Rather than pin
        // a locale the test cannot actually guarantee, every non-year segment types the SAME two
        // digits ("05"), so the final value is identical regardless of which segment is day and
        // which is month — order-agnostic by construction, not by assumption.
        await eventDate.ClickAsync();
        foreach (var key in new[] { "0", "5", "0", "5", "2", "0", "2", "6" })
        {
            await page.Keyboard.PressAsync(key);
        }

        // Under the regression, the stale re-supply's per-segment "" write reset the editor's
        // segment state, so a later segment (the year) could no longer be typed at all — the
        // final value stuck at "". Fixed, every segment survives to the complete date.
        await Expect(eventDate).ToHaveValueAsync("2026-05-05");
    }
}
