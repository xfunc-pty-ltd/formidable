using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static Formidable.Sample.E2E.SamplePage;
using static Microsoft.Playwright.Assertions;

namespace Formidable.Sample.E2E;

/// <summary>
/// The workout page's lifecycles: what happens to an issue between the submit that revealed it and
/// the interaction that answers it. Rows that come and go, an advisory that must never block, a
/// rule whose field leaves the screen while the rule stays, the server's verdict replacing its own
/// previous one, the model-level gate standing across the refreshes that run while the form stays
/// blocked, and — the one these exist to protect — a live verdict surviving the refresh its
/// own edit armed. The live channel also answers here with no submit at all: a cross-field error
/// cleared from the other field of its pair, and a tab-through of a blur-mode field that
/// discloses nothing because nothing was committed.
/// </summary>
[Collection("e2e")]
public sealed class WorkoutLifecycles(SampleAppFixture app)
{
    // Read from EventRegistrationValidator, the sample API's registrations endpoint, and the
    // engine's defensive gate: the shipped text is the contract a reader sees, so the tests quote
    // it rather than matching loosely.
    private const string AttendeeNameRequired = "Attendee name is required";
    private const string TooManyAttendees = "More than 10 attendees needs approval — submission is not blocked";
    private const string DietaryNotesRequired = "Dietary notes are required for catering";
    private const string HiddenIssueGate = "The form cannot be submitted because information that is not currently displayed is invalid.";
    private const string CouponRejected = "Coupon code is not recognised";
    private const string SeatsOutOfRange = "Seats must be a whole number between 0 and 500";
    private const string DeadlineAfterEventDate = "Early-bird deadline must be on or before the event date";
    private const string Accepted = "Submitted — registration accepted.";
    private const string Rejected = "The server rejected the registration — see the messages above.";

    // The row the race test edits: the last of 150, far past anything Virtualize has rendered, so
    // the test has to scroll it into existence first. Its title is what addresses it — the one
    // thing about a collection row that is the row's own.
    private const string LastSessionTitle = "Session 150:";

    // Every state class an input can wear — the four exclusive tiers plus the appended pending
    // marker — so an assertion can forbid all of them at once: a field that carries any one of
    // these has been painted, and "painted nothing" is the property the tab-through pins.
    private static readonly Regex AnyStateClass = new(@"\bformidable-(invalid|warning|info|valid|pending)\b");

    // The appended marker on its own, for riding a pass through appear-and-drain: an assertion
    // that must read a pass's landing rather than the DOM as it stood before the pass waits
    // this class out on a field the pass covers.
    private static readonly Regex Pending = new(@"\bformidable-pending\b");

    // ItemSize is pinned to match the real row height, but scrollHeight can still shift by a
    // pixel or two as placeholder spacers are replaced by rendered rows before layout settles.
    // Re-pushing the scroll on each poll is a defensive convergence check, not a single jump
    // that assumes scrollHeight is already final.
    private const string ScrollLastSessionIntoView = $$"""
        () => {
            const panel = document.querySelector('.scroll-panel');
            panel.scrollTop = panel.scrollHeight;
            return panel.textContent.includes('{{LastSessionTitle}}');
        }
        """;

    [E2EFact]
    public async Task Workout_attendee_rows_validate_and_remove()
    {
        await using var session = await app.NewPageAsync("/workout");
        var page = session.Page;

        await AddAttendeeAsync(page);
        var row = page.Locator(".member-list li");
        await SubmitAsync(page);

        // An ordinary submit-bucket presence rule, and this row was never engaged (typed into,
        // then blurred) before the submit — so the submit channel is what answers here, the way
        // it answers for any field no committed change has ever named.
        // An_engaged_attendee_name_discloses_without_a_submit below pins the live channel's half
        // of the same rule, with no submit anywhere.
        await Expect(MessagesFor(row, "name"))
            .ToHaveTextAsync([AttendeeNameRequired], new() { Timeout = AsyncTimeoutMs });
        await Expect(SummaryEntry(page, AttendeeNameRequired)).ToBeVisibleAsync();

        await Field(row, "name").FillAsync("Ada Lovelace");
        await row.GetByRole(AriaRole.Button, new() { Name = "Remove", Exact = true }).ClickAsync();

        // The row leaves and takes its message with it — inline in the same render, because the
        // row that carried it is gone, and out of the summary as soon as a post-submit refresh
        // reconciles it: there is no longer an attendee for that entry to be about.
        await Expect(page.Locator(".member-list li")).ToHaveCountAsync(0);
        await Expect(MessagesFor(page, "name")).ToHaveCountAsync(0);
        await Expect(SummaryEntry(page, AttendeeNameRequired)).ToHaveCountAsync(0);
    }

