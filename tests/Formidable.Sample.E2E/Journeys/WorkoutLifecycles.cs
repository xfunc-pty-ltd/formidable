using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static Formidable.Sample.E2E.SamplePage;
using static Microsoft.Playwright.Assertions;

namespace Formidable.Sample.E2E;

/// <summary>
/// The workout page's lifecycles: what happens to an issue between the submit that revealed it and
/// the interaction that answers it. Rows that come and go, an advisory that must never block, a
/// rule whose field leaves the screen while the rule stays, the server's verdict replacing its own
/// previous one, and — the one these exist to protect — a live verdict surviving the refresh its
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

        // The rule is a member of BOTH the "Submit" and "Engaged" rulesets, declared once - a
        // plain submit enforces it, with no extra profile involved. This row was never engaged
        // (typed into, then blurred) before this submit, so it is the submit channel that
        // answers here; An_engaged_attendee_name_discloses_without_a_submit below pins the live
        // channel's half of the story, with no submit anywhere.
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
        // and a field no notification has ever named is not in it, whatever ruleset its rule
        // sits in. Same property FormValidationEngineEngagedProfileTests.
        // A_never_notified_field_gets_no_live_verdict_under_the_engaged_profile pins at the
        // engine level.
        await Expect(MessagesFor(row, "name")).ToHaveCountAsync(0);
        await Expect(SummaryEntry(page, AttendeeNameRequired)).ToHaveCountAsync(0);

        await Field(row, "name").FillAsync("Ada");
        await Field(row, "name").PressAsync("Tab");

        // Engaged and passing: still nothing to say.
        await Expect(MessagesFor(row, "name")).ToHaveCountAsync(0);

        await Field(row, "name").FillAsync("");
        await Field(row, "name").PressAsync("Tab");

        // Engaged and now failing: the message answers live, inline and in the summary, with no
        // submit anywhere since the row was added. Reverting LiveProfile to plain Draft would
        // leave this silent until the next submit — the same mutation
        // Engaging_then_clearing_discloses_with_no_submit exercises against the bare engine.
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
        // indicator anywhere. Mutation that must break this: notifying the engine on every
        // blur-mode blur rather than only while a value commit is pending — the blur above then
        // marks the field touched, and a touched field with nothing to complain about paints
        // formidable-valid, which the class assertion forbids. Same property
        // FormidableInputBaseTests.A_blur_with_no_committed_change_notifies_nothing pins at the
        // component level.
        await Expect(MessagesFor(page, "eventdate")).ToHaveCountAsync(0);
        await Expect(eventDate).Not.ToHaveClassAsync(AnyStateClass);
        await Expect(page.Locator(".formidable-pending")).ToHaveCountAsync(0);

        // The bare class list only discriminates if this page paints state classes at all, so
        // commit a real change elsewhere: fill Event name and tab out (a text input fires its
        // change event when focus leaves, and the default update mode commits on that event) —
        // modified and error-free, it earns formidable-valid, and the tabbed-through date field
        // stays bare even after the render that painted its neighbour.
        await Field(page, "eventname").FillAsync("Dev Summit");
        await Field(page, "eventname").PressAsync("Tab");
        await Expect(Field(page, "eventname")).ToHaveClassAsync(new Regex(@"\bformidable-valid\b"));
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
        // unconditional while the checkbox above it decides whether the field is on screen at all.
        await FillValidRegistrationAsync(page, dietaryNotes: "");
        await SubmitAsync(page);

        await Expect(MessagesFor(page, "dietarynotes"))
            .ToHaveTextAsync([DietaryNotesRequired], new() { Timeout = AsyncTimeoutMs });
        await Expect(SummaryEntry(page, DietaryNotesRequired)).ToBeVisibleAsync();

        await Field(page, "includecatering").UncheckAsync();

        // Unticking removes the field, so its inline message goes with it in the same render. The
        // summary is not re-decided by an edit: the entry stands until a submit rules on it again.
        await Expect(MessagesFor(page, "dietarynotes")).ToHaveCountAsync(0);
        await Expect(SummaryEntry(page, DietaryNotesRequired)).ToBeVisibleAsync();

        await SubmitAsync(page);

        // That submit is the re-decision: the only failing rule now has nowhere to show, so the
        // form blocks with the model-level gate instead of with an entry pointing at nothing.
        await Expect(SummaryEntry(page, HiddenIssueGate))
            .ToBeVisibleAsync(new() { Timeout = AsyncTimeoutMs });
        await Expect(SummaryEntry(page, DietaryNotesRequired)).ToHaveCountAsync(0);

        // A model-level entry has no input to land on, so it lands on the form the page gave the
        // model-level id to — the submit's own auto-focus answers for it first, since the gate is
        // the only error there is to take the visitor to.
        await Expect(Field(page, "form")).ToBeFocusedAsync();

        // The summary entry takes the same route. Dispatched rather than clicked, because that
        // focus scrolls the whole form into view and waiting the scroll out would hand the window
        // to the refresh the catering edit armed — and a refresh retires a gate no refresh
        // synthesizes, leaving the entry to be clicked gone from under the click.
        await page.EvaluateAsync("() => document.activeElement?.blur()");
        await SummaryEntry(page, HiddenIssueGate).DispatchEventAsync("click");
        await Expect(Field(page, "form")).ToBeFocusedAsync();
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
