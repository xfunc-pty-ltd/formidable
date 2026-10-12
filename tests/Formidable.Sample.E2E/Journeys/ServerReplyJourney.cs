using Microsoft.Playwright;
using static Formidable.Sample.E2E.SamplePage;
using static Microsoft.Playwright.Assertions;

namespace Formidable.Sample.E2E;

/// <summary>
/// A server reply on a form that saves from a plain button, driven against the Blazor Web App host
/// fixture's <c>/server-reply</c> page. The order's description is filled and its one line is left
/// without a SKU, so every reply names that line. With the lines hidden and no summary, nothing on
/// screen shows the line's error, so the form-level message explains the refused save and focus
/// lands on the form. The two controls are a reply naming a line on screen and a form with a
/// summary.
/// </summary>
[Collection("e2e")]
public sealed class ServerReplyJourney(SampleAppFixture app)
{
    private const string SkuRequired = "SKU is required";
    private const string HiddenIssueGate =
        "The form cannot be submitted because information that is not currently displayed is invalid.";

    // The case. Showing the lines afterwards brings the line's message back, and the form-level
    // message gives way to it.
    [E2EFact]
    public async Task A_reply_naming_only_hidden_lines_shows_the_forms_message_and_focuses_the_form()
    {
        await using var session = await app.NewSessionAsync();
        var page = session.Page;
        await OpenAsync(page);

        await page.Locator("#show-lines").UncheckAsync();
        await Expect(Field(page, "sku")).ToHaveCountAsync(0);

        await SendAsync(page, 1);

        await Expect(ModelMessages(page)).ToHaveTextAsync([HiddenIssueGate]);
        await Expect(page.Locator("form")).Not.ToContainTextAsync(SkuRequired);
        await Expect(page.Locator("form[id$='-form']")).ToBeFocusedAsync();

        await page.Locator("#show-lines").CheckAsync();

        await Expect(MessagesFor(page, "sku")).ToHaveTextAsync([SkuRequired]);
        await Expect(ModelMessages(page)).ToHaveCountAsync(0);
    }

    // Control: with the lines on screen, the reply shows the line's message there, no form-level
    // message, and focus lands on the line's field.
    [E2EFact]
    public async Task A_reply_naming_a_line_on_screen_shows_its_message_and_no_forms_message()
    {
        await using var session = await app.NewSessionAsync();
        var page = session.Page;
        await OpenAsync(page);

        await SendAsync(page, 1);

        await Expect(MessagesFor(page, "sku")).ToHaveTextAsync([SkuRequired]);
        await Expect(ModelMessages(page)).ToHaveCountAsync(0);
        await Expect(Field(page, "sku")).ToBeFocusedAsync();
    }

    // Control: with the lines hidden and a summary, the summary lists the line's error, and the
    // form-level message shows beside it, since a summary's listing does not show a field's error
    // where the field is.
    [E2EFact]
    public async Task With_a_summary_the_forms_message_shows_beside_the_hidden_lines_entry()
    {
        await using var session = await app.NewSessionAsync();
        var page = session.Page;
        await OpenAsync(page);

        await page.Locator("#show-lines").UncheckAsync();
        await page.Locator("#show-summary").CheckAsync();
        await Expect(Field(page, "sku")).ToHaveCountAsync(0);
        await Expect(Summary(page)).ToHaveCountAsync(1);

        await SendAsync(page, 1);

        await Expect(SummaryEntry(page, SkuRequired)).ToBeVisibleAsync();
        await Expect(SummaryEntry(page, HiddenIssueGate)).ToBeVisibleAsync();
        await Expect(ModelMessages(page)).ToHaveTextAsync([HiddenIssueGate]);
    }

    private async Task OpenAsync(IPage page)
    {
        await page.GotoAsync(app.WebAppOrigin + "/server-reply");

        // Prerendering is off, so the field appearing is the circuit connecting.
        var description = Field(page, "description");
        await Expect(description).ToHaveCountAsync(1, new() { Timeout = AsyncTimeoutMs });
        await TypeAsync(description, "Q3 restock");
        await TabAsync(page);
    }

    private static async Task SendAsync(IPage page, int reply)
    {
        await page.GetByRole(AriaRole.Button, new() { Name = "Send to server", Exact = true }).ClickAsync();
        await Expect(page.Locator("#reply")).ToHaveTextAsync($"Reply {reply} applied.");
    }

    private static ILocator ModelMessages(IPage page) =>
        page.Locator("ul[id$='-form-messages'] .formidable-message");
}
