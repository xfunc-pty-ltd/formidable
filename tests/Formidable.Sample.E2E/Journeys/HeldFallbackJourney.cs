using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static Formidable.Sample.E2E.SamplePage;
using static Microsoft.Playwright.Assertions;

namespace Formidable.Sample.E2E;

/// <summary>
/// What the visitor does while a <c>FocusFallback</c> runs, driven against the Blazor Web App host
/// fixture's <c>/held-fallback</c> page. Its first field sits in a closed section, so a blocked
/// submit's focus move misses it, and the form's fallback opens the section after a delay and
/// answers true. A visitor who moves focus during that delay, by Tab or by a click, keeps it
/// there, and the move answers false. With no interaction the retry lands on the field. So it does
/// when the fallback moves focus itself, which is no press of the visitor's, and when the click
/// lands on plain text, outside the form or inside it, which leaves focus on no control.
/// </summary>
[Collection("e2e")]
public sealed class HeldFallbackJourney(SampleAppFixture app)
{
    // Every case where the visitor presses or clicks during the fallback holds it five seconds
    // rather than the page's default 1.5, so a press that reaches the browser late on a slow runner
    // still comes before the retry. Coming after it, a Tab or a click would move focus off the
    // field the retry had landed on. The cases with no visitor action keep the default.
    private const string LongHold = "?delay=5000";

    private static ILocator SubmitButton(IPage page) =>
        page.GetByRole(AriaRole.Button, new() { Name = "Submit", Exact = true });

    private async Task<IPage> OpenAsync(SampleSession session, string query)
    {
        var page = session.Page;
        await page.GotoAsync(app.WebAppOrigin + "/held-fallback" + query);

        // Prerendering is off, so the button appearing is the circuit connecting.
        await Expect(SubmitButton(page)).ToHaveCountAsync(1, new() { Timeout = AsyncTimeoutMs });
        return page;
    }

    /// <summary>Submits, does what <paramref name="visitor"/> does while the fallback waits, and returns the move's answer once it has one.</summary>
    private static async Task<string> SubmitAsync(IPage page, Func<IPage, Task>? visitor)
    {
        await SubmitButton(page).ClickAsync();
        await Expect(page.Locator("#state")).ToHaveTextAsync("running", new() { Timeout = AsyncTimeoutMs });

        if (visitor is not null)
        {
            await visitor(page);
        }

        var result = page.Locator("#result");
        await Expect(result).ToHaveTextAsync(new Regex("^took="), new() { Timeout = AsyncTimeoutMs });
        return (await result.TextContentAsync())!.Trim();
    }

    [E2EFact]
    public async Task Tab_during_the_fallback_keeps_focus()
    {
        await using var session = await app.NewSessionAsync();
        var page = await OpenAsync(session, LongHold);

        var result = await SubmitAsync(page, visitor => visitor.Keyboard.PressAsync("Tab"));

        Assert.Equal("took=False", result);
        await Expect(page.Locator("#after")).ToBeFocusedAsync();
    }

    [E2EFact]
    public async Task A_click_during_the_fallback_keeps_focus()
    {
        await using var session = await app.NewSessionAsync();
        var page = await OpenAsync(session, LongHold);

        var result = await SubmitAsync(page, visitor => visitor.Locator("#other").ClickAsync());

        Assert.Equal("took=False", result);
        await Expect(page.Locator("#other")).ToBeFocusedAsync();
    }

    [E2EFact]
    public async Task No_interaction_lands_on_the_field()
    {
        await using var session = await app.NewSessionAsync();
        var page = await OpenAsync(session, string.Empty);

        var result = await SubmitAsync(page, visitor: null);

        Assert.Equal("took=True", result);
        await Expect(Field(page, "first")).ToBeFocusedAsync();
    }

    // The fallback focuses the heading before it opens the section. Focus has moved, but no key or
    // pointer press moved it, so the retry still takes the visitor to the field.
    [E2EFact]
    public async Task A_fallback_that_moves_focus_itself_still_lands()
    {
        await using var session = await app.NewSessionAsync();
        var page = await OpenAsync(session, "?mode=selffocus");

        var result = await SubmitAsync(page, visitor: null);

        Assert.Equal("took=True", result);
        await Expect(Field(page, "first")).ToBeFocusedAsync();
    }

    // A press on text that takes no focus leaves focus on the page itself, which says nothing about
    // where the visitor wants to be.
    [E2EFact]
    public async Task A_click_on_blank_page_still_lands()
    {
        await using var session = await app.NewSessionAsync();
        var page = await OpenAsync(session, LongHold);

        var result = await SubmitAsync(page, visitor => visitor.Locator("#blank").ClickAsync());

        Assert.Equal("took=True", result);
        await Expect(Field(page, "first")).ToBeFocusedAsync();
    }

    // Inside the form the same click focuses the <form> itself, which carries tabindex="-1". An
    // element outside the tab order is not a control the visitor chose, so the retry still lands.
    [E2EFact]
    public async Task A_click_on_text_inside_the_form_still_lands()
    {
        await using var session = await app.NewSessionAsync();
        var page = await OpenAsync(session, LongHold);

        var result = await SubmitAsync(page, visitor => visitor.Locator("#inside").ClickAsync());

        Assert.Equal("took=True", result);
        await Expect(Field(page, "first")).ToBeFocusedAsync();
    }
}
