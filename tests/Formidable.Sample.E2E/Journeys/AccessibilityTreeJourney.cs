using Microsoft.Playwright;
using static Formidable.Sample.E2E.SamplePage;
using static Microsoft.Playwright.Assertions;

namespace Formidable.Sample.E2E;

/// <summary>
/// One assertion over the accessibility tree, taken where a screen-reader user meets the most of
/// it at once: a blocked submit. Every other assertion in the suite reads a class, a string, an
/// attribute or a count, and the role locators reach for one element's role and accessible name
/// at a time — so the shape those elements sit in is pinned nowhere. That shape is contract. The
/// two live regions and their roles, the lists inside them, the accessible name each field takes
/// from the label wrapped around it, which fields are marked invalid, and the order the whole lot
/// arrives in are all things that could change without one existing test noticing.
/// </summary>
/// <remarks>
/// The page is /summary-shape because its empty submit fills BOTH regions — seven errors and one
/// warning. A snapshot taken where only errors disclose covers the alert region and says nothing
/// whatever about the status region beside it, which is half the shape and the half more likely
/// to be dropped by accident.
/// </remarks>
[Collection("e2e")]
public sealed class AccessibilityTreeJourney(SampleAppFixture app)
{
    [E2EFact]
    public async Task A_blocked_submit_presents_the_accessibility_tree_a_screen_reader_reads()
    {
        await using var session = await app.NewPageAsync("/summary-shape");
        var page = session.Page;

        await page.GetByRole(AriaRole.Button, new() { Name = "Submit", Exact = true }).ClickAsync();
        await Expect(SummaryBands(page)).ToHaveCountAsync(2);

        // Scoped to the form rather than the page. The site nav, the heading and the teaching
        // panel are layout with nothing to say about validation, and folding them in would make
        // this churn on prose edits — the failure mode that gets a snapshot pin re-recorded
        // without being read.
        //
        // What the match is, since it looks exact and is not: every node written here has to be
        // present, carrying this role, this accessible name and this state, in this order. A node
        // that appears in the page and is not written here passes. So this pins what is there and
        // what it is, never the absence of something new; the entry counts are pinned exactly
        // next door, by SummaryShapeJourney's list assertions.
        //
        // Impact is the one textbox with no [invalid] beside it, and that is the contract rather
        // than an omission: aria-invalid follows error severity, and Impact's issue is a warning.
        // The tree says the field has something to read without saying the field is wrong.
        //
        // The format is Playwright's, not the library's. Upgrading the package can respell a tree
        // and send this block red with nothing about Formidable having moved. Re-record it from
        // the failure's own "But was" output, which prints the whole actual tree — and read the
        // diff first, because a regression and a respelling arrive looking identical.
        await Expect(page.Locator("form")).ToMatchAriaSnapshotAsync("""
            - alert:
              - list:
                - listitem:
                  - button "A ticket reference is required"
                - listitem:
                  - button "A ticket reference looks like TKT-0000"
                - listitem:
                  - button "A requester is required"
                - listitem:
                  - button "A requester email is required"
                - listitem:
                  - button "A product is required"
                - listitem:
                  - button "A version is required"
                - listitem:
                  - button "Describe the steps that reproduce it"
            - status:
              - list:
                - listitem:
                  - button "Without an impact this ticket queues behind everything else"
            - text: Ticket reference
            - textbox "Ticket reference" [invalid]
            - list:
              - listitem: A ticket reference is required
              - listitem: A ticket reference looks like TKT-0000
            - text: Requester
            - textbox "Requester" [invalid]
            - list:
              - listitem: A requester is required
            - text: Requester email
            - textbox "Requester email" [invalid]
            - list:
              - listitem: A requester email is required
            - text: Product
            - textbox "Product" [invalid]
            - list:
              - listitem: A product is required
            - text: Version
            - textbox "Version" [invalid]
            - list:
              - listitem: A version is required
            - text: Steps to reproduce
            - textbox "Steps to reproduce" [invalid]
            - list:
              - listitem: Describe the steps that reproduce it
            - text: Impact
            - textbox "Impact"
            - list:
              - listitem: Without an impact this ticket queues behind everything else
            - button "Submit"
            """);
    }
}
