using Microsoft.Playwright;
using static Formidable.Sample.E2E.SamplePage;
using static Microsoft.Playwright.Assertions;

namespace Formidable.Sample.E2E;

/// <summary>
/// The summary's display parameters, each pinned by what it CHANGES about a fixed set of issues.
/// One blocked submit fills the bands, and the toggles above the form then move those same issues
/// around. The two cases whose subject is a difference read the list on both sides of the toggle;
/// the two capped cases that supply an overflow fragment assert the exact entries a band keeps
/// and the exact ones it hands over, which is a pair a cap doing nothing could not produce. The
/// third capped case supplies no fragment, so it asserts the kept entries and the absence of any
/// overflow element.
/// </summary>
[Collection("e2e")]
public sealed class SummaryShapeJourney(SampleAppFixture app)
{
    private const string NameToggle = "Name the field instead of showing the message";
    private const string GroupToggle = "One entry per field instead of one per issue";
    private const string CapToggle = "Cap each band at three entries";
    private const string RevealToggle = "Reveal what the cap held back";

    private static ILocator ErrorEntries(IPage page) =>
        page.Locator(".formidable-summary__group--error button.formidable-summary__link");

    private static ILocator WarningEntries(IPage page) =>
        page.Locator(".formidable-summary__group--warning button.formidable-summary__link");

    private static ILocator Overflow(IPage page) => page.Locator(".formidable-summary__overflow");

    private static ILocator RevealedEntries(IPage page) =>
        page.Locator(".formidable-summary__overflow .overflow-reveal li");

    private static async Task<IPage> BlockedSubmitAsync(SampleSession session)
    {
        var page = session.Page;
        await page.GetByRole(AriaRole.Button, new() { Name = "Submit", Exact = true }).ClickAsync();
        await Expect(SummaryBands(page)).ToHaveCountAsync(2);
        return page;
    }

    [E2EFact]
    public async Task An_item_template_turns_a_list_of_complaints_into_a_list_of_names()
    {
        await using var session = await app.NewPageAsync("/summary-shape");
        var page = await BlockedSubmitAsync(session);

        // With no ItemTemplate the entries are the rules' messages, in the order the fields sit
        // on the page, with Ticket reference contributing two of them.
        await Expect(ErrorEntries(page)).ToHaveTextAsync(
        [
            "A ticket reference is required",
            "A ticket reference looks like TKT-0000",
            "A requester is required",
            "A requester email is required",
            "A product is required",
            "A version is required",
            "Describe the steps that reproduce it",
        ]);
        await Expect(WarningEntries(page))
            .ToHaveTextAsync(["Without an impact this ticket queues behind everything else"]);

        await page.GetByLabel(NameToggle, new() { Exact = true }).CheckAsync();

        // The same seven entries, in the same order, reading as names — and Ticket reference is
        // in the list twice, which is what the next toggle is for. The template reaches every
        // band, not only the errors.
        await Expect(ErrorEntries(page)).ToHaveTextAsync(
        [
            "Ticket reference",
            "Ticket reference",
            "Requester",
            "Requester email",
            "Product",
            "Version",
            "Steps to reproduce",
        ]);
        await Expect(WarningEntries(page)).ToHaveTextAsync(["Impact"]);
    }

    [E2EFact]
    public async Task Grouping_collapses_the_field_with_two_rules_and_leaves_the_other_band_alone()
    {
        await using var session = await app.NewPageAsync("/summary-shape");
        var page = await BlockedSubmitAsync(session);

        await page.GetByLabel(NameToggle, new() { Exact = true }).CheckAsync();
        await Expect(ErrorEntries(page)).ToHaveCountAsync(7);

        await page.GetByLabel(GroupToggle, new() { Exact = true }).CheckAsync();

        // Six entries for six fields, Ticket reference keeping the position its FIRST issue had.
        await Expect(ErrorEntries(page)).ToHaveTextAsync(
        [
            "Ticket reference",
            "Requester",
            "Requester email",
            "Product",
            "Version",
            "Steps to reproduce",
        ]);

        // The advisory band keeps its one entry. That is not a pin on grouping being per-band —
        // a single entry survives either way — it is the guard that a toggle aimed at the error
        // list leaves the rest of the summary where it was.
        await Expect(WarningEntries(page)).ToHaveTextAsync(["Impact"]);
    }

