using Microsoft.Playwright;
using static Formidable.Sample.E2E.SamplePage;
using static Microsoft.Playwright.Assertions;

namespace Formidable.Sample.E2E;

/// <summary>
/// The workout page's cross-cutting stories, each driven end to end in a real browser: every kind
/// of summary entry landing on a real element, a collection issue's click-to-focus scrolling to
/// its message rather than the middle of a tall group, the async availability check lighting
/// exactly one field, and <c>Normalize()</c> running before the model is posted. The composite
/// page exists to demonstrate these seams together, so they are pinned together.
/// </summary>
[Collection("e2e")]
public sealed class WorkoutFocusAndAsync(SampleAppFixture app)
{
    // The availability check runs for a fixed 300 ms and the whole scoping claim holds only while
    // it is in flight, so the two surfaces that carry it are read together, in the browser, at one
    // instant: the page's own "checking…" indicator must sit in the contact email field's wrapper
    // and nowhere else, and the kit's pending class must be on that field's input alone.
    private const string PendingScopedToContactEmail = """
        () => {
            const email = document.querySelector("[id$='-contactemail']");
            const indicators = document.querySelectorAll("em[role='status']");
            const pending = document.querySelectorAll(".formidable-pending");
            return email !== null
                && indicators.length === 1
                && indicators[0].closest(".field")?.contains(email) === true
                && pending.length === 1
                && pending[0] === email;
        }
        """;

    // The row the fallback test edits: the last of 150, far past anything Virtualize renders at
    // rest, so it has to be scrolled into existence to set an invalid value and scrolled away again
    // to reproduce the shape a blocked submit's auto-focus has to recover from — the one thing
    // about a collection row that addresses it on its own.
    private const string LastSessionTitle = "Session 150:";

    // ItemSize is pinned to match the real row height, but scrollHeight can still shift by a pixel
    // or two as placeholder spacers give way to rendered rows before layout settles. Re-pushing the
    // scroll on each poll is a defensive convergence check, not a single jump that assumes
    // scrollHeight is already final. Mirrors WorkoutLifecycles' identical helper.
    private const string ScrollLastSessionIntoView = $$"""
        () => {
            const panel = document.querySelector('.scroll-panel');
            panel.scrollTop = panel.scrollHeight;
            return panel.textContent.includes('{{LastSessionTitle}}');
        }
        """;

    [E2EFact]
    public async Task Workout_blocked_submit_focus_falls_back_to_an_offscreen_session_row()
    {
        await using var session = await app.NewPageAsync("/workout");
        var page = session.Page;

        // Everything the Submit profile asks for, except the session row this test edits invalid:
        // the first error in document order has to be the seats issue, or the auto-focus lands
        // on something else entirely before it ever reaches the panel.
        await Field(page, "contactemail").FillAsync("workout-e2e-focus@example.com");
        await Field(page, "eventname").FillAsync("Dev Summit");
        await Field(page, "eventdate").FillAsync("2027-05-01");
        await Field(page, "dietarynotes").FillAsync("No nuts");
        await Field(page, "venueregion").FillAsync("South Australia");

        await page.WaitForFunctionAsync(
            ScrollLastSessionIntoView,
            options: new PageWaitForFunctionOptions { Timeout = AsyncTimeoutMs });
        var row = page.Locator(".scroll-panel .field").Filter(new() { HasTextString = LastSessionTitle });
        await Field(row, "seats").FillAsync("900");
        await Field(row, "seats").PressAsync("Tab");
        await Expect(MessagesFor(row, "seats"))
            .ToHaveTextAsync(["Seats must be a whole number between 0 and 500"], new() { Timeout = AsyncTimeoutMs });

        // Scroll the invalid row back out of the render window — the step-11 shape: the only error
        // on the form sits on a row that is not in the DOM at all.
        await page.EvaluateAsync("() => { document.querySelector('.scroll-panel').scrollTop = 0; }");
        await Expect(row).ToHaveCountAsync(0);

        await SubmitRegistrationAsync(page);

        // FormidableForm's own auto-focus misses on the first try (no element carries the field's
        // id yet), falls back through FocusFallback (ScrollToSessionAsync — the identical callback
        // /workout already hands FormidableSummary), and retries once: the panel scrolls to the row
        // and focus lands in its Seats box, with no summary click needed. The bUnit pin at
        // FormidableFormComponentTests.A_blocked_submit_focus_miss_invokes_the_fallback_and_retries_once
        // proves the same try -> fallback -> retry shape at the component level against a stubbed
        // service; this is its browser-level counterpart, with the real scroll and the real DOM.
        // Unwiring FormidableForm's FocusFallback on /workout reproduces the miss unhandled: the
        // submit blocks and neither the panel nor the focus moves.
        await Expect(Field(row, "seats")).ToBeFocusedAsync(new() { Timeout = AsyncTimeoutMs });
        await Expect(row).ToBeVisibleAsync();
    }

