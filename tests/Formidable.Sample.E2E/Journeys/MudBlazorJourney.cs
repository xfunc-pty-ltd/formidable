using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static Formidable.Sample.E2E.SamplePage;
using static Microsoft.Playwright.Assertions;

namespace Formidable.Sample.E2E;

/// <summary>
/// A component library's own controls inside FormidableField. MudBlazor's text field and select
/// bind the model with @bind-Value, report each change through @bind-Value:after, take the
/// field's id, state class and aria attributes from one splat, and show Formidable's message
/// under them. MudBlazor's stylesheet and providers come and go with this page, so another page
/// shows none of MudBlazor's markup or look. Its script, once loaded, stays until the app reloads.
/// </summary>
[Collection("e2e")]
public sealed class MudBlazorJourney(SampleAppFixture app)
{
    private static readonly Regex Invalid = StateClass("invalid");

    // The splat puts the state class on Mud's outer wrapper and the id and aria attributes on the
    // input inside it, so both halves are asserted. aria-describedby is followed to the list it
    // names: FormidableFieldMessage renders that list, and a page that dropped it would leave the
    // reference pointing at nothing. Mutation that must break this: remove
    // @attributes="field.InputAttributes" from the text field, and its wrapper never takes
    // formidable-invalid nor its input aria-invalid.
    [E2EFact]
    public async Task A_blocked_submit_shows_formidable_s_messages_on_the_mud_controls()
    {
        await using var session = await app.NewPageAsync("/mudblazor");
        var page = session.Page;

        await SubmitAsync(page);

        await Expect(MessagesFor(page, "bandname")).ToHaveTextAsync(["Band name is required"]);
        await Expect(MessagesFor(page, "room")).ToHaveTextAsync(["Room is required"]);
        foreach (var field in new[] { "bandname", "room" })
        {
            await Expect(MudWrapper(page, field)).ToHaveClassAsync(Invalid);
            await Expect(Field(page, field)).ToHaveAttributeAsync("aria-invalid", "true");

            var describedBy = await Field(page, field).GetAttributeAsync("aria-describedby");
            Assert.False(string.IsNullOrEmpty(describedBy), $"{field}'s input names no message list.");
            await Expect(page.Locator($"[id='{describedBy}'] .formidable-message")).ToHaveCountAsync(1);
        }
    }

    // Each control writes the model through @bind-Value, and its @bind-Value:after reports the
    // change once the model holds it, so each message answers the edit with no second submit.
    // Picking a room also shows MudBlazor's script had run before its components first rendered:
    // a popover set up without it opens far off screen, where no room can be clicked.
    // Mutation that must break this: drop @bind-Value:after="field.NotifyChanged" from the select,
    // and Room's message stays after a room is picked.
    [E2EFact]
    public async Task Mud_inputs_commit_through_the_field_and_answer_live()
    {
        await using var session = await app.NewPageAsync("/mudblazor");
        var page = session.Page;

        await SubmitAsync(page);
        await Expect(MessagesFor(page, "bandname")).ToHaveTextAsync(["Band name is required"]);
        await Expect(MessagesFor(page, "room")).ToHaveTextAsync(["Room is required"]);

        await TypeAsync(Field(page, "bandname"), "The Validators");
        await TabAsync(page);
        await Expect(MessagesFor(page, "bandname")).ToHaveCountAsync(0);
        await Expect(MessagesFor(page, "room")).ToHaveTextAsync(["Room is required"]);

        await PickRoomAsync(page, "Studio B");
        await Expect(MessagesFor(page, "room")).ToHaveCountAsync(0);
        await Expect(MudWrapper(page, "room")).Not.ToHaveClassAsync(Invalid);
    }

