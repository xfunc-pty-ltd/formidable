using Microsoft.Playwright;
using static Formidable.Sample.E2E.SamplePage;
using static Microsoft.Playwright.Assertions;

namespace Formidable.Sample.E2E;

/// <summary>
/// The page's two disclosure mechanisms and the defensive gate in one pass: a UI-collapsed
/// section's unconditional rule is suppressed until the section renders, the accommodation
/// cascade's own <c>When</c> keeps its rules from ever producing a suppressed issue in the first
/// place, and a submit whose ONLY failure sits in the collapsed section — disclosed nowhere —
/// blocks with the model-level gate that focuses the form itself. Revealing the section is
/// registration, not disclosure: the next submit is what shows the error, and the field stays
/// watched from there until the form passes or resets — its summary entry outlives even the
/// section collapsing again.
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
        // stays collapsed, so its (still-empty) rule is the only failure left — suppressed, and
        // revealed by no submit so far, which leaves the blocked submit with nothing on screen
        // to explain itself. That is what the defensive gate stands in for: not an error never
        // shown in the form's life, but one nothing currently discloses. An error a submit has
        // disclosed holds its own entry until the answer comes clean, and a submit that goes
        // through clears what earlier submits revealed, so either route can stage the gate
        // again.
        await TypeAsync(Field(page, "destination"), "Paris");
        await TabAsync(page);
        await Field(page, "accommodationtype").SelectOptionAsync("Hotel");
        await page.GetByRole(AriaRole.Button, new() { Name = "Submit", Exact = true }).ClickAsync();
        await SummaryEntry(page, HiddenIssueGate).ClickAsync();
        // The page holds two forms since the summary-less variant landed, and both carry the
        // model-level id shape, so the bare tail-match is ambiguous under strict mode. The gate
        // entry that was clicked belongs to the form that renders the summary, so that is the
        // form whose focus is asserted.
        await Expect(page.Locator("form[id$='-form']")
            .Filter(new() { Has = page.Locator(".formidable-summary") })).ToBeFocusedAsync();

        // Revealing the section is registration, not disclosure: the field renders with no
        // message, and the gate still stands. Rendering engages nothing, so the live channel has
        // no verdict of its own to put on the field, and the submit channel waits for the next
        // submit. The field's own visibility is awaited first, because a message list counts zero
        // for a field that has not rendered at all: without that anchor the count below would
        // pass against the DOM as it stood before the click, and a regression that disclosed on
        // registration would leave it green.
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
        // than trading it back for the gate — a field a submit has disclosed stays watched
        // until the form passes or resets. Same property
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

    /// <summary>
    /// The page's required marks, and the one placement the rest of the corpus never shows: the
    /// accommodation question's mark renders in the group's <c>&lt;legend&gt;</c> rather than in
    /// either option's <c>&lt;label&gt;</c>, with the accessible half on the radios instead of on
    /// the glyph. The cascade's own fields are demanded only under a condition, which inspection
    /// cannot judge without a model, so revealing them adds no mark at all.
    /// </summary>
    [E2EFact]
    public async Task The_required_mark_sits_in_the_radio_groups_legend()
    {
        await using var session = await app.NewPageAsync("/disclosure");
        var page = session.Page;
        var marks = page.Locator("span.formidable-required");
        var legendMark = page.Locator("legend span.formidable-required");

        // On load the summary-less variant renders no fields, so every mark on screen belongs to
        // the form above: Destination's, inside its label, and the accommodation question's,
        // which is the one in a legend.
        await Expect(marks).ToHaveCountAsync(2);
        await Expect(legendMark).ToHaveTextAsync("*");
        await Expect(legendMark).ToHaveAttributeAsync("aria-hidden", "true");

        // The glyph announces nothing, so the fact rides the radios the way the kit's own inputs
        // carry it. "Yes" is the radio the fieldset gives the field's id to.
        await Expect(Field(page, "needsaccommodation")).ToHaveAttributeAsync("aria-required", "true");

        // A UI-gated field's mark arrives with the field: the rule behind it is unconditional.
        await page.GetByRole(AriaRole.Button, new() { Name = "Show traveler details", Exact = true }).ClickAsync();
        await Expect(Field(page, "travelername")).ToBeVisibleAsync();
        await Expect(marks).ToHaveCountAsync(3);

        // The two cascade fields carry conditional presence rules, so neither earns one however
        // plainly the condition holds on screen.
        await Field(page, "needsaccommodation").CheckAsync();
        await Expect(Field(page, "accommodationtype")).ToBeVisibleAsync();
        await Field(page, "accommodationtype").SelectOptionAsync("Accessible");
        await Expect(Field(page, "specialrequirements")).ToBeVisibleAsync();
        await Expect(marks).ToHaveCountAsync(3);
    }

    /// <summary>
    /// The page's summary-less variant: the gate's model-level explanation reaches the screen
    /// through <c>FormidableModelMessage</c>'s persistent list, and gives way once an inline
    /// error is on screen to explain the block instead. The list is addressed by the model-level
    /// id convention (the form id plus <c>-messages</c>); only the variant renders one, so the
    /// tail-match is unambiguous even though the page holds two forms.
    /// </summary>
    [E2EFact]
    public async Task The_gate_shows_through_the_model_message_on_the_summaryless_form()
    {
        await using var session = await app.NewPageAsync("/disclosure");
        var page = session.Page;
        var variant = page.Locator("#inline-only-variant");
        var modelMessages = variant.Locator("ul[id$='-form-messages'] .formidable-message");

        // Trip details collapsed: every failing field is hidden, so a blocked submit has nothing
        // inline to disclose and the gate's explanation lands in the form-level list.
        await Expect(modelMessages).ToHaveCountAsync(0);
        await variant.GetByRole(AriaRole.Button, new() { Name = "Submit request", Exact = true }).ClickAsync();
        await Expect(modelMessages).ToHaveTextAsync([HiddenIssueGate]);

        // Reveal the section and submit again: the inline errors take over, and the derived gate
        // dissolves — an error the visitor can see now explains the block.
        await variant.GetByRole(AriaRole.Button, new() { Name = "Show trip details", Exact = true }).ClickAsync();
        await Expect(Field(variant, "travelername")).ToBeVisibleAsync();
        await variant.GetByRole(AriaRole.Button, new() { Name = "Submit request", Exact = true }).ClickAsync();
        await Expect(MessagesFor(variant, "travelername")).ToHaveTextAsync([TravelerNameRequired]);
        await Expect(modelMessages).ToHaveCountAsync(0);
    }
}
