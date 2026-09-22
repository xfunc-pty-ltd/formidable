using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static Formidable.Sample.E2E.SamplePage;
using static Microsoft.Playwright.Assertions;

namespace Formidable.Sample.E2E;

/// <summary>
/// One navigation and one real assertion per sample page. Each test drives that page's central
/// demonstration and pins it to the library's observable surface through the shared locator
/// vocabulary in <see cref="SamplePage"/>.
/// </summary>
[Collection("e2e")]
public sealed class PageSmokes(SampleAppFixture app)
{
    [E2EFact]
    public async Task Smoke_quickstart()
    {
        var page = await app.NewPageAsync("/");

        await SubmitAsync(page);

        await Expect(Summary(page)).ToContainTextAsync("Name is required");
    }

    [E2EFact]
    public async Task Smoke_profiles()
    {
        var page = await app.NewPageAsync("/profiles");

        // An empty brief passes the Draft profile: its only rule is a length limit.
        await page.GetByRole(AriaRole.Button, new() { Name = "Save draft", Exact = true }).ClickAsync();

        await Expect(page.GetByRole(AriaRole.Status))
            .ToHaveTextAsync("Draft saved — completeness rules were not enforced.");
    }

    [E2EFact]
    public async Task Smoke_custom_profiles()
    {
        var page = await app.NewPageAsync("/custom-profiles");

        await page.GetByLabel("Title", new() { Exact = true }).FillAsync("Release notes");
        await page.GetByLabel("Slug", new() { Exact = true }).FillAsync("release-notes");
        await Field(page, "category").SelectOptionAsync("Tutorial");
        await Field(page, "readminutes").FillAsync("5");
        await Field(page, "publishdate").FillAsync("2026-09-01");
        await SubmitAsync(page);

        // Standard submit does not include the AdminReview ruleset, so the empty review note passes.
        await Expect(page.GetByRole(AriaRole.Status))
            .ToHaveTextAsync("Submitted under Standard submit — accepted.");
    }

    [E2EFact]
    public async Task Smoke_disclosure()
    {
        var page = await app.NewPageAsync("/disclosure");

        await SubmitAsync(page);

        // The collapsed traveler section's issue is suppressed; the rendered fields' are not.
        var summary = Summary(page);
        await Expect(summary).ToBeVisibleAsync();
        await Expect(summary).ToContainTextAsync("Destination is required");
    }

    [E2EFact]
    public async Task Smoke_severity()
    {
        var page = await app.NewPageAsync("/severity");

        await SubmitAsync(page);

        var errors = page.Locator(".formidable-summary__group--error");
        await Expect(errors).ToBeVisibleAsync();
        await Expect(errors).ToContainTextAsync("Title is required");
    }

    [E2EFact]
    public async Task Smoke_collections()
    {
        var page = await app.NewPageAsync("/collections");
        var removeTeam = page.GetByRole(AriaRole.Button, new() { Name = "Remove team", Exact = true });

        // The page seeds two teams, so the roster's own rule only speaks once both are gone.
        await removeTeam.First.ClickAsync();
        await removeTeam.First.ClickAsync();
        await Expect(page.Locator("form fieldset")).ToHaveCountAsync(0);
        await SubmitAsync(page);
        await page.GetByRole(AriaRole.Button, new() { Name = "Add at least one team", Exact = true }).ClickAsync();

        // A collection rule fails against the list, so its entry focuses the container carrying
        // the collection's id — the only element on the page whose id ends that way.
        await Expect(page.Locator("[id$='-teams']")).ToBeFocusedAsync();
    }

    [E2EFact]
    public async Task Smoke_virtualized()
    {
        var page = await app.NewPageAsync("/virtualized");

        await SubmitAsync(page);

        // The DisclosureOverride reports rows Virtualize has never rendered, from the first
        // submit — a pass over all 200 rows, so this one gets the generous timeout too.
        var summary = Summary(page);
        await Expect(summary).ToBeVisibleAsync(new() { Timeout = AsyncTimeoutMs });
        await Expect(summary).ToContainTextAsync("Serial is required");
    }

    [E2EFact]
    public async Task Smoke_foreign()
    {
        var page = await app.NewPageAsync("/foreign");

        await SubmitAsync(page);

        // The foreign select carries messages exactly like a wrapped input.
        await Expect(MessagesFor(page, "colour")).ToHaveTextAsync(["Colour is required"]);
    }

    [E2EFact]
    public async Task Smoke_vanilla()
    {
        var page = await app.NewPageAsync("/vanilla");

        await SubmitAsync(page);

        // Formidable's verdict, rendered by the framework's own ValidationMessage.
        await Expect(page.Locator(".validation-message")).ToHaveTextAsync("Nickname is required");
        await page.GetByRole(AriaRole.Button, new() { Name = "Nickname is required", Exact = true }).ClickAsync();
        await Expect(page.Locator("[id$='-nickname']")).ToBeFocusedAsync();
    }

