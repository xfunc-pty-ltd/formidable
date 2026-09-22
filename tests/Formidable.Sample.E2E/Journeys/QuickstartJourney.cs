using Microsoft.Playwright;
using static Formidable.Sample.E2E.SamplePage;
using static Microsoft.Playwright.Assertions;

namespace Formidable.Sample.E2E;

/// <summary>
/// The front-door cycle every other page builds on: an empty submit is blocked and discloses,
/// the summary and the field messages agree, fixing the fields by real typing clears them, and
/// the resubmit goes through. It is also where the displaced-click guard is pinned, because this
/// is the smallest page that reproduces the shape: one uncommitted edit, and pressing the button
/// takes the button out from under the pointer before the release.
/// </summary>
[Collection("e2e")]
public sealed class QuickstartJourney(SampleAppFixture app)
{
    [E2EFact]
    public async Task Blocked_submit_discloses_then_a_fixed_form_goes_through()
    {
        await using var session = await app.NewPageAsync("/");
        var page = session.Page;

        await page.GetByRole(AriaRole.Button, new() { Name = "Submit", Exact = true }).ClickAsync();

        await Expect(Summary(page)).ToContainTextAsync("Name is required");
        await Expect(MessagesFor(page, "name")).ToHaveTextAsync(["Name is required"]);

        // The fully real-typed path: keystrokes and a real blur, per the typing policy.
        await TypeAsync(Field(page, "name"), "Ada Lovelace");
        await TabAsync(page);
        await Expect(MessagesFor(page, "name")).ToHaveCountAsync(0);

        await TypeAsync(Field(page, "email"), "ada@example.test");
        await TabAsync(page);

        await page.GetByRole(AriaRole.Button, new() { Name = "Submit", Exact = true }).ClickAsync();
        await Expect(Summary(page)).ToHaveCountAsync(0);
        await Expect(page.Locator("p[role='status']")).ToHaveTextAsync("Submitted — thanks, Ada Lovelace!");
    }

    // The displaced-click guard, at the smallest page that reproduces the shape. Pressing Submit
    // with an edit still uncommitted blurs the field, and that blur commits, engages, discloses
    // and inserts a message ABOVE the button — so the button leaves the pointer between down and
    // up, and the browser dispatches the click on the nearest common ancestor instead of on the
    // button. ClickAsync cannot see any of that: it resolves a target once and presses and
    // releases as one command.
    [E2EFact]
    public async Task A_pressed_submit_survives_the_message_the_blur_inserts_above_the_button()
    {
        await using var session = await app.NewPageAsync("/");
        var page = session.Page;

        // Email is the last field, so the message its commit discloses sits between it and the
        // button. Left uncommitted: the press itself is what blurs it.
        await TypeAsync(Field(page, "email"), "not-an-email");

        await PressHoldReleaseAsync(page, SubmitButton(page), 120);

        // Name is untouched, so the live channel has nothing to say about it and never will:
        // its message can only come from a submit, which is what makes it the discriminator.
        await Expect(MessagesFor(page, "name")).ToHaveTextAsync(["Name is required"]);
    }

    // The same displacement under touch, which the browser routes through the identical
    // cancel-on-different-target rule. Keyboard activation is unaffected either way — focus
    // follows the element rather than a coordinate — so touch and pointer are the whole gap.
    [E2EFact]
    public async Task A_tapped_submit_survives_the_message_the_blur_inserts_above_the_button()
    {
        await using var session = await app.NewPageAsync("/", new BrowserNewContextOptions { HasTouch = true });
        var page = session.Page;

        await TypeAsync(Field(page, "email"), "not-an-email");

        await SubmitButton(page).TapAsync();

        await Expect(MessagesFor(page, "name")).ToHaveTextAsync(["Name is required"]);
    }

    // The condition that keeps the guard from being a second activation path: the pointer stayed
    // where it was pressed. Dragging off a button to cancel it produces exactly the same
    // retargeted click the displacement does, and the browser cannot tell them apart because its
    // own rule is written in terms of targets.
    [E2EFact]
    public async Task A_press_dragged_off_the_button_still_cancels()
    {
        await using var session = await app.NewPageAsync("/");
        var page = session.Page;

        await TypeAsync(Field(page, "email"), "not-an-email");

        // The same displacement the pressed-submit pin above recovers: the blur below still
        // discloses the email message and still moves the button. All that differs is where the
        // pointer ends up.
        var (x, y) = await CentreOfAsync(SubmitButton(page));
        await page.Mouse.MoveAsync(x, y);
        await page.Mouse.DownAsync();
        await page.WaitForTimeoutAsync(120);
        await page.Mouse.MoveAsync(x + 180, y + 110);
        await page.Mouse.UpAsync();

        // The two absences below pass on their first evaluation, so left alone they would both be
        // answered within a round trip of the release, and a submit that did survive would have to
        // render inside that window to be caught. The wait is what makes the window generous
        // instead of incidental — the same settle its covered-button sibling takes for the same
        // reason. Only the email message ahead of them retries on its own.
        await page.WaitForTimeoutAsync(300);

        // The email message proves the press did commit the field, so the button did move: this
        // is the recoverable shape with the pointer moved off it, not a gesture that never began.
        await Expect(MessagesFor(page, "email")).ToHaveTextAsync(["A valid email is required"]);
        await Expect(MessagesFor(page, "name")).ToHaveCountAsync(0);
        await Expect(Summary(page)).Not.ToContainTextAsync("Name is required");
    }

