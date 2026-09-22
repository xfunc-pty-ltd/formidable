using Microsoft.Playwright;
using static Formidable.Sample.E2E.SamplePage;
using static Microsoft.Playwright.Assertions;

namespace Formidable.Sample.E2E;

/// <summary>
/// The page's two disclosure mechanisms and the defensive gate in one pass: a UI-collapsed
/// section's unconditional rule is suppressed until the section renders, the accommodation
/// cascade's own <c>When</c> keeps its rules from ever producing a suppressed issue in the first
/// place, and a submit whose ONLY failure sits in the collapsed section — never once shown —
/// blocks with the model-level gate that focuses the form itself. Revealing the section is
/// registration, not disclosure: the next submit is what shows the error, and from then on the
/// field stays watched — its summary entry outlives even the section collapsing again.
/// </summary>
[Collection("e2e")]
public sealed class DisclosureJourney(SampleAppFixture app)
{
    private const string TravelerNameRequired = "Traveler name is required";
    private const string HiddenIssueGate =
        "The form cannot be submitted because information that is not currently displayed is invalid.";

    [E2EFact]
    public async Task Suppression_reveal_and_the_defensive_gate()
    {
        await using var session = await app.NewPageAsync("/disclosure");
        var page = session.Page;

        // Rendered fields disclose; the collapsed traveler section's issue is suppressed.
        await page.GetByRole(AriaRole.Button, new() { Name = "Submit", Exact = true }).ClickAsync();
        await Expect(Summary(page)).ToContainTextAsync("Destination is required");
        await Expect(Summary(page)).ToContainTextAsync("Answer the accommodation question");
        await Expect(Summary(page)).Not.ToContainTextAsync(TravelerNameRequired);

        // The NotifyChanged-wired controls run passes without a submit: answering the
        // accommodation question updates its message in place. The "Yes" radio is the one
        // element the fieldset gives an id, so it is addressed the same way a wrapped input is.
        await Field(page, "needsaccommodation").CheckAsync();
        await Expect(MessagesFor(page, "needsaccommodation")).ToHaveCountAsync(0);

        // Real-typed path: fill the other visible fields validly while the traveler section
        // stays collapsed, so its (still-empty) rule is the only failure left — suppressed,
        // and never once shown by a submit, which is the one staging that trips the defensive
        // gate: an error a submit HAS shown would keep its own entry instead.
        await TypeAsync(Field(page, "destination"), "Paris");
        await TabAsync(page);
        await Field(page, "accommodationtype").SelectOptionAsync("Hotel");
        await page.GetByRole(AriaRole.Button, new() { Name = "Submit", Exact = true }).ClickAsync();
        await SummaryEntry(page, HiddenIssueGate).ClickAsync();
        await Expect(page.Locator("[id$='-form']")).ToBeFocusedAsync();

        // Revealing the section is registration, not disclosure: the field renders with no
        // message, and the gate still stands — only a submit re-decides what shows. The field's
        // own visibility is awaited first, because a message list counts zero for a field that
        // has not rendered at all: without that anchor the count below would pass against the
        // DOM as it stood before the click, and a regression that disclosed on registration
        // would leave it green.
        await page.GetByRole(AriaRole.Button, new() { Name = "Show traveler details", Exact = true }).ClickAsync();
        await Expect(Field(page, "travelername")).ToBeVisibleAsync();
        await Expect(MessagesFor(page, "travelername")).ToHaveCountAsync(0);
        await Expect(SummaryEntry(page, HiddenIssueGate)).ToBeVisibleAsync();

        // That submit: the error lands inline and in the summary, and the gate gives way to
        // it — there is a disclosed error to explain the block now.
        await page.GetByRole(AriaRole.Button, new() { Name = "Submit", Exact = true }).ClickAsync();
        await Expect(MessagesFor(page, "travelername")).ToHaveTextAsync([TravelerNameRequired]);
        await Expect(SummaryEntry(page, TravelerNameRequired)).ToBeVisibleAsync();
        await Expect(SummaryEntry(page, HiddenIssueGate)).ToHaveCountAsync(0);

        // Once shown, watched: collapsing the section takes the inline message with the
        // markup, but the summary keeps the entry, and another submit keeps it listed rather
        // than trading it back for the gate. Same property
        // FormValidationEngineViewTests.A_field_revealed_at_an_earlier_submit_still_counts_disclosed_after_leaving_the_page
        // pins at the engine level. On their own the two persistence asserts would hold
        // against the pre-submit DOM just as well, so the submit is given a flip only its own
        // landing can produce: switching the accommodation type to Accessible first renders
        // the special-requirements field, empty and quiet (nothing has engaged it), and that
        // submit is what discloses its error — the asserts wait behind it and therefore read
        // the pass's own answer.
        await page.GetByRole(AriaRole.Button, new() { Name = "Hide traveler details", Exact = true }).ClickAsync();
        await Expect(MessagesFor(page, "travelername")).ToHaveCountAsync(0);
        await Expect(SummaryEntry(page, TravelerNameRequired)).ToBeVisibleAsync();
        await Field(page, "accommodationtype").SelectOptionAsync("Accessible");
        await Expect(MessagesFor(page, "specialrequirements")).ToHaveCountAsync(0);
        await page.GetByRole(AriaRole.Button, new() { Name = "Submit", Exact = true }).ClickAsync();
        await Expect(MessagesFor(page, "specialrequirements"))
            .ToHaveTextAsync(["Describe the special requirements"]);
        await Expect(SummaryEntry(page, TravelerNameRequired)).ToBeVisibleAsync();
        await Expect(SummaryEntry(page, HiddenIssueGate)).ToHaveCountAsync(0);
    }
}