    [E2EFact]
    public async Task An_engaged_attendee_name_discloses_without_a_submit()
    {
        await using var session = await app.NewPageAsync("/workout");
        var page = session.Page;

        // Mirrors the real walkthrough this ruleset answers: an earlier, unrelated submit
        // already went through, so what follows pins the live channel on its own rather than
        // "nothing has ever validated this form yet" — no submit happens again anywhere below.
        await FillValidRegistrationAsync(page);
        await SubmitAsync(page);
        await Expect(page.Locator("p[role='status']"))
            .ToHaveTextAsync(Accepted, new() { Timeout = AsyncTimeoutMs });

        await AddAttendeeAsync(page);
        var row = page.Locator(".member-list li").First;

        // The row's rule is already failing (a blank Name), but nothing has engaged it yet: a
        // live verdict lands only on the engaged set — the fields a committed change has named —
        // and a field no notification has ever named is not in it, whatever bucket its rule sits
        // in. Same property FormValidationEngineLiveDefaultTests.
        // A_never_notified_row_field_gets_no_live_verdict pins at the engine level.
        await Expect(MessagesFor(row, "name")).ToHaveCountAsync(0);
        await Expect(SummaryEntry(page, AttendeeNameRequired)).ToHaveCountAsync(0);

        await Field(row, "name").FillAsync("Ada");
        await Field(row, "name").PressAsync("Tab");

        // Engaged and passing: still nothing to say.
        await Expect(MessagesFor(row, "name")).ToHaveCountAsync(0);

        await Field(row, "name").FillAsync("");
        await Field(row, "name").PressAsync("Tab");

        // Engaged and now failing: the message answers live, inline and in the summary, with no
        // submit anywhere since the row was added — the live channel evaluates whatever would
        // block a submit, and this row is in the engaged set. Restoring ValidationProfile.Draft
        // as the live channel's default would leave it silent until the next submit — the same
        // mutation FormValidationEngineLiveDefaultTests.
        // An_engaged_then_emptied_required_field_discloses_with_no_submit exercises against the
        // bare engine.
        await Expect(MessagesFor(row, "name"))
            .ToHaveTextAsync([AttendeeNameRequired], new() { Timeout = AsyncTimeoutMs });
        await Expect(SummaryEntry(page, AttendeeNameRequired)).ToBeVisibleAsync();

        // A second, never-engaged row proves the first row's disclosure did not loosen anything
        // — a live pass answers every engaged field, and engagement is earned per field: no
        // committed change has ever named this row's Name, so it is not in the set and it
        // stays silent.
        await AddAttendeeAsync(page);
        var secondRow = page.Locator(".member-list li").Nth(1);
        await Expect(MessagesFor(secondRow, "name")).ToHaveCountAsync(0);
    }

