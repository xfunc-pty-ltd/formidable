using Microsoft.Playwright;
using static Formidable.Sample.E2E.SamplePage;
using static Microsoft.Playwright.Assertions;

namespace Formidable.Sample.E2E;

/// <summary>
/// The server round trip: a rejected request applies at the severity it carries (errors block,
/// advisories don't), a resubmission replaces the previous verdict rather than stacking on it, a
/// reply to a send the visitor has since changed is dropped, and the same filter answers behind
/// either hosting style the page can post to.
/// </summary>
[Collection("e2e")]
public sealed class ServerJourney(SampleAppFixture app)
{
    // Read from RoundTripOrderValidator and the page's own status line: the shipped text is the
    // contract a reader sees, so the test quotes it rather than matching loosely.
    private const string DescriptionRequired = "Description is required";
    private const string SkuRequired = "SKU is required";
    private const string HyphenAdvisory = "Hyphens make order references harder to read aloud";
    private const string Accepted = "Server accepted the order.";

    [E2EFact]
    public async Task Server_advisories_render_beside_the_errors_and_outlive_them()
    {
        await using var session = await app.NewPageAsync("/server");
        var page = session.Page;

        // A description the server advises against, with the SKU left empty so the request is
        // rejected at all — advisories ride a 400, never a success response.
        await Field(page, "description").FillAsync("Q3-restock");
        await SendAsync(page);

        await Expect(MessagesFor(page, "sku"))
            .ToHaveTextAsync([SkuRequired], new() { Timeout = AsyncTimeoutMs });

        // The advisory half of the same response, on its own field. The severity class is the
        // point: the engine applied it as a warning, not as another error.
        await Expect(MessagesFor(page, "description"))
            .ToHaveTextAsync([HyphenAdvisory], new() { Timeout = AsyncTimeoutMs });
        await Expect(page.Locator("ul[id$='-description-messages'] .formidable-message--warning"))
            .ToHaveCountAsync(1);

        await Field(page, "sku").FillAsync("ABC123");
        await SendAsync(page);

        await Expect(page.Locator("p[role='status']"))
            .ToHaveTextAsync(Accepted, new() { Timeout = AsyncTimeoutMs });

        // The error clearing is the post-submit refresh landing, which rebuilds both channels —
        // so what survives it survived a rebuild, not just a render that has not happened yet.
        await Expect(MessagesFor(page, "sku")).ToHaveCountAsync(0, new() { Timeout = AsyncTimeoutMs });

        // The advisory stays: the description still holds the hyphen, so the client's own copy of
        // that rule keeps failing and the refresh keeps disclosing it. An accepted request carries
        // no verdict to replace it with either.
        await Expect(MessagesFor(page, "description")).ToHaveTextAsync([HyphenAdvisory]);

        await Field(page, "description").FillAsync("Q3 restock");

        // The default update mode commits on the element's own change event, which a text input
        // raises when it loses focus - so the edit is not an edit the engine has heard about until
        // focus moves, exactly as it would for a reader typing and then reaching for the button.
        await page.Keyboard.PressAsync("Tab");

        // And it goes when the rule behind it stops failing, without another send: the debounced
        // refresh re-derives the advisory channel and the client's copy is no longer in it.
        await Expect(MessagesFor(page, "description"))
            .ToHaveCountAsync(0, new() { Timeout = AsyncTimeoutMs });
        await Expect(SummaryBands(page)).ToHaveCountAsync(0);
    }

    [E2EFact]
    public async Task Reapplying_the_same_verdict_replaces_instead_of_stacking()
    {
        await using var session = await app.NewPageAsync("/server");
        var page = session.Page;

        // A real-typed, real-blurred description — hyphenless, so it produces no advisory and
        // leaves the SKU-only assertions below untouched; the point is a realistically-typed
        // payload, not a different verdict.
        await TypeAsync(Field(page, "description"), "Q3 restock");
        await TabAsync(page);

        // Two identical rejections: replace-per-apply means the second 400 replaces the
        // first's issues rather than duplicating them. Neither send fills the SKU, and
        // ServerRoundTrip's pre-send Normalize() only ever drops WHITESPACE-only SKU lines
        // (RoundTripOrder.Normalize) — a genuinely empty one survives on purpose so NotEmpty can
        // point at the row, which is exactly the case here. The page renders no raw/normalized
        // preview the way /normalize does, so there is nothing else observable about that call
        // to assert here.
        await SendAsync(page);
        await Expect(MessagesFor(page, "sku"))
            .ToHaveTextAsync([SkuRequired], new() { Timeout = AsyncTimeoutMs });
        await SendAsync(page);
        await Expect(MessagesFor(page, "sku"))
            .ToHaveTextAsync([SkuRequired], new() { Timeout = AsyncTimeoutMs });
    }

