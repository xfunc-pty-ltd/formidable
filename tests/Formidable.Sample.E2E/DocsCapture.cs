using Microsoft.Playwright;
using static Formidable.Sample.E2E.SamplePage;
using static Microsoft.Playwright.Assertions;

namespace Formidable.Sample.E2E;

/// <summary>
/// Marks a docs-capture test: skips unless FORMIDABLE_CAPTURE=1 is set alongside FORMIDABLE_E2E=1.
/// The e2e gate alone — the shape a release-verification or CI run uses — must never regenerate the
/// shipped PNGs as a side effect of an ordinary run, so this gate sits above <see cref="E2EFactAttribute"/>
/// rather than folding into it.
/// </summary>
public sealed class DocsCaptureFactAttribute : FactAttribute
{
    public DocsCaptureFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("FORMIDABLE_E2E") != "1"
            || Environment.GetEnvironmentVariable("FORMIDABLE_CAPTURE") != "1")
        {
            Skip = "Set FORMIDABLE_E2E=1 and FORMIDABLE_CAPTURE=1 to (re)capture the docs imagery.";
        }
    }
}

/// <summary>
/// Renders the PNGs under <c>docs/assets</c> from the real workout page instead of by hand: a hero
/// image per colour scheme and a cropped social-preview image, all at fixed viewports so a re-run
/// never drifts the framing. Nothing here asserts anything about the library — these are captures,
/// not regression tests — so they stay behind their own gate on top of FORMIDABLE_E2E, and a plain
/// gated run (the release-verification shape) skips them rather than rewriting the shipped assets
/// as a side effect. Re-run after a visual change to the workout page:
/// <code>FORMIDABLE_E2E=1 FORMIDABLE_CAPTURE=1 dotnet test --filter DocsCapture</code>
/// In PowerShell: <c>$env:FORMIDABLE_E2E = "1"; $env:FORMIDABLE_CAPTURE = "1"; dotnet test --filter
/// DocsCapture</c> — then clear both variables so a later plain run in the same session behaves as
/// a plain run.
/// </summary>
[Collection("e2e")]
public sealed class DocsCapture(SampleAppFixture app)
{
    // Mirrors the origin SampleAppFixture starts the standalone WASM sample on. The fixture keeps
    // that origin private because only NewPageAsync needed it; this class needs a custom viewport
    // and colour scheme per context, which NewPageAsync does not expose, so it opens its own
    // contexts on the fixture's shared browser instead of adding servers of its own.
    private const string WorkoutUrl = "http://localhost:5181/workout";

    // Eleven rows is the cheapest way past the Attendees collection's Warning-severity rule
    // (EventRegistrationValidator.ConfigureDraftRules: more than 10 is a warning, never a
    // block), and each needs a Name — the per-row presence rule sits in the submit bucket, so a
    // blank row would add its own error and spoil the one-error state the hero wants. Real
    // first-and-last names rather than placeholders, since this shot is what a visitor sees first.
    private static readonly string[] HeroAttendeeNames =
    [
        "Maya Chen", "Liam O'Connor", "Priya Natarajan", "Oliver Bennett", "Sofia Rossi",
        "Noah Kim", "Ava Thompson", "Ethan Walsh", "Grace Nguyen", "Lucas Ferreira", "Isla Campbell",
    ];

    [DocsCaptureFact]
    public async Task Hero_light_captures_a_mixed_valid_and_warning_state()
    {
        var path = Path.Combine(AssetsDirectory(), "hero-light.png");
        var page = await OpenWorkoutAsync(ColorScheme.Light);

        await StageMixedValidationAsync(page);
        await FrameForHeroAsync(page);

        await page.ScreenshotAsync(new PageScreenshotOptions { Path = path });
    }

    [DocsCaptureFact]
    public async Task Hero_dark_captures_a_mixed_valid_and_warning_state()
    {
        var path = Path.Combine(AssetsDirectory(), "hero-dark.png");
        var page = await OpenWorkoutAsync(ColorScheme.Dark);

        await StageMixedValidationAsync(page);
        await FrameForHeroAsync(page);

        await page.ScreenshotAsync(new PageScreenshotOptions { Path = path });
    }

    [DocsCaptureFact]
    public async Task Social_preview_clips_the_header_and_summary()
    {
        var path = Path.Combine(AssetsDirectory(), "social-preview.png");
        var page = await OpenWorkoutAsync(ColorScheme.Light);

        await SubmitEmptyRegistrationAsync(page);

        // The teaching panel sits between the page's own header and the form it teaches, easily
        // several viewport heights tall — content a social-preview crop has no room for and no
        // reason to show. Hiding it (this page only, this screenshot only) is what lets the page's
        // "Full workout" header and the validation summary it produced land in the same 640px crop.
        await page.AddStyleTagAsync(new PageAddStyleTagOptions { Content = ".teaching { display: none !important; }" });
        await page.EvaluateAsync("() => window.scrollTo(0, 0)");

        await page.ScreenshotAsync(new PageScreenshotOptions
        {
            Path = path,
            Clip = new Clip { X = 0, Y = 0, Width = 1280, Height = 640 },
        });
    }