    [E2EFact]
    public async Task Fixing_the_event_date_clears_the_deadline_error_live()
    {
        await using var session = await app.NewPageAsync("/workout");
        var page = session.Page;

        // Both date fields carry UpdateOn="OnBlur", and each internal segment of a native date
        // input is its own tab stop — so the blur that delivers a commit's notification comes
        // from clicking the next element, the same way FillValidRegistrationAsync treats the
        // date field. Event date first, then a deadline that falls AFTER it: once the deadline's
        // blur lands, both dates parse, the pair violates, and the cross-field message discloses
        // on the deadline — no submit anywhere in this journey.
        await Field(page, "eventdate").FillAsync("2027-05-01");
        await Field(page, "earlybirddeadline").FillAsync("2027-06-15");
        await Field(page, "description").ClickAsync();

        await Expect(MessagesFor(page, "earlybirddeadline"))
            .ToHaveTextAsync([DeadlineAfterEventDate], new() { Timeout = AsyncTimeoutMs });

        // Fix the pair from the OTHER field: the edit names the event date alone, but a live
        // pass answers every engaged field, and the deadline is engaged — so its verdict is
        // re-answered to the empty one and the message leaves, still with no submit. Mutation
        // that must break this: scoping the verdict apply to the fields the pass was told
        // changed — the fixing pass then writes only the event date's entry and the deadline
        // keeps reporting an error a direct validate of the model disproves. Same property
        // FormValidationEngineEngagedSetTests.Fixing_a_cross_field_error_from_the_other_field_clears_it_live
        // pins at the engine level.
        await Field(page, "eventdate").FillAsync("2027-09-01");
        await Field(page, "description").ClickAsync();

        await Expect(MessagesFor(page, "earlybirddeadline"))
            .ToHaveCountAsync(0, new() { Timeout = AsyncTimeoutMs });
    }

    [E2EFact]
    public async Task A_tab_through_of_a_blur_mode_field_discloses_nothing()
    {
        await using var session = await app.NewPageAsync("/workout");
        var page = session.Page;
        var eventDate = Field(page, "eventdate");

        // Enter the blur-mode date field and leave with nothing typed. Leaving can take several
        // presses (each internal segment of a native date input is its own tab stop), and the
        // journey needs the field genuinely LEFT — so it tabs until focus reaches the next field
        // and asserts that it did: a real blur has then been delivered, with no committed change
        // behind it.
        await eventDate.ClickAsync();
        for (var presses = 0; presses < 4; presses++)
        {
            await TabAsync(page);
            if (await Field(page, "earlybirddeadline")
                    .EvaluateAsync<bool>("el => el === document.activeElement"))
            {
                break;
            }
        }

        await Expect(Field(page, "earlybirddeadline")).ToBeFocusedAsync();

        // On this fresh, never-submitted form the tab-through validates nothing, touches
        // nothing, paints nothing: no message for the field, no state class on it, no pending
        // indicator anywhere. Two of the three discriminate the notify-on-every-blur mutation
        // outright, because the live channel evaluates whatever would block a submit: a
        // wrongly-notified empty date would join the engaged set, fail the EventDate NotEmpty
        // sitting in the submit bucket, and answer at once with a message and an error class.
        // The pending count is the one that cannot discriminate it — with ContactEmail empty
        // this page's only async rule is gated off, so no pass would light pending either way.
        // The component-level counterpart is
        // FormidableInputBaseTests.A_blur_with_no_committed_change_notifies_nothing.
        //
        // All three are absence reads that pass on their first poll, so what gives the two real
        // ones room to trip is the margin between the blur and the read rather than anything
        // structural: the tab loop and the focus assert above put several interop round trips in
        // between, and this page sets no LiveDebounce for a wrongly-notified pass to wait behind.
        await Expect(MessagesFor(page, "eventdate")).ToHaveCountAsync(0);
        await Expect(eventDate).Not.ToHaveClassAsync(AnyStateClass);
        await Expect(page.Locator(".formidable-pending")).ToHaveCountAsync(0);

        // The bare class list above only means something if this page paints state classes at
        // all, and a clean committed change is the proof: an engaged, error-free Event name has
        // a fresh submit-selected answer holding nothing against it — produced by the live pass
        // the commit started, on a page that tracks no form validity — so it wears the confirmed
        // border. That is the same pass the date field sat out entirely, having been left rather
        // than changed.
        await Field(page, "eventname").FillAsync("Dev Summit");
        await Field(page, "eventname").PressAsync("Tab");
        await Expect(Field(page, "eventname")).ToHaveClassAsync(
            new Regex(@"\bformidable-valid\b"), new() { Timeout = AsyncTimeoutMs });

        // An error paints too, and unlike the confirmed border it is gated on nothing: a
        // malformed engaged email carries formidable-invalid. Through both of those renders the
        // tabbed-through date keeps its bare class list — every pass since has answered the whole
        // model, and the date has never been in the engaged set to hear any of it.
        await Field(page, "contactemail").FillAsync("not-an-email");
        await Field(page, "contactemail").PressAsync("Tab");
        await Expect(Field(page, "contactemail")).ToHaveClassAsync(
            new Regex(@"\bformidable-invalid\b"), new() { Timeout = AsyncTimeoutMs });
        await Expect(MessagesFor(page, "eventdate")).ToHaveCountAsync(0);
        await Expect(eventDate).Not.ToHaveClassAsync(AnyStateClass);
    }