    [E2EFact]
    public async Task A_rejected_round_trip_focuses_the_first_problem()
    {
        await using var session = await app.NewPageAsync("/server");
        var page = session.Page;

        // Both Description and the one seeded line's SKU fail on an empty submit; Description
        // sits first in ServerRoundTrip.razor's markup, so applying the server's verdict should
        // focus it — the same first-in-document-order contract a blocked client submit already
        // has.
        await SendAsync(page);

        await Expect(MessagesFor(page, "description")).ToHaveTextAsync(
            [DescriptionRequired], new() { Timeout = AsyncTimeoutMs });
        await Expect(Field(page, "description")).ToBeFocusedAsync();
    }

    [E2EFact]
    public async Task The_controller_endpoint_speaks_the_same_filter()
    {
        await using var session = await app.NewPageAsync("/server");
        var page = session.Page;

        await page.GetByLabel("MVC controller").CheckAsync();
        await Expect(page.Locator(".endpoint-caption")).ToContainTextAsync("/api/controller/orders");

        await SendAsync(page);
        await Expect(MessagesFor(page, "sku"))
            .ToHaveTextAsync([SkuRequired], new() { Timeout = AsyncTimeoutMs });
    }

    [E2EFact]
    public async Task Enter_submits_nothing_on_a_form_with_no_submit_button_and_two_text_boxes()
    {
        await using var session = await app.NewPageAsync("/server");
        var page = session.Page;

        // The page's own sentence, in its How it works: both buttons are type="button", and the
        // form opens with two text boxes (the description and the first SKU line). While it shows
        // more than one, the browser's implicit submission has nothing to fire and no client-side
        // submit runs. An implicit submission here would run the submit profile and disclose the
        // empty description, which is what the absence below rules out. A send that drops every
        // line leaves the description as the one text box, and Enter then does submit, as it
        // does on /disclosure's one-box form.
        await Field(page, "description").FocusAsync();
        await page.Keyboard.PressAsync("Enter");

        // Absences pass on their first evaluation, so the wait is what makes the window generous
        // rather than incidental, the same settle the quickstart journey takes for its own.
        await page.WaitForTimeoutAsync(300);
        await Expect(MessagesFor(page, "description")).ToHaveCountAsync(0);
        await Expect(SummaryBands(page)).ToHaveCountAsync(0);
        await Expect(page.Locator("p[role='status']")).ToHaveTextAsync(string.Empty);
    }

    // Two overlapping sends whose older reply arrives last. The first send goes out with the SKU
    // empty and its request is held in the browser. The visitor fills the SKU and sends again, and
    // that send is accepted. Then the first request goes through, and its 400 arrives last. The
    // page marked each send, and the SKU changed after the first mark, so that reply is dropped: no
    // message under the filled SKU, the summary stays shut, and the status line still says the
    // order was accepted. Applied unmarked, the same reply puts "SKU is required" under ABC123.
    // A dropped reply changes nothing on the page, so there is no change to wait for. The wait after
    // the 400 arrives is long enough for the page to have read and applied it, which is what makes
    // the absences mean something.
    [E2EFact]
    public async Task A_late_reply_to_an_older_send_leaves_a_filled_sku_clean()
    {
        await using var session = await app.NewPageAsync("/server");
        var page = session.Page;

        await TypeAsync(Field(page, "description"), "Q3 restock");
        await TabAsync(page);

        await using var first = await HeldRequest.HoldNextAsync(page, "**/api/orders/", "POST");
        await SendAsync(page);
        await first.RequestedAsync();

        await Field(page, "sku").FillAsync("ABC123");
        await TabAsync(page);
        await SendAsync(page);
        await Expect(page.Locator("p[role='status']"))
            .ToHaveTextAsync(Accepted, new() { Timeout = AsyncTimeoutMs });

        var rejection = page.WaitForResponseAsync(
            response => response.Request.Method == "POST" && response.Url.Contains("/api/orders/", StringComparison.Ordinal) && response.Status == 400,
            new() { Timeout = AsyncTimeoutMs });
        first.Release();
        await rejection;
        await page.WaitForTimeoutAsync(1_500);

        await Expect(Field(page, "sku")).ToHaveValueAsync("ABC123");
        await Expect(MessagesFor(page, "sku")).ToHaveCountAsync(0);
        await Expect(Field(page, "sku")).Not.ToHaveClassAsync(StateClass("invalid"));
        await Expect(SummaryBands(page)).ToHaveCountAsync(0);
        await Expect(page.Locator("p[role='status']")).ToHaveTextAsync(Accepted);
    }