    [E2EFact]
    public async Task Workout_blocked_submit_focuses_every_entry_kind()
    {
        await using var session = await app.NewPageAsync("/workout");
        var page = session.Page;

        await SubmitRegistrationAsync(page);
        await Expect(Summary(page)).ToBeVisibleAsync(new() { Timeout = AsyncTimeoutMs });

        // A natively-rendered field: nothing of the kit's is on screen for it, and the entry still
        // lands, because the page hands the native input the id the focus service looks for.
        await SummaryEntry(page, "Venue region is required").ClickAsync();
        await Expect(Field(page, "venueregion")).ToBeFocusedAsync();

        // A collection-level advisory: the field owns no input at all, so the entry lands on the
        // group element carrying the collection's id.
        await SummaryEntry(page, "You can add attendees now or after registering").ClickAsync();
        await Expect(Field(page, "attendees")).ToBeFocusedAsync();

        // And the ordinary case the other two are measured against: a wrapped input.
        await SummaryEntry(page, "Contact email is required").ClickAsync();
        await Expect(Field(page, "contactemail")).ToBeFocusedAsync();
    }

    [E2EFact]
    public async Task Workout_async_pending_scopes_to_the_email_field()
    {
        await using var session = await app.NewPageAsync("/workout");
        var page = session.Page;

        // Armed before the keystroke that starts the check: the predicate above polls inside the
        // browser, so the window it has to catch is never shortened by a round trip from here.
        var pendingScoped = page.WaitForFunctionAsync(
            PendingScopedToContactEmail,
            options: new PageWaitForFunctionOptions { Timeout = AsyncTimeoutMs });
        await Field(page, "contactemail").FillAsync("taken@example.com");
        await pendingScoped;

        await Expect(MessagesFor(page, "contactemail"))
            .ToHaveTextAsync(["That email is already registered"], new() { Timeout = AsyncTimeoutMs });
    }

    [E2EFact]
    public async Task Workout_normalize_runs_before_send()
    {
        await using var session = await app.NewPageAsync("/workout");
        var page = session.Page;

        // Everything a Submit-profile pass asks for; the tier is preselected and catering starts
        // ticked, so the dietary note is the only catering field that has to be answered.
        await Field(page, "contactemail").FillAsync("workout-e2e@example.com");
        await Field(page, "eventname").FillAsync("Dev   Summit");
        await Field(page, "eventdate").FillAsync("2027-05-01");
        await Field(page, "dietarynotes").FillAsync("No nuts");
        await Field(page, "couponcode").FillAsync("WELCOME10");
        await Field(page, "venueregion").FillAsync("South Australia");

        await SubmitRegistrationAsync(page);

        // The API accepts the coupon, so the round trip proves the posted model was the normalized
        // one — the server validates what it was sent.
        await Expect(page.Locator("p[role='status']"))
            .ToHaveTextAsync("Submitted — registration accepted.", new() { Timeout = AsyncTimeoutMs });

        // And the collapse happened on the page's own copy of the model before the send, so the
        // box the double spaces were typed into now reads back single-spaced.
        await Expect(Field(page, "eventname")).ToHaveValueAsync("Dev Summit");
    }

