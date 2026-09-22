using Microsoft.Playwright;
using static Formidable.Sample.E2E.SamplePage;
using static Microsoft.Playwright.Assertions;

namespace Formidable.Sample.E2E;

/// <summary>
/// FormidableValidator (attach mode) notices a removed row's field unregistering on its own —
/// no NotifyFieldSetChanged call anywhere on this page, no OnAfterRenderAsync of its own to poll
/// with. This is the real-runtime counterpart of the bUnit pin
/// Removing_a_row_in_attach_mode_clears_its_issues in FormidableValidatorComponentTests, and the
/// load-bearing evidence for the Task.Yield deferral FormidableValidator.OnFieldRegistryChanged
/// takes as the standard path on this host's actual single-threaded WASM runtime, where no
/// SynchronizationContext is ever installed: revert the FieldRegistry.Changed subscription in
/// FormidableValidator (or its WASM-side deferral) and the removed row's error lingers here
/// exactly as it would in that bUnit pin, since nothing else on the page ever re-validates the
/// model once submit's own pass has run — no live edit touches the departed field, and no page
/// code calls the engine again. The second test below is the real-runtime counterpart of the
/// disclosure fix for the page's plain "Submitted by" input: FormidableFieldAnchor is what
/// registers it, and reverting that anchor is what would suppress its error again before it
/// ever reaches the client bucket.
/// </summary>
[Collection("e2e")]
public sealed class AttachModeJourney(SampleAppFixture app)
{
    [E2EFact]
    public async Task Removing_a_row_clears_its_issue_from_the_page()
    {
        await using var session = await app.NewPageAsync("/attach");
        var page = session.Page;

        // The page seeds one filled line and one blank one — submitting as-is fails only the
        // blank line, so it alone carries the row error this test removes.
        var blankRow = page.Locator("form li.field").Last;
        await page.GetByRole(AriaRole.Button, new() { Name = "Submit", Exact = true }).ClickAsync();
        await Expect(MessagesFor(blankRow, "description")).ToHaveTextAsync(["Description is required"]);
        await Expect(SummaryEntry(page, "Description is required")).ToBeVisibleAsync();

        // Exactly what a page's own Remove button does, and nothing more: the model is mutated
        // and the row's own FormidableInputText/FormidableFieldMessage unregister on dispose.
        // Nothing here calls NotifyFieldSetChanged or touches the engine directly — the validator
        // has to notice the field left by itself.
        await blankRow.GetByRole(AriaRole.Button, new() { Name = "Remove", Exact = true }).ClickAsync();
        await Expect(page.Locator("form li.field")).ToHaveCountAsync(1);

        // The row's own message leaves with its element; the summary entry is the meaningful
        // assertion, since it comes from the engine's own submit-issue channel rather than the
        // row's DOM — it only clears once the automatic post-submit refresh re-validates the
        // model and finds the departed line's rule no longer firing, which Expect's own retry
        // absorbs rather than any fixed wait here.
        await Expect(SummaryEntry(page, "Description is required")).ToHaveCountAsync(0);
    }

    [E2EFact]
    public async Task Clearing_submitted_by_reaches_the_native_message_and_the_summary()
    {
        await using var session = await app.NewPageAsync("/attach");
        var page = session.Page;

        // The seeded value is non-empty, so this alone is what puts the field in a failing
        // state — no line needs touching for this test's own claim. A real blur before the click
        // lets the edit's own live pass settle first, rather than racing the submit that follows
        // against the interop round trip Fill's own change event still has in flight.
        await page.GetByLabel("Submitted by", new() { Exact = true }).FillAsync(string.Empty);
        await TabAsync(page);
        await page.GetByRole(AriaRole.Button, new() { Name = "Submit", Exact = true }).ClickAsync();

        // The seeded blank line's own error comes only from this submit's report and lands in the
        // exact same render as "Submitted by"'s — both fields are resolved and bucketed inside one
        // ValidateForSubmitAsync dispatch before either reaches the DOM. Waiting for it here is
        // what rules out reading "Submitted by"'s message while the pass an earlier edit started is
        // still the last thing to have rendered, rather than this submit's settled verdict.
        await Expect(SummaryEntry(page, "Description is required")).ToBeVisibleAsync();

        // FormidableFieldAnchor registers the plain InputText, so its error survives the
        // suppression an unregistered field hits before ValidateForSubmitAsync ever buckets it:
        // the compatibility bridge speaks it on the native ValidationMessage, and the summary,
        // reading the same now-registered field, lists it too.
        await Expect(page.Locator(".validation-message")).ToHaveTextAsync("Submitter name is required");
        await Expect(SummaryEntry(page, "Submitter name is required")).ToBeVisibleAsync();
    }
}
