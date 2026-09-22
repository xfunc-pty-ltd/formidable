using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Formidable.Sample.E2E;

/// <summary>
/// What the focus service answers, driven at the script rather than through a form. The answer is
/// whether the element took focus, not whether one was found: the two agree on an ordinary input
/// and part company on a field the page holds but keeps out of reach, and that difference is the
/// only thing standing between such a field and the recovery a root has for it — its
/// <c>FocusFallback</c>, or the diagnostic that stands in when none is wired.
/// </summary>
/// <remarks>
/// The contract belongs to the script, so it is pinned there: no sample page has to grow a field
/// it cannot focus for the shape to be observable, and nothing here depends on which fields a
/// page happens to render. The probes are built outside <c>#app</c>, so none of them is Blazor's
/// to diff, and the module is reached by the same import the page itself used — a second import
/// of one URL is the same instance. The scroll id passed alongside each target is the
/// <c>-messages</c> id the real caller always passes; nothing carries it here, which is the
/// ordinary case of a field whose message list has nothing to render.
/// </remarks>
[Collection("e2e")]
public sealed class FocusServiceContractJourney(SampleAppFixture app)
{
    private const string BuildProbes = @"() => {
        document.body.insertAdjacentHTML('afterbegin',
            `<div id='focus-probes'>
                <button type='button' id='probe-anchor'>Anchor</button>
                <input id='probe-reachable'>
                <input id='probe-disabled' disabled>
                <details><summary>Details</summary><input id='probe-collapsed'></details>
             </div>`);
        document.getElementById('probe-anchor').focus();
    }";

    private const string FocusField =
        "async id => (await import('/_content/Formidable.Blazor/formidable.js'))" +
        ".focusField(id, id + '-messages')";

    /// <summary>
    /// Three shapes, one question. A reachable input answers <c>true</c> and takes focus; an id
    /// nothing carries answers <c>false</c> without ever reaching the focus call; and the shape
    /// between them — an element the page holds that will not take focus — answers <c>false</c>
    /// too, which is what makes it a miss the caller can recover rather than a move it believes
    /// happened. Both refusing shapes leave focus exactly where the visitor left it, which is the
    /// cost the answer exists to report.
    /// </summary>
    [E2EFact]
    public async Task Focus_reports_whether_the_element_took_focus_not_whether_it_was_found()
    {
        await using var session = await app.NewPageAsync("/");
        var page = session.Page;
        await page.EvaluateAsync(BuildProbes);

        Assert.True(
            await page.EvaluateAsync<bool>(FocusField, "probe-reachable"),
            "a reachable input takes focus");
        await Expect(page.Locator("#probe-reachable")).ToBeFocusedAsync();

        // Back to the anchor before each refusing shape, so "focus did not move" is a claim about
        // this call rather than about the one before it.
        await page.EvaluateAsync("() => document.getElementById('probe-anchor').focus()");
        Assert.False(
            await page.EvaluateAsync<bool>(FocusField, "probe-disabled"),
            "a disabled input is on the page and still refuses focus");
        await Expect(page.Locator("#probe-anchor")).ToBeFocusedAsync();

        await page.EvaluateAsync("() => document.getElementById('probe-anchor').focus()");
        Assert.False(
            await page.EvaluateAsync<bool>(FocusField, "probe-collapsed"),
            "an input inside a closed details is in the DOM and still refuses focus");
        await Expect(page.Locator("#probe-anchor")).ToBeFocusedAsync();

        await page.EvaluateAsync("() => document.getElementById('probe-anchor').focus()");
        Assert.False(
            await page.EvaluateAsync<bool>(FocusField, "probe-absent"),
            "no element carries that id at all");
        await Expect(page.Locator("#probe-anchor")).ToBeFocusedAsync();
    }

    /// <summary>
    /// The shape that looks like the others and is not: an input under a full-screen overlay is
    /// unreachable to the pointer and perfectly focusable to the keyboard, so the move lands and
    /// there is no miss for a fallback to recover. It is why a page that has to clear something
    /// out of the way reaches for <c>PrepareFocus</c>, which runs before the attempt, rather than
    /// the fallback, which only ever runs after one has already failed.
    /// </summary>
    [E2EFact]
    public async Task An_element_under_an_overlay_still_takes_focus()
    {
        await using var session = await app.NewPageAsync("/");
        var page = session.Page;
        await page.EvaluateAsync(BuildProbes);
        await page.EvaluateAsync(@"() => document.body.insertAdjacentHTML('afterbegin',
            `<div id='probe-overlay' style='position:fixed;inset:0;background:#000;z-index:9999'></div>`)");

        Assert.True(
            await page.EvaluateAsync<bool>(FocusField, "probe-reachable"),
            "an overlay covers a field without taking its focusability away");
        await Expect(page.Locator("#probe-reachable")).ToBeFocusedAsync();
    }
}
