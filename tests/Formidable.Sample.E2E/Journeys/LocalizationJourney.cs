using Microsoft.Playwright;
using static Formidable.Sample.E2E.SamplePage;
using static Microsoft.Playwright.Assertions;

namespace Formidable.Sample.E2E;

/// <summary>
/// Two mechanisms answer under one culture switch: FluentValidation's own translated default
/// message (no <c>WithMessage</c> on Full Name) and the resx-sourced message on Age (a message
/// factory, so the lookup happens at validation time). WebAssembly fixes its culture at startup,
/// so switching stores the choice and force-reloads the host; a fresh browser context — not a
/// reload — is what proves the en-AU fallback boots with nothing stored yet.
/// </summary>
[Collection("e2e")]
public sealed class LocalizationJourney(SampleAppFixture app)
{
    [E2EFact]
    public async Task Culture_stores_survives_reload_and_speaks_both_mechanisms()
    {
        await using var session = await app.NewPageAsync("/localization");
        var page = session.Page;
        var picker = page.GetByLabel("Messages and formats");

        // A fresh context stores no culture: the en-AU fallback boots, and FluentValidation's
        // own untranslated default answers Full Name (no WithMessage on that rule).
        await Expect(picker).ToHaveValueAsync("en-AU");
        await page.GetByRole(AriaRole.Button, new() { Name = "Submit", Exact = true }).ClickAsync();
        await Expect(MessagesFor(page, "fullname")).ToHaveTextAsync(["'Full Name' must not be empty."]);

        // Switching stores the culture and force-reloads the host itself (Navigation.NavigateTo
        // with forceLoad: true) — a separate Playwright-driven ReloadAsync races that in-flight
        // navigation and gets aborted, so nothing here triggers a second one. The pick itself sets
        // the old DOM's select value immediately, before that reload happens, so this assertion
        // alone doesn't prove the reboot; it's the German-message assertions below that can only
        // pass once the stored culture applied at host start, and the locator's own retries carry
        // this poll across the app-triggered navigation regardless — a failure here is loud
        // (English where German was expected), never a false green.
        await picker.SelectOptionAsync("de-DE");
        await Expect(page.GetByLabel("Messages and formats"))
            .ToHaveValueAsync("de-DE", new LocatorAssertionsToHaveValueOptions { Timeout = AsyncTimeoutMs });

        // Both mechanisms speak German on the same empty submit: FluentValidation's own
        // translated default on Full Name, and the resx-sourced message on Age (its Must rule
        // fires on an empty string too, unlike Full Name's NotEmpty-shaped default).
        await page.GetByRole(AriaRole.Button, new() { Name = "Submit", Exact = true }).ClickAsync();
        await Expect(MessagesFor(page, "fullname")).ToHaveTextAsync(["'Full Name' darf nicht leer sein."]);
        await Expect(MessagesFor(page, "age"))
            .ToHaveTextAsync(["Das Alter muss eine ganze Zahl zwischen 18 und 130 sein"]);
    }
}