    // Nothing about an ordinary click changes: the browser delivered it to the button itself, so
    // there is nothing displaced to recover and the guard must not add a second activation on top.
    // The two-pixel insertion is what makes this discriminating rather than merely reassuring. It
    // puts the button in motion under a still pointer, so every condition the guard weighs holds
    // except the one that decides the case here — the click was never retargeted, because two
    // pixels is far too small for the release to have landed anywhere but on the button. Settled,
    // the button would not move at all, and the pin would pass whether the guard read the retarget
    // or not.
    [E2EFact]
    public async Task Two_ordinary_clicks_on_a_settled_page_submit_exactly_twice()
    {
        await using var session = await app.NewPageAsync("/");
        var page = session.Page;

        await TypeAsync(Field(page, "name"), "Ada Lovelace");
        await TabAsync(page);
        await TypeAsync(Field(page, "email"), "ada@example.test");
        await TabAsync(page);
        await Expect(page.Locator(".formidable-message")).ToHaveCountAsync(0);

        // Prepended to the body rather than into the form: everything under #app is Blazor's to
        // diff, and a foreign node inside it is not this pin's business to introduce.
        await page.EvaluateAsync(@"() => document.addEventListener('pointerdown', () => {
            const spacer = document.createElement('div');
            spacer.className = 'press-nudge';
            spacer.style.cssText = 'height:2px';
            document.body.prepend(spacer);
        }, true)");

        await CountSubmitsAsync(page);
        await SubmitButton(page).ClickAsync();
        await Expect(page.Locator("p[role='status']")).ToHaveTextAsync("Submitted — thanks, Ada Lovelace!");
        await SubmitButton(page).ClickAsync();

