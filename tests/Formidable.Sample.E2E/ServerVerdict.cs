using Microsoft.Playwright;
using static Formidable.Sample.E2E.SamplePage;
using static Microsoft.Playwright.Assertions;

namespace Formidable.Sample.E2E;

/// <summary>
/// The half of a server verdict that is not an error. A rejected request carries advisories
/// alongside its errors, and applying the response puts each one on the field it names at the
/// severity it carries — so this walks the whole life of one: it arrives, it renders as a warning
/// rather than an error, it outlives the edit that clears the error beside it, and it leaves only
/// when the rule behind it stops failing.
/// </summary>
[Collection("e2e")]
public sealed class ServerVerdict(SampleAppFixture app)
{
    // Read from RoundTripOrderValidator and the page's own status line: the shipped text is the
    // contract a reader sees, so the test quotes it rather than matching loosely.
    private const string SkuRequired = "SKU is required";
    private const string HyphenAdvisory = "Hyphens make order references harder to read aloud";
    private const string Accepted = "Server accepted the order.";

    [E2EFact]
    public async Task Server_advisories_render_beside_the_errors_and_outlive_them()
    {
        var page = await app.NewPageAsync("/server");

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

    private static Task SendAsync(IPage page) =>
        page.GetByRole(AriaRole.Button, new() { Name = "Send to server", Exact = true }).ClickAsync();
}
