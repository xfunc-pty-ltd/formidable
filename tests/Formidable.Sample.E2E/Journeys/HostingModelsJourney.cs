using Microsoft.Playwright;
using static Formidable.Sample.E2E.SamplePage;
using static Microsoft.Playwright.Assertions;

namespace Formidable.Sample.E2E;

/// <summary>
/// The three hosting shapes the standalone WASM sample cannot reach, driven against the Blazor
/// Web App host fixture: a form on a server circuit, the window between a prerendered form and a
/// working one, and a page with no render mode at all.
/// </summary>
/// <remarks>
/// Every page here hosts the same <c>ContactForm</c> component, so the render mode is what
/// differs between them. Component tests hold both ends of the prerender window as
/// states — a static renderer with a render mode assigned, and an interactive one — but only a
/// browser holds the crossing, and only a real request has a status code.
/// </remarks>
[Collection("e2e")]
public sealed class HostingModelsJourney(SampleAppFixture app)
{
    // Prerendering is off on this page, so nothing at all renders until the circuit is connected
    // and every assertion below is about the interactive render. The cycle is the quickstart's,
    // which is what makes a failure here a hosting-model failure rather than a validation one:
    // the same cycle is pinned on the WASM sample and would fail there too if it were.
    [E2EFact]
    public async Task A_form_on_a_server_circuit_blocks_an_empty_submit_and_lets_a_corrected_one_through()
    {
        await using var session = await app.NewSessionAsync();
        var page = session.Page;

        await page.GotoAsync(app.WebAppOrigin + "/interactive");

        // Waiting for the button IS waiting for interactivity here: an unconnected circuit has
        // rendered no button to wait for.
        var submit = page.GetByRole(AriaRole.Button, new() { Name = "Submit", Exact = true });
        await Expect(submit).ToHaveCountAsync(1, new() { Timeout = AsyncTimeoutMs });

        await submit.ClickAsync();
        await Expect(Summary(page)).ToContainTextAsync("Name is required");
        await Expect(MessagesFor(page, "name")).ToHaveTextAsync(["Name is required"]);
        await Expect(MessagesFor(page, "email")).ToHaveTextAsync(["Email is required"]);

        await TypeAsync(Field(page, "name"), "Ada Lovelace");
        await TabAsync(page);
        await Expect(MessagesFor(page, "name")).ToHaveCountAsync(0);

        await TypeAsync(Field(page, "email"), "ada@example.test");
        await TabAsync(page);

        await submit.ClickAsync();
        await Expect(SummaryBands(page)).ToHaveCountAsync(0);
        await Expect(page.Locator("p[role='status']")).ToHaveTextAsync("Submitted — thanks, Ada Lovelace!");
    }

    // The window, crossed once on one page. Holding the bootstrapper's own request takes the
    // machine's speed out of the first half: the window stays open until this test releases it,
    // so the inert form is there to be read rather than caught on the way past. The second half
    // then waits for a change this test caused, not for one that may already have happened.
    [E2EFact]
    public async Task The_prerendered_form_is_inert_until_the_bootstrapper_runs()
    {
        await using var session = await app.NewSessionAsync();
        var page = session.Page;

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await page.RouteAsync("**/_framework/blazor.web.js*", async route =>
        {
            await release.Task;
            await route.ContinueAsync();
        });

        try
        {
            // Commit, not the default Load: a parked subresource never lets the load event fire,
            // and the prerendered document is in the page the moment the response is.
            await page.GotoAsync(
                app.WebAppOrigin + "/prerendered",
                new PageGotoOptions { WaitUntil = WaitUntilState.Commit });

            await Expect(page.Locator("form[inert]")).ToHaveCountAsync(1);

            release.SetResult();

            await Expect(page.Locator("form")).ToHaveCountAsync(1, new() { Timeout = AsyncTimeoutMs });
            await Expect(page.Locator("form[inert]")).ToHaveCountAsync(0, new() { Timeout = AsyncTimeoutMs });

            // The absence is worth something only if what is left is a working form, so this
            // presses Submit: an interactive render answers with messages, where the same press
            // inside the window posts natively and takes the whole document with it.
            await page.GetByRole(AriaRole.Button, new() { Name = "Submit", Exact = true }).ClickAsync();
            await Expect(MessagesFor(page, "name")).ToHaveTextAsync(["Name is required"]);
        }
        finally
        {
            // A no-op once released; a failed assert must not leave the route handler parked.
            release.TrySetResult();
        }
    }

    // The status code is what no component test can show, and it is the whole of what went wrong
    // before: the refusal aborted the render, so the visitor got the host's error response and
    // everything else the page held went with it.
    [E2EFact]
    public async Task A_statically_rendered_page_says_why_the_form_is_missing_and_keeps_the_rest()
    {
        await using var session = await app.NewSessionAsync();
        var page = session.Page;

        var response = await page.GotoAsync(app.WebAppOrigin + "/static");

        Assert.NotNull(response);
        Assert.Equal(200, response.Status);

        await Expect(page.Locator("#before-form")).ToHaveTextAsync("Before the form.");
        await Expect(page.Locator("#after-form")).ToHaveTextAsync("After the form.");
        await Expect(page.Locator(".formidable-render-mode-message"))
            .ToContainTextAsync("requires an interactive render mode");

        // No form element either: the refusal replaces the form rather than disabling one, so
        // there is nothing on the page for the platform's own 400 to be provoked out of.
        await Expect(page.Locator("form")).ToHaveCountAsync(0);
    }
}