    // The stylesheet link sits in the page's own HeadContent, so it leaves the document head with
    // the page, and the providers' markup leaves with it. Coming back puts one link in again, not
    // a second, and the select still works. A page opened first loads neither MudBlazor file.
    // Mutation that must break this: move the stylesheet link out of the page into index.html,
    // and /bootstrap carries it too (moving the script there breaks the opening check the same
    // way).
    [E2EFact]
    public async Task The_head_content_is_confined_to_the_page()
    {
        await using var session = await app.NewPageAsync("/bootstrap");
        var page = session.Page;
        var mudStylesheets = page.Locator("head link[href*='MudBlazor']");
        var sidebar = page.GetByRole(AriaRole.Navigation, new() { Name = "Sample pages" });

        await Expect(page.Locator("h1")).ToHaveTextAsync("Fitting a UI library");
        await Expect(mudStylesheets).ToHaveCountAsync(0);
        await Expect(page.Locator("script[src*='MudBlazor']")).ToHaveCountAsync(0);

        await sidebar.GetByRole(AriaRole.Link, new() { Name = "Fitting MudBlazor", Exact = true }).ClickAsync();
        await Expect(page.Locator("h1")).ToHaveTextAsync("Fitting MudBlazor");
        await Expect(mudStylesheets).ToHaveCountAsync(1);

        await sidebar.GetByRole(AriaRole.Link, new() { Name = "Fitting a UI library", Exact = true }).ClickAsync();
        await Expect(page.Locator("h1")).ToHaveTextAsync("Fitting a UI library");
        await Expect(mudStylesheets).ToHaveCountAsync(0);
        await Expect(page.Locator("[class*='mud-']")).ToHaveCountAsync(0);

        await sidebar.GetByRole(AriaRole.Link, new() { Name = "Fitting MudBlazor", Exact = true }).ClickAsync();
        await Expect(page.Locator("h1")).ToHaveTextAsync("Fitting MudBlazor");
        await Expect(mudStylesheets).ToHaveCountAsync(1);

        await SubmitAsync(page);
        await Expect(MessagesFor(page, "room")).ToHaveTextAsync(["Room is required"]);
        await PickRoomAsync(page, "Studio A");
        await Expect(MessagesFor(page, "room")).ToHaveCountAsync(0);
    }

    // FormidableFieldMessage is each field's one message renderer: MudBlazor's own error text is
    // left unset, so nothing else in the form repeats the message. The summary lists it as well,
    // by design, so the count is taken over the form outside the summary. Mutation that must
    // break this: set the text field's Error and ErrorText from the field's issues beside
    // FormidableFieldMessage, and its message renders twice.
    [E2EFact]
    public async Task Each_field_shows_its_message_once()
    {
        await using var session = await app.NewPageAsync("/mudblazor");
        var page = session.Page;
        var outsideTheSummary = page.Locator("form > :not(.summary-slot)");

        await SubmitAsync(page);
        await Expect(MessagesFor(page, "bandname")).ToHaveTextAsync(["Band name is required"]);
        await Expect(MessagesFor(page, "room")).ToHaveTextAsync(["Room is required"]);

        await Expect(outsideTheSummary.GetByText("Band name is required", new() { Exact = true }))
            .ToHaveCountAsync(1);
        await Expect(outsideTheSummary.GetByText("Room is required", new() { Exact = true }))
            .ToHaveCountAsync(1);
    }

    /// <summary>The MudBlazor wrapper around a field's input: where the splatted class lands.</summary>
    private static ILocator MudWrapper(IPage page, string field) =>
        page.Locator(".mud-input-control").Filter(new() { Has = Field(page, field) });

    private static Task SubmitAsync(IPage page) =>
        page.GetByRole(AriaRole.Button, new() { Name = "Submit", Exact = true }).ClickAsync();

    /// <summary>Opens the room select the way a visitor does and clicks one of the rooms its
    /// popover lists.</summary>
    private static async Task PickRoomAsync(IPage page, string room)
    {
        await MudWrapper(page, "room").ClickAsync();
        await page.GetByRole(AriaRole.Option, new() { Name = room, Exact = true }).ClickAsync();
    }
}