    [E2EFact]
    public async Task Workout_warning_never_blocks()
    {
        await using var session = await app.NewPageAsync("/workout");
        var page = session.Page;

        await FillValidRegistrationAsync(page, coupon: "WELCOME10");

        for (var i = 0; i < 11; i++)
        {
            await AddAttendeeAsync(page);
        }

        // Identical blank rows have nothing of their own to be addressed by until they are named,
        // so the fill loop is the one place a row is reached by position.
        var names = page.Locator(".member-list li [id$='-name']");
        await Expect(names).ToHaveCountAsync(11);
        for (var i = 0; i < 11; i++)
        {
            await names.Nth(i).FillAsync($"Attendee {i + 1}");
        }

        // The collection-level rule answers as the eleventh row lands, and it answers as a warning:
        // the severity is what the rest of this test is about, so it is asserted, not assumed.
        var advisory = page.Locator("ul[id$='-attendees-messages'] .formidable-message--warning");
        await Expect(advisory).ToHaveTextAsync(TooManyAttendees);

        await SubmitAsync(page);

        // Eleven attendees, an advisory on screen, and the registration still goes through — a
        // warning is disclosure, never a veto.
        await Expect(page.Locator("p[role='status']"))
            .ToHaveTextAsync(Accepted, new() { Timeout = AsyncTimeoutMs });
        await Expect(advisory).ToHaveTextAsync(TooManyAttendees);
    }

