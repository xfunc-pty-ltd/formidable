using Microsoft.Playwright;
using static Formidable.Sample.E2E.SamplePage;
using static Microsoft.Playwright.Assertions;

namespace Formidable.Sample.E2E;

/// <summary>
/// The server round trip: a rejected request applies at the severity it carries (errors block,
/// advisories don't), a resubmission replaces the previous verdict rather than stacking on it,
/// and the same filter answers behind either hosting style the page can post to.
/// </summary>
[Collection("e2e")]
public sealed class ServerJourney(SampleAppFixture app)
{
    // Read from RoundTripOrderValidator and the page's own status line: the shipped text is the
    // contract a reader sees, so the test quotes it rather than matching loosely.
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
        await Expect(Summary(page)).ToHaveCountAsync(0);
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

    private static Task SendAsync(IPage page) =>
        page.GetByRole(AriaRole.Button, new() { Name = "Send to server", Exact = true }).ClickAsync();
}
