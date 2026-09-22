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
/// code calls the engine again. The second test below is the real-runtime counterpart of the live
/// channel's own disclosure: the page's "Submitted by" is a plain InputText whose rule sits in the
/// common bucket, so committing a change engages the field and the live pass answers for it with
/// no submit anywhere in the path. Both surfaces it reaches are ones the engine writes rather than
/// renders — Blazor's own ValidationMessage reads the EditContext store, which is a projection of
/// the live view, and FormidableSummary reads that same view through GetVisibleIssues — so what
/// reddens it is breaking that projection: stop a plain input's committed change from engaging the
/// field, or rebuild the store from what a submit disclosed alone, and the message waits for the
/// Submit button. What it does NOT discriminate is the FormidableFieldAnchor beside the input.
/// That anchor registers the field from its first render, so both assertions would stay green
/// without it — the live channel never consulted registration to make them pass. The anchor
/// speaks for the submit channel, which this page cannot show off, since its seeded value is
/// valid and nothing fails there until the user edits.
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
        // state — no line needs touching for this test's own claim. The blur commits the change,
        // which is what engages the field.
        await page.GetByLabel("Submitted by", new() { Exact = true }).FillAsync(string.Empty);
        await TabAsync(page);

        // Before any submit, and this is the load-bearing pair: no submit has run, so the submit
        // channel has disclosed nothing and the live channel is the only thing that can be
        // speaking here. The compatibility bridge is the native ValidationMessage — an EditContext
        // reader the engine reaches only by projecting the live view into the message store — and
        // the summary reads that same view.
        await Expect(page.Locator(".validation-message")).ToHaveTextAsync("Submitter name is required");
        await Expect(SummaryEntry(page, "Submitter name is required")).ToBeVisibleAsync();

        // The submit lays its own answer over the top. The seeded blank line's error comes only
        // from this submit's report and lands in the exact same render as "Submitted by"'s — both
        // fields are resolved and bucketed inside one ValidateForSubmitAsync dispatch before
        // either reaches the DOM — so waiting for it is what makes the two re-checks below read a
        // DOM this submit has actually reached.
        await page.GetByRole(AriaRole.Button, new() { Name = "Submit", Exact = true }).ClickAsync();
        await Expect(SummaryEntry(page, "Description is required")).ToBeVisibleAsync();

        // Neither surface loses the message across the submit: the anchor is what reveals the
        // field to the submit channel, and the two channels agree about it rather than one
        // replacing the other.
        await Expect(page.Locator(".validation-message")).ToHaveTextAsync("Submitter name is required");
        await Expect(SummaryEntry(page, "Submitter name is required")).ToBeVisibleAsync();
    }
}