    [E2EFact]
    public async Task Workout_attendee_warning_scrolls_to_the_message_not_the_fieldset()
    {
        await using var session = await app.NewPageAsync("/workout");
        var page = session.Page;

        // A submit reveals the attendees group's disclosure; from there the count-based warning
        // updates live as rows are added, with no further submit needed.
        await SubmitRegistrationAsync(page);
        await Expect(Summary(page)).ToBeVisibleAsync(new() { Timeout = AsyncTimeoutMs });

        var addAttendee = page.GetByRole(AriaRole.Button, new() { Name = "Add attendee", Exact = true });
        for (var i = 0; i < 11; i++)
        {
            await addAttendee.ClickAsync();
        }

        const string warning = "More than 10 attendees needs approval — submission is not blocked";
        await Expect(SummaryEntry(page, warning)).ToBeVisibleAsync(new() { Timeout = AsyncTimeoutMs });

        // The resolved scroll target is the (short) message list, not the fieldset — eleven
        // blank rows just prove the fieldset itself really is the tall thing a pre-fix centring
        // would have used, so this pins the id-retargeting half of the fix (scroll target is the
        // message, not the container), not the size-aware "tall" branch. That branch is pinned
        // separately, on the model-level gate's form-wide fallback below.
        var fieldsetHeight = await Field(page, "attendees")
            .EvaluateAsync<double>("el => el.getBoundingClientRect().height");
        var viewportHeight = await page.EvaluateAsync<double>("() => window.innerHeight");
        Assert.True(fieldsetHeight > viewportHeight * 0.6);

        await SummaryEntry(page, warning).ClickAsync();

        // The scroll is smooth, so its resting position — not a mid-animation snapshot — is what
        // proves where it landed: wait for window.scrollY to stop moving first.
        await WaitForScrollToSettleAsync(page);

        // The message list, addressed the way every collection message list is: the field's id
        // with "-messages" appended, not a spelled-out id.
        var top = await page.Locator("ul[id$='-attendees-messages']")
            .EvaluateAsync<double>("el => el.getBoundingClientRect().top");
        Assert.True(
            top >= 0 && top < viewportHeight,
            $"expected the attendees message list inside the viewport (0..{viewportHeight}), got top={top}");
    }

    [E2EFact]
    public async Task Workout_hidden_issue_gate_top_aligns_the_tall_form()
    {
        await using var session = await app.NewPageAsync("/workout");
        var page = session.Page;

        // Everything the Submit profile asks for except Dietary notes, whose rule is
        // unconditional — and the catering section is unticked BEFORE any submit, so the one
        // failure this scenario needs sits on a field no submit has ever been able to show:
        // once a submit discloses an error, the form keeps listing it even off screen until
        // the form passes or resets, so until one of those only a never-shown failure blocks
        // with the gate.
        await Field(page, "contactemail").FillAsync("workout-e2e-gate@example.com");
        await Field(page, "eventname").FillAsync("Dev Summit");
        await Field(page, "eventdate").FillAsync("2027-05-01");
        await Field(page, "venueregion").FillAsync("South Australia");
        await Field(page, "includecatering").UncheckAsync();

        await SubmitRegistrationAsync(page);

        const string gate =
            "The form cannot be submitted because information that is not currently displayed is invalid.";
        await Expect(SummaryEntry(page, gate)).ToBeVisibleAsync(new() { Timeout = AsyncTimeoutMs });

        // The model-level field renders no message list of its own, so the scroll target falls
        // back to the focus target: the whole <form>, which spans far more than the viewport —
        // the "tall" branch the size-aware alignment exists for. The blocked submit's own
        // auto-focus (FocusFirstErrorOnInvalidSubmit) is what triggers it; no summary click is
        // needed.
        await Expect(Field(page, "form")).ToBeFocusedAsync();
        await WaitForScrollToSettleAsync(page);

        var top = await Field(page, "form").EvaluateAsync<double>("el => el.getBoundingClientRect().top");
        // "start" alignment rests the element's top at (near) the viewport's top; the pre-fix
        // "center" alignment would rest a form this tall's top far above it, strongly negative.
        Assert.True(
            top is >= -5 and <= 50,
            $"expected the form's top near the viewport's top (start-aligned), got top={top}");
    }

    private static Task SubmitRegistrationAsync(IPage page) =>
        page.GetByRole(AriaRole.Button, new() { Name = "Submit registration", Exact = true }).ClickAsync();

    // A smooth scrollIntoView animates over several frames, so reading the resting position
    // immediately after the click that triggers it can catch it mid-flight. Poll window.scrollY
    // until two consecutive reads agree.
    private static async Task WaitForScrollToSettleAsync(IPage page)
    {
        var previous = double.NaN;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var current = await page.EvaluateAsync<double>("() => window.scrollY");
            if (current == previous)
            {
                return;
            }
            previous = current;
            await Task.Delay(50);
        }
    }
}