    [E2EFact]
    public async Task Workout_suppression_and_the_gate()
    {
        await using var session = await app.NewPageAsync("/workout");
        var page = session.Page;

        // Everything the Submit profile asks for except the dietary note, whose rule is
        // unconditional while the checkbox above it decides whether the field is on screen at
        // all. Unticking BEFORE any submit is what stages the gate: the note's rule fails from
        // the start, but no submit ever gets the chance to show it — and an error no submit
        // has shown is exactly what the gate stands in for.
        await FillValidRegistrationAsync(page, dietaryNotes: "");
        await Field(page, "includecatering").UncheckAsync();
        await SubmitAsync(page);

        // The only failing rule has nowhere to show, so the form blocks with the model-level
        // gate instead of with an entry pointing at nothing.
        await Expect(SummaryEntry(page, HiddenIssueGate))
            .ToBeVisibleAsync(new() { Timeout = AsyncTimeoutMs });
        await Expect(SummaryEntry(page, DietaryNotesRequired)).ToHaveCountAsync(0);

        // A model-level entry has no input to land on, so it lands on the form the page gave the
        // model-level id to — the submit's own auto-focus answers for it first, since the gate is
        // the only error there is to take the visitor to.
        await Expect(Field(page, "form")).ToBeFocusedAsync();

        // An edit that puts no message on screen cannot retire the explanation, and Description
        // carries no rule of its own to put one there. The commit runs a live pass and arms the
        // background refresh; the pending marker on the edited field spans both, so its draining
        // is the sign the passes have run — and the gate entry must stand on the far side, because
        // the gate is a predicate over the submit's own answer, not a stored entry a refresh
        // rebuild can drop. Same property
        // FormValidationEngineViewTests.The_gate_survives_a_post_submit_refresh_while_the_form_stays_blocked
        // pins at the engine level.
        await Field(page, "description").FillAsync("Regional developer summit");
        await Field(page, "description").PressAsync("Tab");
        await Expect(Field(page, "description")).ToHaveClassAsync(
            Pending, new() { Timeout = AsyncTimeoutMs });
        await Expect(Field(page, "description")).Not.ToHaveClassAsync(
            Pending, new() { Timeout = AsyncTimeoutMs });
        await Expect(SummaryEntry(page, HiddenIssueGate)).ToBeVisibleAsync();

        // The summary entry takes the same route as the auto-focus, clicked for real: no
        // refresh can retire the gate while the form stays blocked, so there is no window in
        // which the entry could leave from under the click.
        await page.EvaluateAsync("() => document.activeElement?.blur()");
        await SummaryEntry(page, HiddenIssueGate).ClickAsync();
        await Expect(Field(page, "form")).ToBeFocusedAsync();

        // Re-ticking the checkbox puts the field back on screen, and nothing more: on the
        // submit channel disclosure is a submit's act, so the note stays quiet until the next
        // submit rules — which shows the error where it lives and dissolves the gate. The
        // field's own visibility is awaited first, since a message list counts zero for a field
        // that has not rendered at all: without that anchor the count below would pass against
        // the DOM as it stood before the tick, and a regression that disclosed on registration
        // would leave it green.
        await Field(page, "includecatering").CheckAsync();
        await Expect(Field(page, "dietarynotes")).ToBeVisibleAsync();
        await Expect(MessagesFor(page, "dietarynotes")).ToHaveCountAsync(0);
        await SubmitAsync(page);
        await Expect(MessagesFor(page, "dietarynotes"))
            .ToHaveTextAsync([DietaryNotesRequired], new() { Timeout = AsyncTimeoutMs });
        await Expect(SummaryEntry(page, DietaryNotesRequired)).ToBeVisibleAsync();
        await Expect(SummaryEntry(page, HiddenIssueGate)).ToHaveCountAsync(0);

        // Once shown, watched: unticking takes the field and its inline message off screen,
        // but the summary keeps the entry, and another submit keeps it listed rather than
        // trading it back for the gate — a field a submit has disclosed stays watched until
        // the form passes or resets. Same property
        // FormValidationEngineViewTests.A_field_revealed_at_an_earlier_submit_still_counts_disclosed_after_leaving_the_page
        // pins at the engine level.
        await Field(page, "includecatering").UncheckAsync();
        await Expect(MessagesFor(page, "dietarynotes")).ToHaveCountAsync(0);
        await Expect(SummaryEntry(page, DietaryNotesRequired)).ToBeVisibleAsync();

        // A fresh committed edit right before the submit (a changed value, or the commit is
        // elided) strands every stored verdict at an older stamp, so the submit must run the
        // 300 ms availability check itself and the pending window below is structurally wide.
        await Field(page, "description").FillAsync("Regional developer summit, day two");
        await Field(page, "description").PressAsync("Tab");
        await SubmitAsync(page);

        // Both closing asserts describe a summary the submit leaves unchanged, so on their own
        // they would hold against the pre-submit DOM just as well and prove nothing about what
        // the submit decided. Pending on the edited field cannot drain before every in-flight
        // pass over it has landed — the form-wide submit included — so riding it through
        // appear and drain first means the asserts read the pass's own answer.
        await Expect(Field(page, "description")).ToHaveClassAsync(
            Pending, new() { Timeout = AsyncTimeoutMs });
        await Expect(Field(page, "description")).Not.ToHaveClassAsync(
            Pending, new() { Timeout = AsyncTimeoutMs });
        await Expect(SummaryEntry(page, DietaryNotesRequired)).ToBeVisibleAsync();
        await Expect(SummaryEntry(page, HiddenIssueGate)).ToHaveCountAsync(0);
    }