    private async Task<IPage> OpenWorkoutAsync(ColorScheme colorScheme)
    {
        var context = await app.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1280, Height = 800 },
            ColorScheme = colorScheme,
        });
        var page = await context.NewPageAsync();
        await page.GotoAsync(WorkoutUrl);

        // Same reasoning as SampleAppFixture.NewPageAsync: a cold WASM boot is far slower than
        // anything that follows it, so the wait here is just as generous.
        await page.WaitForSelectorAsync("nav", new PageWaitForSelectorOptions { Timeout = 30_000 });
        return page;
    }

    // "Empty first submit": load the page and submit with nothing entered. Every presence rule
    // whose field is still empty answers at once and the summary renders — the same state
    // WorkoutFocusAndAsync's blocked-submit test drives, chosen here for the same reason: it is
    // reachable from a fresh page with no fill steps to keep in sync as the form changes.
    private static async Task SubmitEmptyRegistrationAsync(IPage page)
    {
        await page.GetByRole(AriaRole.Button, new() { Name = "Submit registration", Exact = true }).ClickAsync();
        await Expect(Summary(page)).ToBeVisibleAsync(new() { Timeout = AsyncTimeoutMs });
    }

    // Stages the workout page the hero shots want: a realistic conference registration with
    // every required field answered but one, an eleven-attendee list past the Warning-severity
    // threshold, and Dietary notes left blank — its rule is unconditional (ConfigureDraftRules),
    // so it is the cheapest single field that keeps one error and the summary's error band alive
    // once everything else clears. Kept separate from SubmitEmptyRegistrationAsync, which
    // Social_preview still uses unchanged for its own all-red empty-submit crop: the two shots
    // want different states, so they get different staging rather than one helper branching on
    // its caller.
    private static async Task StageMixedValidationAsync(IPage page)
    {
        await Field(page, "contactemail").FillAsync("events@example.com");
        await Field(page, "eventname").FillAsync("Ridgeline Systems Conference");
        await Field(page, "eventdate").FillAsync("2027-03-18");
        await Field(page, "earlybirddeadline").FillAsync("2027-02-01");
        await Field(page, "description").FillAsync("Two-day conference for platform engineers across ANZ.");
        await Field(page, "couponcode").FillAsync("EARLYBIRD");
        await Field(page, "cateringheadcount").FillAsync("120");
        await Field(page, "venueregion").FillAsync("Adelaide, South Australia");
        await TabAsync(page);

        var addAttendee = page.GetByRole(AriaRole.Button, new() { Name = "Add attendee", Exact = true });
        for (var i = 0; i < HeroAttendeeNames.Length; i++)
        {
            await addAttendee.ClickAsync();
            var row = page.Locator(".member-list li").Nth(i);
            await Field(row, "name").FillAsync(HeroAttendeeNames[i]);
        }

        await page.GetByRole(AriaRole.Button, new() { Name = "Submit registration", Exact = true }).ClickAsync();

        // Both waits double as the staging's own proof: the one field left blank is what the
        // error band names, and the eleven-row list is what the warning band names. Waiting on
        // them (rather than a fixed delay) also absorbs the contact email's 300ms availability
        // check and the debounced live passes every fill above started.
        await Expect(SummaryEntry(page, "Dietary notes are required for catering"))
            .ToBeVisibleAsync(new() { Timeout = AsyncTimeoutMs });
        await Expect(SummaryEntry(page, "More than 10 attendees needs approval — submission is not blocked"))
            .ToBeVisibleAsync(new() { Timeout = AsyncTimeoutMs });
    }

    // Frames the hero shots on the nav's brand block and the page header rather than on the page
    // chrome the teaching panel would otherwise push below the fold: same technique
    // Social_preview uses (hide .teaching, scroll to the top), applied here through its own copy
    // so the two capture shapes stay free to diverge without either editing the other's helper.
    private static async Task FrameForHeroAsync(IPage page)
    {
        await page.AddStyleTagAsync(new PageAddStyleTagOptions { Content = ".teaching { display: none !important; }" });
        await page.EvaluateAsync("() => window.scrollTo(0, 0)");
    }

    private static string AssetsDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (directory.EnumerateFiles("*.sln").Any() || directory.EnumerateDirectories(".git").Any())
            {
                var assets = Path.Combine(directory.FullName, "docs", "assets");
                Directory.CreateDirectory(assets);
                return assets;
            }
        }

        throw new InvalidOperationException(
            $"No .sln or .git was found above {AppContext.BaseDirectory} — DocsCapture locates the repository root the same way SampleAppFixture does.");
    }
}
