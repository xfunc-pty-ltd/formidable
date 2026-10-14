using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Formidable.Sample.E2E;

/// <summary>
/// What the focus service answers, driven at the script rather than through a form. The answer is
/// whether the element took focus, not whether one was found: the two agree on an ordinary input
/// and part company on a field the page holds but keeps out of reach, and that difference is the
/// only thing standing between such a field and the recovery a root has for it — its
/// <c>FocusFallback</c>, or the diagnostic that stands in when none is wired. The retry that
/// recovery makes carries a third argument, and it leaves focus alone when the visitor pressed a
/// key or a pointer button after the miss and focus sits on another element.
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

    // The call a focus move makes after its fallback answered true: the same two ids and a third
    // argument saying it is the retry.
    private const string RetryField =
        "async id => (await import('/_content/Formidable.Blazor/formidable.js'))" +
        ".focusField(id, id + '-messages', true)";

    // What a fallback does for the collapsed probe: makes it reachable, without moving focus.
    private const string OpenCollapsed = "() => { document.querySelector('#focus-probes details').open = true; }";

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

    /// <summary>
    /// A retry after the visitor pressed a key that moved focus answers <c>false</c> and leaves
    /// focus where the key put it, though the field is reachable by then.
    /// </summary>
    [E2EFact]
    public async Task A_retry_after_the_visitor_pressed_and_moved_focus_leaves_it_there()
    {
        await using var session = await app.NewPageAsync("/");
        var page = session.Page;
        await page.EvaluateAsync(BuildProbes);
        Assert.False(await page.EvaluateAsync<bool>(FocusField, "probe-collapsed"), "the first attempt misses");

        await page.Keyboard.PressAsync("Tab");
        await Expect(page.Locator("#probe-reachable")).ToBeFocusedAsync();
        await page.EvaluateAsync(OpenCollapsed);

        Assert.False(
            await page.EvaluateAsync<bool>(RetryField, "probe-collapsed"),
            "the visitor moved focus while the field was being made reachable");
        await Expect(page.Locator("#probe-reachable")).ToBeFocusedAsync();
    }

    /// <summary>
    /// Focus moved by a script with no press of the visitor's, as a fallback can move it, does not
    /// stop the retry: it lands on the field. A key event the page's own script dispatches is no
    /// press of the visitor's either.
    /// </summary>
    [E2EFact]
    public async Task A_retry_after_focus_moved_with_no_press_lands()
    {
        await using var session = await app.NewPageAsync("/");
        var page = session.Page;
        await page.EvaluateAsync(BuildProbes);
        Assert.False(await page.EvaluateAsync<bool>(FocusField, "probe-collapsed"), "the first attempt misses");

        await page.EvaluateAsync(
            "() => document.body.dispatchEvent(new KeyboardEvent('keydown', { key: 'Tab', bubbles: true }))");
        await page.EvaluateAsync("() => document.getElementById('probe-reachable').focus()");
        await page.EvaluateAsync(OpenCollapsed);

        Assert.True(await page.EvaluateAsync<bool>(RetryField, "probe-collapsed"), "no press moved focus");
        await Expect(page.Locator("#probe-collapsed")).ToBeFocusedAsync();
    }

    /// <summary>
    /// A press that leaves focus where it was at the miss does not stop the retry: the visitor has
    /// not chosen anywhere else.
    /// </summary>
    [E2EFact]
    public async Task A_retry_after_a_press_that_left_focus_in_place_lands()
    {
        await using var session = await app.NewPageAsync("/");
        var page = session.Page;
        await page.EvaluateAsync(BuildProbes);
        Assert.False(await page.EvaluateAsync<bool>(FocusField, "probe-collapsed"), "the first attempt misses");

        await page.Keyboard.PressAsync("Shift");
        await Expect(page.Locator("#probe-anchor")).ToBeFocusedAsync();
        await page.EvaluateAsync(OpenCollapsed);

        Assert.True(await page.EvaluateAsync<bool>(RetryField, "probe-collapsed"), "focus never left the anchor");
        await Expect(page.Locator("#probe-collapsed")).ToBeFocusedAsync();
    }

    /// <summary>
    /// A press that leaves focus on the page itself, rather than on an element, does not stop the
    /// retry either: the body says nothing about where the visitor wants to be.
    /// </summary>
    [E2EFact]
    public async Task A_retry_with_focus_on_the_body_lands()
    {
        await using var session = await app.NewPageAsync("/");
        var page = session.Page;
        await page.EvaluateAsync(BuildProbes);
        Assert.False(await page.EvaluateAsync<bool>(FocusField, "probe-collapsed"), "the first attempt misses");

        await page.Keyboard.PressAsync("Shift");
        await page.EvaluateAsync("() => document.activeElement.blur()");
        Assert.True(await page.EvaluateAsync<bool>("() => document.activeElement === document.body"));
        await page.EvaluateAsync(OpenCollapsed);

        Assert.True(await page.EvaluateAsync<bool>(RetryField, "probe-collapsed"), "focus sat on the body");
        await Expect(page.Locator("#probe-collapsed")).ToBeFocusedAsync();
    }

    /// <summary>
    /// An editable region outside the tab order still counts as a control: a click into one while
    /// the field is made reachable keeps focus there, and the retry answers <c>false</c>.
    /// </summary>
    [E2EFact]
    public async Task A_retry_after_the_visitor_clicked_into_an_editable_region_leaves_focus_there()
    {
        await using var session = await app.NewPageAsync("/");
        var page = session.Page;
        await page.EvaluateAsync(BuildProbes);
        await page.EvaluateAsync(@"() => document.getElementById('focus-probes')
            .insertAdjacentHTML('beforeend', `<div id='probe-editable' contenteditable>Notes</div>`)");
        Assert.False(await page.EvaluateAsync<bool>(FocusField, "probe-collapsed"), "the first attempt misses");

        await page.Locator("#probe-editable").ClickAsync();
        await Expect(page.Locator("#probe-editable")).ToBeFocusedAsync();
        await page.EvaluateAsync(OpenCollapsed);

        Assert.False(
            await page.EvaluateAsync<bool>(RetryField, "probe-collapsed"),
            "the visitor clicked into the editable region");
        await Expect(page.Locator("#probe-editable")).ToBeFocusedAsync();
    }

    // A host for a web component's own input, in an open shadow root. The page reports the host as
    // the focused element, and the host sits outside the tab order.
    private const string AddShadowInputs = @"() => {
        const host = document.createElement('div');
        host.id = 'probe-shadow';
        document.getElementById('focus-probes').append(host);
        host.attachShadow({ mode: 'open' }).innerHTML = `<input id='shadow-first'><input id='shadow-second'>`;
    }";

    private const string DeepFocusId = @"() => {
        let element = document.activeElement;
        while (element && element.shadowRoot && element.shadowRoot.activeElement) {
            element = element.shadowRoot.activeElement;
        }
        return element ? element.id : '';
    }";

    /// <summary>
    /// A control inside an open shadow root counts as the control it is, not as its host: a click into
    /// one while the field is made reachable keeps focus there, and the retry answers <c>false</c>.
    /// </summary>
    [E2EFact]
    public async Task A_retry_after_the_visitor_clicked_into_a_shadow_root_input_leaves_focus_there()
    {
        await using var session = await app.NewPageAsync("/");
        var page = session.Page;
        await page.EvaluateAsync(BuildProbes);
        await page.EvaluateAsync(AddShadowInputs);
        Assert.False(await page.EvaluateAsync<bool>(FocusField, "probe-collapsed"), "the first attempt misses");

        await page.Locator("#probe-shadow #shadow-first").ClickAsync();
        Assert.Equal("shadow-first", await page.EvaluateAsync<string>(DeepFocusId));
        await page.EvaluateAsync(OpenCollapsed);

        Assert.False(
            await page.EvaluateAsync<bool>(RetryField, "probe-collapsed"),
            "the visitor clicked into the shadow root's input");
        Assert.Equal("shadow-first", await page.EvaluateAsync<string>(DeepFocusId));
    }

    /// <summary>
    /// The element that held focus at the miss is read at the same depth: a visitor already in a
    /// shadow root's input who presses a key that moves nothing has not chosen anywhere else.
    /// </summary>
    [E2EFact]
    public async Task A_retry_after_a_press_that_left_focus_in_a_shadow_root_input_lands()
    {
        await using var session = await app.NewPageAsync("/");
        var page = session.Page;
        await page.EvaluateAsync(BuildProbes);
        await page.EvaluateAsync(AddShadowInputs);
        await page.Locator("#probe-shadow #shadow-first").ClickAsync();
        Assert.False(await page.EvaluateAsync<bool>(FocusField, "probe-collapsed"), "the first attempt misses");

        await page.Keyboard.PressAsync("Shift");
        Assert.Equal("shadow-first", await page.EvaluateAsync<string>(DeepFocusId));
        await page.EvaluateAsync(OpenCollapsed);

        Assert.True(await page.EvaluateAsync<bool>(RetryField, "probe-collapsed"), "focus never left the input");
        await Expect(page.Locator("#probe-collapsed")).ToBeFocusedAsync();
    }

    /// <summary>
    /// A retry reads the latest miss, not an earlier one: a miss nothing retried, a press, then a
    /// newer miss whose own record must catch the visitor's Tab that follows it.
    /// </summary>
    [E2EFact]
    public async Task A_retry_reads_the_latest_miss()
    {
        await using var session = await app.NewPageAsync("/");
        var page = session.Page;
        await page.EvaluateAsync(BuildProbes);
        Assert.False(await page.EvaluateAsync<bool>(FocusField, "probe-absent"), "the earlier miss");
        await page.Keyboard.PressAsync("Shift");
        Assert.False(await page.EvaluateAsync<bool>(FocusField, "probe-collapsed"), "the newer miss");

        await page.Keyboard.PressAsync("Tab");
        await Expect(page.Locator("#probe-reachable")).ToBeFocusedAsync();
        await page.EvaluateAsync(OpenCollapsed);

        Assert.False(
            await page.EvaluateAsync<bool>(RetryField, "probe-collapsed"),
            "the visitor moved focus after the newer miss");
        await Expect(page.Locator("#probe-reachable")).ToBeFocusedAsync();
    }
}
