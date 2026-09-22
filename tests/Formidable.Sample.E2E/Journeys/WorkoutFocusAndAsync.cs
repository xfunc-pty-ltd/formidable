using Microsoft.Playwright;
using static Formidable.Sample.E2E.SamplePage;
using static Microsoft.Playwright.Assertions;

namespace Formidable.Sample.E2E;

/// <summary>
/// The workout page's cross-cutting stories, each driven end to end in a real browser: every kind
/// of summary entry landing on a real element, the async availability check lighting exactly one
/// field, and <c>Normalize()</c> running before the model is posted. The composite page exists to
/// demonstrate these seams together, so they are pinned together.
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

    private static Task SubmitRegistrationAsync(IPage page) =>
        page.GetByRole(AriaRole.Button, new() { Name = "Submit registration", Exact = true }).ClickAsync();
}