        // One nudge per press, so the movement the guard would read really did happen both times.
        await Expect(page.Locator(".press-nudge")).ToHaveCountAsync(2);
        Assert.Equal(2, await SubmitCountAsync(page));
    }

    // The least obvious of the three conditions, and the one that keeps the guard from being too
    // clever: a click can be retargeted because something opened OVER the button rather than
    // because the button left. Intercepting a click is exactly what an element opened over one is
    // for, so a button that never moved is never resurrected.
    [E2EFact]
    public async Task A_button_covered_rather_than_moved_is_never_resurrected()
    {
        await using var session = await app.NewPageAsync("/");
        var page = session.Page;

        await TypeAsync(Field(page, "name"), "Ada Lovelace");
        await TabAsync(page);
        await TypeAsync(Field(page, "email"), "ada@example.test");
        await TabAsync(page);

        await CountSubmitsAsync(page);

        // Fixed, so it covers the button without moving anything: the release lands on the overlay
        // and the browser dispatches the click on the common ancestor, exactly as a displacement
        // would — but the button's own border box is untouched.
        await page.EvaluateAsync(@"() => document.addEventListener('pointerdown', () => {
            const overlay = document.createElement('div');
            overlay.style.cssText = 'position:fixed;inset:0;z-index:99999';
            document.body.appendChild(overlay);
        }, true)");

        await PressHoldReleaseAsync(page, SubmitButton(page), 120);
        await page.WaitForTimeoutAsync(300);

        Assert.Equal(0, await SubmitCountAsync(page));
        await Expect(page.Locator("p[role='status']")).ToHaveCountAsync(0);
    }

    // The other direction, and the reason the guard's listeners sit on the document rather than on
    // the form: fixing the last error takes a message and the summary OFF the page, the button
    // rises past the pointer, and the click is dispatched on <main> — outside the form entirely,
    // where a form-scoped listener would never see it.
    [E2EFact]
    public async Task A_submit_survives_the_summary_leaving_from_above_the_button()
    {
        await using var session = await app.NewPageAsync("/");
        var page = session.Page;

        await SubmitButton(page).ClickAsync();
        await Expect(Summary(page)).ToContainTextAsync("Name is required");

        await TypeAsync(Field(page, "name"), "Ada Lovelace");
        await TabAsync(page);

        // Left uncommitted on purpose: the press below is what commits it, and committing it is
        // what empties the summary and lifts everything under it.
        await TypeAsync(Field(page, "email"), "ada@example.test");

        // Long enough for the message and summary slide-outs to settle before the release, so the
        // button has finished moving rather than being mid-transition.
        await PressHoldReleaseAsync(page, SubmitButton(page), 700);

        await Expect(page.Locator("p[role='status']")).ToHaveTextAsync("Submitted — thanks, Ada Lovelace!");
    }

    // Attach mode's first route takes whatever element carries the model-level gate id, and that
    // id is a consumer's to place: put it on a summary wrapper and it names something narrower
    // than the form, with no button under it anywhere. Accepting that would install a guard that
    // can never fire and say nothing, which is precisely how a swallowed click went undiagnosed
    // for as long as it did. So an element with no button falls through to the wider <form> the
    // second route finds, and the recovery lands.
    [E2EFact]
    public async Task A_gate_id_on_a_buttonless_element_falls_through_to_the_form()
    {
        await using var session = await app.NewPageAsync("/");
        var page = session.Page;

        // Built outside #app, so none of it is Blazor's to diff: a form holding a field to walk up
        // from, a button, and — standing in for a misplaced gate id — a wrapper with neither. The
        // press grows the wrapper, which both displaces the button and puts what the release lands
        // on inside the form, so the click retargets to the form exactly as the real defect does.
        await page.EvaluateAsync(@"() => {
            document.body.insertAdjacentHTML('afterbegin',
                `<form id='probe-form'>
                    <div id='probe-gate'></div>
                    <input id='probe-field'>
                    <button type='button' id='probe-button'>Go</button>
                 </form>`);

            window.__probeClicks = 0;
            const button = document.getElementById('probe-button');
            button.addEventListener('click', () => window.__probeClicks++);
            button.addEventListener('pointerdown', () => {
                document.getElementById('probe-gate').style.height = '120px';
            });
        }");

        // The same module the page itself imported: a second import of one URL is the same
        // instance, so this registers into the live registry the guard is already reading.
        var registered = await page.EvaluateAsync<bool>(
            "async () => (await import('/_content/Formidable.Blazor/formidable.js'))" +
            ".registerClickRecovery('probe-gate', ['probe-field'])");
        Assert.True(registered);

        await PressHoldReleaseAsync(page, page.Locator("#probe-button"), 120);

        // The browser delivered nothing to the button itself — it retargeted the click to the
        // form — so this counts recoveries and nothing else.
        Assert.Equal(1, await page.EvaluateAsync<int>("() => window.__probeClicks"));
    }

    // A root is asked for its guard once, on the first interactive render for its context, and a
    // form whose content sits behind a condition that has not opened yet holds no button and has
    // registered no field at that moment. Were the script to decline it, there would be nothing to
    // walk up from either, and that form would go without a guard for the rest of its life. So a
    // form is taken as a root whatever it currently holds, and what renders into it later is
    // inside it already.
    [E2EFact]
    public async Task An_empty_form_is_still_a_root_and_guards_what_renders_into_it()
    {
        await using var session = await app.NewPageAsync("/");
        var page = session.Page;

        // Built outside #app, so none of it is Blazor's to diff. Empty on purpose: this is the
        // shape a root has on the render that asks for its guard, before its content arrives.
        await page.EvaluateAsync(
            "() => document.body.insertAdjacentHTML(" +
            "'afterbegin', `<form id='probe-empty'></form>`)");

        var registered = await page.EvaluateAsync<bool>(
            "async () => (await import('/_content/Formidable.Blazor/formidable.js'))" +
            ".registerClickRecovery('probe-empty', [])");
        Assert.True(registered, "an empty form is still the root its own id names");

        // The content arrives afterwards and nothing re-registers, which is the whole property:
        // the guard was scoped to the form, so a button rendered into it is already in reach.
        await page.EvaluateAsync(@"() => {
            document.getElementById('probe-empty').innerHTML =
                `<div id='probe-shim'></div><button type='button' id='probe-late'>Go</button>`;
            window.__lateClicks = 0;
            const button = document.getElementById('probe-late');
            button.addEventListener('click', () => window.__lateClicks++);
            button.addEventListener('pointerdown', () => {
                document.getElementById('probe-shim').style.height = '120px';
            });
        }");

        await PressHoldReleaseAsync(page, page.Locator("#probe-late"), 120);

        // The browser retargeted the click to the form, so this counts recoveries and nothing more.
        Assert.Equal(1, await page.EvaluateAsync<int>("() => window.__lateClicks"));
    }

    private static ILocator SubmitButton(IPage page) =>
        page.GetByRole(AriaRole.Button, new() { Name = "Submit", Exact = true });

    /// <summary>Starts counting real form submissions. Capture phase on the document, so it sees
    /// the event whether the click that produced it reached the button or was delivered to it.</summary>
    private static Task CountSubmitsAsync(IPage page) => page.EvaluateAsync(
        "() => { window.__submits = 0; document.addEventListener('submit', () => window.__submits++, true); }");

    private static Task<int> SubmitCountAsync(IPage page) =>
        page.EvaluateAsync<int>("() => window.__submits");
}
