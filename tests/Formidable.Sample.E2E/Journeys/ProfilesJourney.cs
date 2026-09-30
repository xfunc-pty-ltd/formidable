using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static Formidable.Sample.E2E.SamplePage;
using static Microsoft.Playwright.Assertions;

namespace Formidable.Sample.E2E;

/// <summary>Draft vs Submit on one validator: a draft-bucket format rule answers live as the
/// field is left, and so does a submit-bucket presence rule once the visitor has engaged the
/// field it sits on, because the live channel evaluates whatever would block a submit. Title
/// shows that. Summary's identical rule stays silent for two reasons: a field nobody has engaged
/// says nothing live, and Summary's input sets WaitForSubmit, so even an engaged Summary shows
/// its failure nowhere until a submit has answered, and answers its edits after that. A draft
/// save enforces neither presence rule, disclosed or not. The same split decides the required
/// marks the page opens with: they read the submit bucket, so both fields wear one from the
/// first render while the draft save enforces neither.</summary>
[Collection("e2e")]
public sealed class ProfilesJourney(SampleAppFixture app)
{
    [E2EFact]
    public async Task An_engaged_presence_rule_discloses_live_while_an_untouched_one_waits()
    {
        await using var session = await app.NewPageAsync("/profiles");
        var page = session.Page;

        // Typing past the draft profile's length rule speaks live, the way a format rule always
        // has — and the commit that carries it is also what puts Title in the engaged set.
        await TypeAsync(Field(page, "title"), new string('x', 61));
        await TabAsync(page);
        await Expect(MessagesFor(page, "title")).ToHaveTextAsync(["Title is 60 characters max"]);

        // Clearing the field swaps which rule is failing, and the submit bucket's own presence
        // rule answers on the same live pass: the channel selects whatever would block a submit,
        // and Title is a field a committed change has named. Nothing above the closing Submit
        // below presses a submit at all. Mutation that must break this: restoring
        // ValidationProfile.Draft as the live channel's default — the required rule then never
        // runs live and this list is empty until a submit is blocked. Same property
        // FormidableEngineLiveDefaultTests.An_engaged_then_emptied_required_field_discloses_with_no_submit
        // pins at the engine level.
        await Field(page, "title").FillAsync("");
        await TabAsync(page);
        await Expect(MessagesFor(page, "title")).ToHaveTextAsync(["Title is required to submit"]);

        // Summary is failing the identical rule, from the first render onwards, and says nothing
        // at all: engagement is earned per field and nothing has ever named this one. Its input
        // also waits for Submit, which would keep it quiet here even if engagement were not
        // earned per field, so this silence is the page's promise to a visitor who has not
        // reached the field rather than a pin on engagement. The untouched-Title check in
        // A_field_that_waits_for_submit_stays_quiet_then_answers_live is this page's
        // browser pin on engagement.
        await Expect(MessagesFor(page, "summary")).ToHaveCountAsync(0);

        // A draft save enforces neither presence rule, and the save happens with Title still
        // empty and still disclosing, which is the whole point of staging it here: the button
        // asks the Draft profile directly, so nothing the live channel decided to say reaches
        // that answer. An empty Title satisfies every rule the draft bucket holds — the length
        // rule is vacuous on it — so the save goes through with its own required-Title message
        // still on screen beside it, which is the independence running the other way: the save
        // asks the validator directly and disturbs nothing the engine is holding. Mutation that
        // must break this: routing the save through the live channel's selection, which would
        // have it report the very failure the message beside it already shows.
        await page.GetByRole(AriaRole.Button, new() { Name = "Save draft", Exact = true }).ClickAsync();
        // The page's own status line, not GetByRole(AriaRole.Status): the summary's persistent
        // advisories region carries role="status" too, so the role alone is two elements here.
        await Expect(page.Locator("p[role='status']"))
            .ToHaveTextAsync("Draft saved — completeness rules were not enforced.");
        await Expect(MessagesFor(page, "title")).ToHaveTextAsync(["Title is required to submit"]);

        // Both marks are still standing over a save that just went through with Summary empty,
        // which is what the page's own step says about them: they read the submit bucket, so
        // nothing a draft save does — or declines to enforce — moves them.
        await Expect(page.Locator("span.formidable-required")).ToHaveCountAsync(2);

        // And submit is what finally reaches the field the visitor never touched.
        await page.GetByRole(AriaRole.Button, new() { Name = "Submit", Exact = true }).ClickAsync();
        await Expect(Summary(page)).ToContainTextAsync("Summary is required to submit");
    }