    [E2EFact]
    public async Task Workout_server_coupon_applies_and_replaces()
    {
        await using var session = await app.NewPageAsync("/workout");
        var page = session.Page;

        // A code no client rule can know about: the client submit passes, the POST goes out, and
        // the API's 400 comes back as an issue on the coupon field.
        await FillValidRegistrationAsync(page, coupon: "BOGUS");
        await SubmitAsync(page);

        await Expect(MessagesFor(page, "couponcode"))
            .ToHaveTextAsync([CouponRejected], new() { Timeout = AsyncTimeoutMs });
        await Expect(page.Locator("p[role='status']")).ToHaveTextAsync(Rejected);
        await Expect(SummaryEntry(page, CouponRejected)).ToBeVisibleAsync();

        await Field(page, "couponcode").FillAsync("WELCOME10");
        await SubmitAsync(page);

        // Each response carries the server's current verdict, so the corrected resubmission leaves
        // no trace of the previous rejection — inline or in the summary.
        await Expect(page.Locator("p[role='status']"))
            .ToHaveTextAsync(Accepted, new() { Timeout = AsyncTimeoutMs });
        await Expect(MessagesFor(page, "couponcode")).ToHaveCountAsync(0);
        await Expect(SummaryEntry(page, CouponRejected)).ToHaveCountAsync(0);
    }

    [E2EFact]
    public async Task Workout_seats_verdict_lands_on_first_blur()
    {
        await using var session = await app.NewPageAsync("/workout");
        var page = session.Page;

        // An accepted submit is the setup, not the subject: it leaves the form with no error sites
        // at all, which is the state in which the refresh used to have nothing to resurface.
        await FillValidRegistrationAsync(page);
        await SubmitAsync(page);
        await Expect(page.Locator("p[role='status']"))
            .ToHaveTextAsync(Accepted, new() { Timeout = AsyncTimeoutMs });

        // Only the rows near the scroll position exist in the DOM, so the row has to be scrolled
        // into existence before it can be edited — the panel's own scrollTop, exactly as the
        // page's focus fallback moves it.
        await page.WaitForFunctionAsync(
            ScrollLastSessionIntoView,
            options: new PageWaitForFunctionOptions { Timeout = AsyncTimeoutMs });
        var row = page.Locator(".scroll-panel .field").Filter(new() { HasTextString = LastSessionTitle });
        await Expect(row).ToBeVisibleAsync();

        await Field(row, "seats").FillAsync("900");
        await Field(row, "seats").PressAsync("Tab");

        // That single edit starts a live pass and arms the post-submit refresh at the same instant.
        // The live pass runs the whole model, so the contact email's 300 ms availability check
        // makes it outlast the 300 ms debounce — the refresh comes due while it is still in
        // flight. The refresh defers to it rather than racing or cancelling it; the live pass
        // keeps running and its verdict answers the engaged fields — the committed edit engaged
        // this seats field, so its message arrives with no second submit to ask for it. The
        // deferred refresh, when it does run, executes only the rules still owed an answer at
        // this edit, serving the availability check the live pass answered from the per-rule
        // verdict store rather than paying for it a second time. The assertion is deliberately
        // the auto-waiting one, with no submit and no sleep behind it.
        await Expect(MessagesFor(row, "seats"))
            .ToHaveTextAsync([SeatsOutOfRange], new() { Timeout = AsyncTimeoutMs });
    }

    // Everything the Submit profile asks for, with the two fields the individual tests vary left to
    // the caller: the tier is preselected and catering starts ticked, so a filled dietary note is
    // all the catering section needs, and an empty coupon is one the API accepts.
    private static async Task FillValidRegistrationAsync(
        IPage page,
        string coupon = "",
        string dietaryNotes = "No nuts")
    {
        await Field(page, "contactemail").FillAsync("workout-e2e@example.com");
        await Field(page, "eventname").FillAsync("Dev Summit");
        await Field(page, "eventdate").FillAsync("2027-05-01");

        if (dietaryNotes.Length > 0)
        {
            await Field(page, "dietarynotes").FillAsync(dietaryNotes);
        }

        if (coupon.Length > 0)
        {
            await Field(page, "couponcode").FillAsync(coupon);
        }

        // Last, so the date field above has been blurred — it commits its value on blur, the way a
        // native date input has to be treated.
        await Field(page, "venueregion").FillAsync("South Australia");
    }

    private static Task AddAttendeeAsync(IPage page) =>
        page.GetByRole(AriaRole.Button, new() { Name = "Add attendee", Exact = true }).ClickAsync();

    private static Task SubmitAsync(IPage page) =>
        page.GetByRole(AriaRole.Button, new() { Name = "Submit registration", Exact = true }).ClickAsync();
}