    // The same steps with nothing held. The first reply comes back quickly, but the fill can still
    // commit before the page has handled it, so this is the same race without the hold, and an
    // unmarked apply can leave "SKU is required" under the filled SKU here too. Whichever comes
    // first, the marked page ends with the order accepted and no message under the SKU.
    [E2EFact]
    public async Task The_same_two_sends_with_nothing_held_end_accepted_and_clean()
    {
        await using var session = await app.NewPageAsync("/server");
        var page = session.Page;

        await TypeAsync(Field(page, "description"), "Q3 restock");
        await TabAsync(page);

        var replies = new System.Collections.Concurrent.ConcurrentQueue<int>();
        page.Response += (_, response) =>
        {
            if (response.Request.Method == "POST" && response.Url.Contains("/api/orders/", StringComparison.Ordinal))
            {
                replies.Enqueue(response.Status);
            }
        };

        await SendAsync(page);
        await Field(page, "sku").FillAsync("ABC123");
        await TabAsync(page);
        await SendAsync(page);

        var deadline = DateTime.UtcNow.AddMilliseconds(AsyncTimeoutMs);
        while (replies.Count < 2 && DateTime.UtcNow < deadline)
        {
            await page.WaitForTimeoutAsync(50);
        }

        // The first send went out with the SKU empty and the second with it filled.
        Assert.Equal([200, 400], replies.Order());
        await page.WaitForTimeoutAsync(1_500);
        await Expect(MessagesFor(page, "sku")).ToHaveCountAsync(0);
        await Expect(SummaryBands(page)).ToHaveCountAsync(0);
        await Expect(page.Locator("p[role='status']")).ToHaveTextAsync(Accepted);
    }

    // A send whose normalizing drops a line changes which fields are on the page as it goes. With a
    // second line added, the first SKU holds only spaces and the second is empty, so the send drops
    // the first line and the server names the one left. Its 400 still lands: "SKU is required"
    // under the one SKU left, and Description's error, because the page lets the form show the
    // shorter list before it marks the send.
    [E2EFact]
    public async Task A_send_that_drops_a_blank_line_still_lands_its_rejection()
    {
        await using var session = await app.NewPageAsync("/server");
        var page = session.Page;

        await page.GetByRole(AriaRole.Button, new() { Name = "Add line", Exact = true }).ClickAsync();
        await Expect(Field(page, "sku")).ToHaveCountAsync(2);
        await Field(page, "sku").Nth(0).FillAsync("   ");
        await TabAsync(page);

        await SendAsync(page);

        await Expect(Field(page, "sku")).ToHaveCountAsync(1, new() { Timeout = AsyncTimeoutMs });
        await Expect(MessagesFor(page, "sku"))
            .ToHaveTextAsync([SkuRequired], new() { Timeout = AsyncTimeoutMs });
        await Expect(MessagesFor(page, "description")).ToHaveTextAsync([DescriptionRequired]);
        await Expect(page.Locator("p[role='status']")).ToHaveTextAsync("Server rejected the order — its verdict is now inline.");
    }

    private static Task SendAsync(IPage page) =>
        page.GetByRole(AriaRole.Button, new() { Name = "Send to server", Exact = true }).ClickAsync();
}