    [E2EFact]
    public async Task A_cap_with_no_overflow_template_ends_the_list_and_says_nothing()
    {
        await using var session = await app.NewPageAsync("/summary-shape");
        var page = await BlockedSubmitAsync(session);

        await page.GetByLabel(CapToggle, new() { Exact = true }).CheckAsync();

        // The first three of the seven, in the order they already had.
        await Expect(ErrorEntries(page)).ToHaveTextAsync(
        [
            "A ticket reference is required",
            "A ticket reference looks like TKT-0000",
            "A requester is required",
        ]);

        // No element, no sentence: with the fragment unset a capped band renders nothing at all
        // in place of what it dropped.
        await Expect(Overflow(page)).ToHaveCountAsync(0);

        // The cap is counted per band, and this is where the page can say so: a cap counted
        // across the summary would have been spent entirely on errors, leaving this band empty.
        await Expect(WarningEntries(page))
            .ToHaveTextAsync(["Without an impact this ticket queues behind everything else"]);
    }

    [E2EFact]
    public async Task The_expander_names_the_entries_the_cap_held_back()
    {
        await using var session = await app.NewPageAsync("/summary-shape");
        var page = await BlockedSubmitAsync(session);

        await page.GetByLabel(NameToggle, new() { Exact = true }).CheckAsync();
        await page.GetByLabel(GroupToggle, new() { Exact = true }).CheckAsync();
        await page.GetByLabel(CapToggle, new() { Exact = true }).CheckAsync();
        await page.GetByLabel(RevealToggle, new() { Exact = true }).CheckAsync();

        await Expect(ErrorEntries(page))
            .ToHaveTextAsync(["Ticket reference", "Requester", "Requester email"]);

        // One overflow line, on the band that held something back and on no other: the advisory
        // band's single entry is inside the cap, so its band never reaches the fragment.
        await Expect(Overflow(page)).ToHaveCountAsync(1);
        await Expect(Overflow(page)).ToContainTextAsync("3 not listed");
        await Expect(RevealedEntries(page).First).ToBeHiddenAsync();

        await page.Locator(".formidable-summary__overflow .overflow-reveal > summary").ClickAsync();

        // The assertion the widened fragment exists for: the names of what did not fit, which no
        // count could have supplied.
        await Expect(RevealedEntries(page))
            .ToHaveTextAsync(["Product", "Version", "Steps to reproduce"]);
    }

    [E2EFact]
    public async Task Ungrouping_under_the_cap_spends_two_of_its_three_entries_on_one_field()
    {
        await using var session = await app.NewPageAsync("/summary-shape");
        var page = await BlockedSubmitAsync(session);

        await page.GetByLabel(NameToggle, new() { Exact = true }).CheckAsync();
        await page.GetByLabel(CapToggle, new() { Exact = true }).CheckAsync();
        await page.GetByLabel(RevealToggle, new() { Exact = true }).CheckAsync();

        // The cap counts ENTRIES, and ungrouped there is an entry per complaint, so two of the
        // three go to one field and a fourth name joins the ones held back.
        await Expect(ErrorEntries(page))
            .ToHaveTextAsync(["Ticket reference", "Ticket reference", "Requester"]);

        // The count comes first, and the cardinality with it: ToContainTextAsync passes on any
        // matched element, so without this the assertion could not tell one overflow line from
        // two. Only the error band capped, so one is the whole answer.
        await Expect(Overflow(page)).ToHaveCountAsync(1);
        await Expect(Overflow(page)).ToContainTextAsync("4 not listed");

        await page.Locator(".formidable-summary__overflow .overflow-reveal > summary").ClickAsync();
        await Expect(RevealedEntries(page))
            .ToHaveTextAsync(["Requester email", "Product", "Version", "Steps to reproduce"]);
    }

    [E2EFact]
    public async Task A_form_with_only_a_warning_left_submits_and_keeps_the_advisory_band()
    {
        await using var session = await app.NewPageAsync("/summary-shape");
        var page = session.Page;

        await TypeAsync(Field(page, "reference"), "TKT-2031");
        await TypeAsync(Field(page, "requester"), "Ada Lovelace");
        await TypeAsync(Field(page, "requesteremail"), "ada@analytical.example");
        await TypeAsync(Field(page, "product"), "Difference Engine");
        await TypeAsync(Field(page, "version"), "2.1");
        await TypeAsync(Field(page, "steps"), "Crank the handle twice");

        // Tab out of the last field before pressing anything: a fill followed straight by a click
        // is this suite's known source of lost first clicks.
        await TabAsync(page);

        await page.GetByRole(AriaRole.Button, new() { Name = "Submit", Exact = true }).ClickAsync();

        // Impact is left empty on purpose. Its rule is a warning, so it neither blocks the submit
        // nor leaves the summary — which is what the page's last step tells a reader to expect.
        await Expect(page.Locator("p[role='status']")).ToContainTextAsync("support queue");
        await Expect(ErrorEntries(page)).ToHaveCountAsync(0);
        await Expect(WarningEntries(page))
            .ToHaveTextAsync(["Without an impact this ticket queues behind everything else"]);
    }
}
