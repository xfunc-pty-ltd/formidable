using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Formidable.Sample.E2E;

/// <summary>
/// The shape every lesson page's teaching panel keeps: one complementary landmark named "About
/// this page", whose level-2 headings read Try it, The rules and How it works in that order, which
/// comes before the page's form, and whose How it works section stays closed until the visitor
/// opens it. A keyboard or screen-reader visitor meets the steps first and the explanations one
/// click away, on a wide window and a narrow one alike. The pages' in-app links are pinned
/// base-relative here too, because the hosted demo serves the app under a base path that a link
/// starting with a slash walks out of.
/// </summary>
[Collection("e2e")]
public sealed class TeachingPanelJourney(SampleAppFixture app)
{
    /// <summary>The sample's lesson pages: every route in the sidebar. A page added to the sample
    /// joins this list, so every assertion below covers it.</summary>
    public static readonly string[] LessonRoutes =
        ["/profiles", "/", "/custom-profiles", "/disclosure", "/severity", "/draft-load",
            "/collections", "/virtualized", "/foreign", "/vanilla", "/attach",
            "/async", "/server", "/summary-shape", "/dialog-submit", "/scroll-focus",
            "/bootstrap", "/mudblazor", "/css-colours", "/field-state", "/normalize", "/localization",
            "/workout"];

    private const string PanelName = "About this page";

    // Mutations that must break this: the component renders The rules before Try it (the heading
    // order fails); How it works rendered open (the open-property check fails, and with that check
    // taken out the hidden-body check fails on its own); the demo rendered before the aside (the
    // document-order check fails); one listed page left without its How it works (the heading
    // order fails on that route).
    [E2EFact]
    public async Task A_lesson_page_reads_Try_it_then_The_rules_with_How_it_works_closed()
    {
        foreach (var route in LessonRoutes)
        {
            await using var session = await app.NewPageAsync(route);
            var page = session.Page;
            var panel = await AssertOneNamedPanelBeforeTheFormAsync(page, route);

            // A <details> element's open property reflects its open attribute, so false here is
            // the attribute's absence, read with the assertion's own retry.
            var more = panel.Locator("details.teaching__more");
            await Expect(more, $"{route}: How it works").ToHaveCountAsync(1);
            await Expect(more, $"{route}: How it works starts closed").ToHaveJSPropertyAsync("open", false);

            var body = more.Locator(":scope > :not(summary)").First;
            await Expect(body, $"{route}: How it works hides its body until opened").ToBeHiddenAsync();
            await more.Locator(":scope > summary").ClickAsync();
            await Expect(body, $"{route}: How it works shows its body once opened").ToBeVisibleAsync();
        }
    }

    // Below the two-column breakpoint (72rem, 1,152 px; 700 px is also below the 760 px point
    // where the sidebar moves above the page) the panel keeps its own box above the form, so the
    // landmark, its heading order and its place before the form hold there too. Mutations that
    // must break this: the aside hidden below the breakpoint (the landmark count fails), The rules
    // rendered before Try it (the heading order fails), and the demo rendered before the aside
    // (the document-order check fails).
    [E2EFact]
    public async Task On_a_narrow_window_the_panel_stays_one_named_landmark_before_the_form()
    {
        const string route = "/profiles";
        await using var session = await app.NewPageAsync(
            route,
            new BrowserNewContextOptions { ViewportSize = new ViewportSize { Width = 700, Height = 900 } });

        await AssertOneNamedPanelBeforeTheFormAsync(session.Page, $"{route} at 700 px");
    }

    // An href that starts with one slash resolves against the host, not the app's base, so on the
    // hosted demo it leaves the app. A protocol-relative href (two slashes) is an external link
    // and is left out. The page's heading and form are awaited first, so a count of zero is read
    // from the rendered page rather than from one still booting. Mutation that must break this: a
    // root-absolute link added to a lesson page's intro.
    [E2EFact]
    public async Task No_in_app_link_is_root_absolute()
    {
        foreach (var route in LessonRoutes)
        {
            await using var session = await app.NewPageAsync(route);
            var page = session.Page;

            await Expect(page.Locator("main h1"), $"{route}: the page heading").ToHaveCountAsync(1);
            await Expect(page.Locator("main form").First, $"{route}: the page's form").ToBeAttachedAsync();
            await Expect(page.Locator("main a[href^='/']:not([href^='//'])"), $"{route}: root-absolute links")
                .ToHaveCountAsync(0);
        }
    }

    private static async Task<ILocator> AssertOneNamedPanelBeforeTheFormAsync(IPage page, string route)
    {
        var panel = page.GetByRole(AriaRole.Complementary, new() { Name = PanelName, Exact = true });
        await Expect(panel, $"{route}: the \"{PanelName}\" landmark").ToHaveCountAsync(1);

        await Expect(panel.GetByRole(AriaRole.Heading, new() { Level = 2 }), $"{route}: the panel's headings")
            .ToHaveTextAsync(["Try it", "The rules", "How it works"]);

        var form = page.Locator("form").First;
        await Expect(form, $"{route}: the page's first form").ToBeAttachedAsync();
        var panelFirst = await panel.EvaluateAsync<bool>(
            @"(aside, form) => {
                const position = aside.compareDocumentPosition(form);
                return (position & Node.DOCUMENT_POSITION_FOLLOWING) !== 0
                    && (position & Node.DOCUMENT_POSITION_CONTAINED_BY) === 0;
            }",
            await form.ElementHandleAsync());
        Assert.True(panelFirst, $"{route}: the panel must come before the page's first form in document order.");

        return panel;
    }
}
