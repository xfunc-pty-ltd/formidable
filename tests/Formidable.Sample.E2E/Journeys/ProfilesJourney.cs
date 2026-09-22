using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static Formidable.Sample.E2E.SamplePage;
using static Microsoft.Playwright.Assertions;

namespace Formidable.Sample.E2E;

/// <summary>Draft vs Submit on one validator: a draft-bucket format rule answers live as the
/// field is left, and so does a submit-bucket presence rule, once the visitor has engaged the
/// field it sits on — the live channel evaluates whatever would block a submit, so what separates
/// Title's disclosed required rule from Summary's identical but silent one is engagement and
/// nothing else. A draft save enforces neither presence rule, disclosed or not. The same split
/// decides the required marks the page opens with: they read the submit bucket, so both fields
/// wear one from the first render while the draft save enforces neither.</summary>
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
        // at all: engagement is earned per field and nothing has ever named this one. Its silence
        // beside Title's message is what makes either of them mean anything — a page that simply
        // disclosed everything, and one that disclosed nothing, would each fail one of the two.
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
