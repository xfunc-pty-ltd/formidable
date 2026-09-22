using Microsoft.Playwright;
using static Formidable.Sample.E2E.SamplePage;
using static Microsoft.Playwright.Assertions;

namespace Formidable.Sample.E2E;

/// <summary>
/// Keystroke-level input pins the bUnit suite cannot see. The first two pin the symptoms of a
/// stale-cascade regression: a non-fixed cascade could re-notify a kit input with a snapshot of
/// its parameters taken before the current keystroke, handing the input back the value it had
/// just replaced — one render after the fresh commit. The third pins the blur-time DOM value
/// sync: a number input displaying text it reports as empty. <see cref="SamplePage.Field"/>'s
/// <c>FillAsync</c>-based helpers never exercise any of this — <c>fill()</c> sets the whole
/// value in one shot and dispatches a single event, so every test here drives the keyboard
/// directly instead.
/// </summary>
[Collection("e2e")]
public sealed class InputRegressions(SampleAppFixture app)
{
    [E2EFact]
    public async Task Typing_into_the_middle_of_a_field_does_not_move_the_caret()
    {
        await using var session = await app.NewPageAsync("/normalize");
        var page = session.Page;
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
        await using var session = await app.NewPageAsync("/workout");
        var page = session.Page;
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

    [E2EFact]
    public async Task An_unparseable_number_entry_clears_on_tab_out()
    {
        await using var session = await app.NewPageAsync("/custom-profiles");
        var page = session.Page;
        var readMinutes = Field(page, "readminutes");

        // "e" is one of the few characters the browser itself admits into a number input
        // (scientific notation), but "e3" alone is not a number: the element DISPLAYS it while
        // reporting an empty value to every event, so no render-tree diff ever sees anything to
        // overwrite. validity.badInput is the one observable that sees the ghost — el.value
        // reads "" whether the box shows "e3" or nothing.
        await readMinutes.ClickAsync();
        await readMinutes.PressSequentiallyAsync("e3");
        Assert.True(await readMinutes.EvaluateAsync<bool>("el => el.validity.badInput"));

        await page.Keyboard.PressAsync("Tab");

        // The blur-time sync writes the model's value (null, so an empty string) straight into
        // the element: the ghost text is gone and the box agrees with the model.
        await page.WaitForFunctionAsync(
            "() => { const el = document.querySelector(\"[id$='-readminutes']\"); return el && !el.validity.badInput; }",
            options: new PageWaitForFunctionOptions { Timeout = AsyncTimeoutMs });
        await Expect(readMinutes).ToHaveValueAsync("");
    }
}