    // Summary's input sets WaitForSubmit="true": a change engages the field and its rule runs,
    // but its failure shows nowhere until a submit has answered. Title is the witness that the
    // rule has run. Its change comes after Summary's, and the check that change starts answers
    // every field the visitor has changed, so Title's message cannot appear before Summary's
    // answer has. Without the witness, the silence below could be read before the check answered,
    // and so could pass however the page were bound. Mutation that must break it: remove
    // WaitForSubmit from the page's Summary input, and the message-count assertion under the
    // witness fails.
    [E2EFact]
    public async Task A_field_that_waits_for_submit_stays_quiet_then_answers_live()
    {
        await using var session = await app.NewPageAsync("/profiles");
        var page = session.Page;
        var summaryInput = Field(page, "summary");

        // A passing value still earns the valid class while the field waits: WaitForSubmit holds
        // back what the field shows, not the rule, which runs on every change. Mutation that must
        // break this: make a held field lose formidable-valid (its would-pass-submit flag false
        // while held), and this assertion fails.
        await TypeAsync(summaryInput, "A short brief");
        await TabAsync(page);
        await Expect(summaryInput).ToHaveClassAsync(new Regex(@"\bformidable-valid\b"));

        // Title is still untouched, and silent. The check that turned Summary green answered every
        // engaged field in the same update, so if a change engaged more than its own field,
        // Title's required message would already be on screen. This is the page's browser check
        // that engagement is earned per field, since Summary's wait keeps it quiet whatever
        // engagement does. Mutation that must break it: have HandleFieldChanged engage every
        // rendered field, and Title's message shows beside Summary's green.
        await Expect(MessagesFor(page, "title")).ToHaveCountAsync(0);

        await summaryInput.FillAsync("");
        await TabAsync(page);
        await TypeAsync(Field(page, "title"), new string('x', 61));
        await TabAsync(page);
        await Expect(MessagesFor(page, "title")).ToHaveTextAsync(["Title is 60 characters max"]);

        await Expect(MessagesFor(page, "summary")).ToHaveCountAsync(0);
        await Expect(summaryInput).Not.ToHaveClassAsync(new Regex(@"\bformidable-invalid\b"));
        await Expect(summaryInput).Not.ToHaveAttributeAsync("aria-invalid", "true");
        await Expect(Summary(page)).Not.ToContainTextAsync("Summary is required to submit");

        // The submit answers for Summary as it does for every field on screen.
        await page.GetByRole(AriaRole.Button, new() { Name = "Submit", Exact = true }).ClickAsync();
        await Expect(Summary(page)).ToContainTextAsync("Summary is required to submit");
        await Expect(MessagesFor(page, "summary")).ToHaveTextAsync(["Summary is required to submit"]);

        // From here the field answers its edits with no second submit. This step alone cannot
        // tell an ended wait from one that never ends: every check on this page runs the submit
        // rules, and after a submit each edit also re-checks the whole form, so the message clears
        // either way.
        await TypeAsync(summaryInput, "A short brief");
        await TabAsync(page);
        await Expect(MessagesFor(page, "summary")).ToHaveCountAsync(0);

        // A submit that passes leaves nothing waiting to be shown at submit, so a message that
        // appears after it can only be the field answering its own edit. Mutation that must break
        // this: make the wait ignore whether a submit has answered, so it never ends, and the
        // message never appears.
        await Field(page, "title").FillAsync("Quarterly plan");
        await TabAsync(page);
        await page.GetByRole(AriaRole.Button, new() { Name = "Submit", Exact = true }).ClickAsync();
        await Expect(page.Locator("p[role='status']"))
            .ToHaveTextAsync("Submitted — format and completeness rules all passed.");
        await summaryInput.FillAsync("");
        await TabAsync(page);
        await Expect(MessagesFor(page, "summary")).ToHaveTextAsync(["Summary is required to submit"]);
    }

    // The page's first TryIt step reads the two labels before anything is typed, and this is that
    // step: both fields carry a mark neither the markup nor the page's code asks for, because
    // both carry a NotEmpty() in the submit bucket. The mark is decoration and says so, while the
    // input carries the fact — so a screen reader is told the field is required rather than read
    // a star. The closing assert is the half that can only be checked in a browser: querying by
    // ROLE and NAME runs the accessible-name computation itself, so it answers with the name a
    // screen reader would announce for the input. Mutation that must break it: dropping
    // aria-hidden from the marker, which admits the star into that name.
    [E2EFact]
    public async Task Both_required_fields_are_marked_from_the_validator_alone()
    {
        await using var session = await app.NewPageAsync("/profiles");
        var page = session.Page;

        await Expect(page.Locator("span.formidable-required")).ToHaveCountAsync(2);
        await Expect(page.Locator("span.formidable-required").First).ToHaveTextAsync("*");
        await Expect(Field(page, "title")).ToHaveAttributeAsync("aria-required", "true");
        await Expect(Field(page, "summary")).ToHaveAttributeAsync("aria-required", "true");
        await Expect(page.GetByRole(AriaRole.Textbox, new() { Name = "Title", Exact = true }))
            .ToHaveCountAsync(1);
    }

    [E2EFact]
    public async Task Reset_returns_the_form_to_pristine()
    {
        await using var session = await app.NewPageAsync("/profiles");
        var page = session.Page;
        var title = Field(page, "title");

        await page.GetByRole(AriaRole.Button, new() { Name = "Submit", Exact = true }).ClickAsync();
        await Expect(Summary(page)).ToBeVisibleAsync();
        await Expect(title).ToHaveClassAsync(new Regex(@"\bformidable-invalid\b"));

        await page.GetByRole(AriaRole.Button, new() { Name = "Reset", Exact = true }).ClickAsync();

        await Expect(SummaryBands(page)).ToHaveCountAsync(0);
        await Expect(title).Not.ToHaveClassAsync(new Regex(@"\bformidable-invalid\b"));
    }
}
