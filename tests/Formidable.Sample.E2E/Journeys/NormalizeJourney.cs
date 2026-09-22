using Microsoft.Playwright;
using static Formidable.Sample.E2E.SamplePage;
using static Microsoft.Playwright.Assertions;

namespace Formidable.Sample.E2E;

/// <summary>
/// Normalize() is model-owned cleanup that runs before validation, not a rule of its own: a page
/// that mutates the model must notify the engine which fields changed, and trimming BEFORE
/// validation can turn a blocked submit into one that goes through.
/// </summary>
[Collection("e2e")]
public sealed class NormalizeJourney(SampleAppFixture app)
{
    [E2EFact]
    public async Task Normalize_now_rejudges_the_cleaned_value_at_once()
    {
        await using var session = await app.NewPageAsync("/normalize");
        var page = session.Page;
        var title = page.GetByLabel("Title", new() { Exact = true });

        // Real keystrokes under OnInput: >40 raw but <=40 trimmed, so only Normalize changes
        // the verdict. 10 spaces + 35 characters = 45 raw, 35 trimmed.
        await TypeAsync(title, new string(' ', 10) + new string('x', 35));
        await Expect(MessagesFor(page, "title")).ToHaveTextAsync(["Title is 40 characters max"]);

        // No Tab, no submit: the button notifies the changed field and the message clears.
        await page.GetByRole(AriaRole.Button, new() { Name = "Normalize now", Exact = true }).ClickAsync();
        await Expect(MessagesFor(page, "title")).ToHaveCountAsync(0);
    }

    [E2EFact]
    public async Task Normalize_and_submit_judges_the_cleaned_value()
    {
        await using var session = await app.NewPageAsync("/normalize");
        var page = session.Page;
        var title = page.GetByLabel("Title", new() { Exact = true });

        // 42 raw, 34 trimmed: trimming runs BEFORE validation and the submit succeeds.
        await title.FillAsync("    Meeting notes about the Q3 rollout    ");
        await page.GetByRole(AriaRole.Button, new() { Name = "Normalize + submit", Exact = true }).ClickAsync();
        await Expect(page.Locator("p[role='status']")).ToContainTextAsync("Submitted");

        // All-spaces trims to empty and Cascade.Stop keeps it to ONE message.
        await title.FillAsync(new string(' ', 45));
        await page.GetByRole(AriaRole.Button, new() { Name = "Normalize + submit", Exact = true }).ClickAsync();
        await Expect(MessagesFor(page, "title")).ToHaveTextAsync(["Title is required"]);
    }
}
