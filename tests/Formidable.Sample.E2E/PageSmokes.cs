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
        await using var session = await app.NewPageAsync("/");
        await Expect(session.Page.Locator("h1")).ToHaveTextAsync("Quickstart");
    }

    [E2EFact]
    public async Task Smoke_profiles()
    {
        await using var session = await app.NewPageAsync("/profiles");
        await Expect(session.Page.Locator("h1")).ToHaveTextAsync("Draft vs Submit profiles");
    }

    [E2EFact]
    public async Task Smoke_custom_profiles()
    {
        await using var session = await app.NewPageAsync("/custom-profiles");
        await Expect(session.Page.Locator("h1")).ToHaveTextAsync("Custom profiles");
    }

    [E2EFact]
    public async Task Smoke_disclosure()
    {
        await using var session = await app.NewPageAsync("/disclosure");
        await Expect(session.Page.Locator("h1")).ToHaveTextAsync("Progressive disclosure");
    }

    [E2EFact]
    public async Task Smoke_severity()
    {
        await using var session = await app.NewPageAsync("/severity");
        await Expect(session.Page.Locator("h1")).ToHaveTextAsync("Severity levels");
    }

    [E2EFact]
    public async Task Smoke_collections()
    {
        await using var session = await app.NewPageAsync("/collections");
        await Expect(session.Page.Locator("h1")).ToHaveTextAsync("Nested collections — row-stable errors");
    }

    [E2EFact]
    public async Task Smoke_virtualized()
    {
        await using var session = await app.NewPageAsync("/virtualized");
        await Expect(session.Page.Locator("h1")).ToHaveTextAsync("Virtualize + KeepRegistered");
    }

    [E2EFact]
    public async Task Smoke_foreign()
    {
        await using var session = await app.NewPageAsync("/foreign");
        await Expect(session.Page.Locator("h1")).ToHaveTextAsync("Wrapping a foreign control");
    }

    [E2EFact]
    public async Task Smoke_vanilla()
    {
        await using var session = await app.NewPageAsync("/vanilla");
        await Expect(session.Page.Locator("h1")).ToHaveTextAsync("Vanilla interop");
    }

    [E2EFact]
    public async Task Smoke_async()
    {
        await using var session = await app.NewPageAsync("/async");
        await Expect(session.Page.Locator("h1")).ToHaveTextAsync("Async rules");
    }

    [E2EFact]
    public async Task Smoke_server()
    {
        await using var session = await app.NewPageAsync("/server");
        await Expect(session.Page.Locator("h1")).ToHaveTextAsync("Server round-trip");
    }

    [E2EFact]
    public async Task Smoke_bootstrap()
    {
        await using var session = await app.NewPageAsync("/bootstrap");
        await Expect(session.Page.Locator("h1")).ToHaveTextAsync("Fitting a UI library");
    }

    [E2EFact]
    public async Task Smoke_field_state()
    {
        await using var session = await app.NewPageAsync("/field-state");
        await Expect(session.Page.Locator("h1")).ToHaveTextAsync("Field-state visualizer");
    }

    [E2EFact]
    public async Task Smoke_normalize()
    {
        await using var session = await app.NewPageAsync("/normalize");
        await Expect(session.Page.Locator("h1")).ToHaveTextAsync("Normalize");
    }

    [E2EFact]
    public async Task Smoke_css_colours()
    {
        await using var session = await app.NewPageAsync("/css-colours");
        await Expect(session.Page.Locator("h1")).ToHaveTextAsync("CSS colours");
    }

    [E2EFact]
    public async Task Smoke_scroll_focus()
    {
        await using var session = await app.NewPageAsync("/scroll-focus");
        await Expect(session.Page.Locator("h1"))
            .ToHaveTextAsync("Scroll & focus — click-to-focus across a long form");
    }

    [E2EFact]
    public async Task Smoke_localization()
    {
        await using var session = await app.NewPageAsync("/localization");
        await Expect(session.Page.Locator("h1")).ToHaveTextAsync("Localization");
    }

    [E2EFact]
    public async Task Smoke_workout()
    {
        await using var session = await app.NewPageAsync("/workout");
        await Expect(session.Page.Locator("h1")).ToHaveTextAsync("Full workout");
    }
}