    [E2EFact]
    public async Task Smoke_async()
    {
        var page = await app.NewPageAsync("/async");

        await page.GetByLabel("Username", new() { Exact = true }).FillAsync("admin");

        // The pending indicator is a real window, not a paint: the check takes 600 ms by default.
        await page.WaitForSelectorAsync("em[role='status']");
        await Expect(MessagesFor(page, "username"))
            .ToHaveTextAsync(["That username is taken"], new() { Timeout = AsyncTimeoutMs });
    }

    [E2EFact]
    public async Task Smoke_server()
    {
        var page = await app.NewPageAsync("/server");

        await page.GetByLabel("MVC controller").CheckAsync();

        // The same filter behind both hosting styles; the caption names the one that will be posted.
        await Expect(page.Locator(".endpoint-caption")).ToContainTextAsync("/api/controller/orders");
    }

    [E2EFact]
    public async Task Smoke_bootstrap()
    {
        var page = await app.NewPageAsync("/bootstrap");

        await SubmitAsync(page);

        // The page's whole lesson: the state class on the input is BOOTSTRAP's, not the kit's.
        await Expect(page.GetByLabel("Name", new() { Exact = true })).ToHaveClassAsync(new Regex(@"\bis-invalid\b"));
    }

    [E2EFact]
    public async Task Smoke_field_state()
    {
        var page = await app.NewPageAsync("/field-state");

        // Focus and leave without typing — the page wires MarkTouched to onblur.
        await page.GetByLabel("Username", new() { Exact = true }).PressAsync("Tab");

        // Column order is the table's own header: Field, Touched, Modified, Validating, ...
        await Expect(page.Locator(".state-table tbody tr:has-text('Username')"))
            .ToHaveTextAsync(new Regex("^UsernameTrueFalse"));
    }

    [E2EFact]
    public async Task Smoke_normalize()
    {
        var page = await app.NewPageAsync("/normalize");

        await page.GetByLabel("Title", new() { Exact = true }).FillAsync(new string(' ', 45));
        await page.GetByRole(AriaRole.Button, new() { Name = "Normalize + submit", Exact = true }).ClickAsync();

        // Normalize trims the 45 spaces away before the pass, and Cascade.Stop means the emptied
        // title fails NotEmpty alone — one message, not two.
        await Expect(MessagesFor(page, "title")).ToHaveTextAsync(["Title is required"]);
    }

    [E2EFact]
    public async Task Smoke_css_colours()
    {
        var page = await app.NewPageAsync("/css-colours");

        await Expect(page.Locator("input[type=color]")).ToHaveCountAsync(3);
        await SubmitAsync(page);

        await Expect(MessagesFor(page, "title")).ToHaveTextAsync(["Title is required"]);
    }

    [E2EFact]
    public async Task Smoke_scroll_focus()
    {
        var page = await app.NewPageAsync("/scroll-focus");

        await SubmitAsync(page);

        // The roster seeds its very last row empty, so the summary's last entry is the form's
        // last field — the longest ride click-to-focus can be asked for on this page.
        await page.Locator(".formidable-summary__link").Last.ClickAsync();
        await Expect(page.Locator("form fieldset").Last.Locator("input").Last).ToBeFocusedAsync();
    }

    [E2EFact]
    public async Task Smoke_localization()
    {
        var page = await app.NewPageAsync("/localization");

        // A fresh context stores no culture, so the app boots on its en-AU fallback.
        await Expect(page.GetByLabel("Messages and formats")).ToHaveValueAsync("en-AU");
        await SubmitAsync(page);

        // FluentValidation's own translated default for the active culture — no WithMessage.
        await Expect(MessagesFor(page, "fullname")).ToHaveTextAsync(["'Full Name' must not be empty."]);
    }

    [E2EFact]
    public async Task Smoke_workout()
    {
        var page = await app.NewPageAsync("/workout");

        await page.GetByRole(AriaRole.Button, new() { Name = "Submit registration", Exact = true }).ClickAsync();

        // A wrapped input's issue and a natively-rendered one's arrive in the same summary.
        var summary = Summary(page);
        await Expect(summary).ToContainTextAsync("Contact email is required", new() { Timeout = AsyncTimeoutMs });
        await Expect(summary).ToContainTextAsync("Venue region is required", new() { Timeout = AsyncTimeoutMs });
    }

    private static Task SubmitAsync(IPage page) =>
        page.GetByRole(AriaRole.Button, new() { Name = "Submit", Exact = true }).ClickAsync();
}
