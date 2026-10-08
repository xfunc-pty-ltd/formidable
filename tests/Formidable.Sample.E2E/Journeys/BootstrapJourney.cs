using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static Formidable.Sample.E2E.SamplePage;
using static Microsoft.Playwright.Assertions;

namespace Formidable.Sample.E2E;

/// <summary>
/// The page's whole lesson: the state class on the input is Bootstrap's own, not the kit's —
/// <c>FormidableCssClasses</c> only remaps the class NAMES, so <c>is-invalid</c> comes and goes
/// exactly the way it would under Bootstrap alone once the field is actually fixed. The form
/// shows once Bootstrap's stylesheet has loaded.
/// </summary>
[Collection("e2e")]
public sealed class BootstrapJourney(SampleAppFixture app)
{
    [E2EFact]
    public async Task Names_invalid_class_clears_after_a_real_fix()
    {
        await using var session = await app.NewPageAsync("/bootstrap");
        var page = session.Page;
        var name = TextBox(page, "Name");

        await page.GetByRole(AriaRole.Button, new() { Name = "Submit", Exact = true }).ClickAsync();
        await Expect(name).ToHaveClassAsync(new Regex(@"\bis-invalid\b"));

        // Real-typed fix, blurred for real: this page's asserted path is the class itself.
        await TypeAsync(name, "Ada Lovelace");
        await TabAsync(page);
        await page.GetByRole(AriaRole.Button, new() { Name = "Submit", Exact = true }).ClickAsync();
        await Expect(name).Not.ToHaveClassAsync(new Regex(@"\bis-invalid\b"));
    }

    // The form shows only once Bootstrap's stylesheet has applied, so it never draws in the
    // sample's own style and then jumps into Bootstrap's. The heading is there all along: the
    // stylesheet styles the whole document, so the page around the form restyles when it
    // arrives. The box around the form takes the visitor's colour scheme as it appears (the
    // context asks for a light one). Mutations that must break this: render the form without
    // waiting for the stylesheet (the form shows while the stylesheet is held); start the scheme
    // watcher on the page's first render, before the box exists (the box never takes
    // data-bs-theme).
    [E2EFact]
    public async Task The_form_shows_once_Bootstrap_s_stylesheet_has_loaded()
    {
        await using var session = await app.NewPageAsync("/css-colours");
        var page = session.Page;
        var loading = page.Locator(".lesson__demo").GetByText("Loading Bootstrap…", new() { Exact = true });
        await using var stylesheet = await HeldRequest.HoldAsync(page, "**/bootstrap.min.css");

        await OpenFromSidebarAsync(page, "Fitting a UI library");
        await stylesheet.RequestedAsync();
        await Expect(loading).ToBeVisibleAsync();
        await Expect(TextBox(page, "Name")).ToHaveCountAsync(0);

        stylesheet.Release();
        await Expect(TextBox(page, "Name")).ToBeVisibleAsync();
        await Expect(loading).ToHaveCountAsync(0);
        await Expect(page.Locator("#bootstrap-demo")).ToHaveAttributeAsync("data-bs-theme", "light");
    }

    // A stylesheet that fails to download leaves the page saying so, with a Retry button, and the
    // form stays away. Retry asks for the file again and, once it has loaded, shows the form as a
    // first visit does, with one stylesheet link in the head. Mutations that must break this: drop
    // the catch around the stylesheet load (the page keeps its placeholder, with no Retry); leave a
    // failed link in the head rather than taking it out (Retry adds a second one).
    [E2EFact]
    public async Task A_failed_stylesheet_load_says_so_and_offers_a_retry()
    {
        const string stylesheetRequest = "**/bootstrap.min.css";
        await using var session = await app.NewPageAsync("/css-colours");
        var page = session.Page;
        var demo = page.Locator(".lesson__demo");
        var failure = demo.GetByText("Bootstrap's stylesheet did not load, so the form cannot show.", new() { Exact = true });
        var retry = demo.GetByRole(AriaRole.Button, new() { Name = "Retry", Exact = true });

        await page.RouteAsync(stylesheetRequest, route => route.AbortAsync());
        await OpenFromSidebarAsync(page, "Fitting a UI library");

        await Expect(failure).ToBeVisibleAsync();
        await Expect(failure).ToHaveAttributeAsync("role", "alert");
        await Expect(retry).ToBeVisibleAsync();
        await Expect(demo.GetByText("Loading Bootstrap…")).ToHaveCountAsync(0);
        await Expect(TextBox(page, "Name")).ToHaveCountAsync(0);

        await page.UnrouteAsync(stylesheetRequest);
        await retry.ClickAsync();

        await Expect(TextBox(page, "Name")).ToBeVisibleAsync();
        await Expect(failure).ToHaveCountAsync(0);
        await Expect(retry).ToHaveCountAsync(0);
        await Expect(page.Locator("link[href*='bootstrap.min.css']")).ToHaveCountAsync(1);
        await page.GetByRole(AriaRole.Button, new() { Name = "Submit", Exact = true }).ClickAsync();
        await Expect(TextBox(page, "Name")).ToHaveClassAsync(new Regex(@"\bis-invalid\b"));
    }

    // A pin: a visitor can leave while the stylesheet is still on its way. Nothing the page started
    // fails once it has gone, and the page it left for keeps the sample's look, with no library
    // stylesheet in its head. Mutations that must break this: drop the stylesheet's removal from
    // DisposeAsync (the next page keeps it); let the scheme watcher's removal assume a watcher
    // exists (a page left before its box showed never started one, and leaving throws).
    [E2EFact]
    public async Task Leaving_while_the_stylesheet_loads_leaves_nothing_behind()
    {
        await using var session = await app.NewPageAsync("/css-colours");
        var page = session.Page;
        var errors = RecordErrors(page);
        await using var stylesheet = await HeldRequest.HoldAsync(page, "**/bootstrap.min.css");

        await OpenFromSidebarAsync(page, "Fitting a UI library");
        await stylesheet.RequestedAsync();
        await OpenFromSidebarAsync(page, "CSS colours");
        stylesheet.Release();

        // A late failure gives no signal to wait for, so the check allows it a moment first.
        await page.WaitForTimeoutAsync(500);
        await Expect(LibraryStylesheets(page)).ToHaveCountAsync(0);
        Assert.Empty(errors);
    }
}
