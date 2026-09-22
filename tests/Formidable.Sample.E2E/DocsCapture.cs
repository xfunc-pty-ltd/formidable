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

    // How far below the top of the viewport the summary lands for the hero shots: enough to keep
    // it clear of the very edge, small enough that the field messages below it fill the rest of
    // the 800px frame — the whole point of the shot. A double, not a float: EvaluateAsync's
    // argument serializer does not round-trip System.Single, so a float argument silently reaches
    // the page as undefined and turns the arithmetic below into NaN.
    private const double SummaryTopMargin = 40d;

    [DocsCaptureFact]
    public async Task Hero_light_captures_the_empty_first_submit()
    {
        var path = Path.Combine(AssetsDirectory(), "hero-light.png");
        var page = await OpenWorkoutAsync(ColorScheme.Light);

        await SubmitEmptyRegistrationAsync(page);
        await ScrollSummaryNearTopAsync(page);

        await page.ScreenshotAsync(new PageScreenshotOptions { Path = path });
    }

    [DocsCaptureFact]
    public async Task Hero_dark_captures_the_empty_first_submit()
    {
        var path = Path.Combine(AssetsDirectory(), "hero-dark.png");
        var page = await OpenWorkoutAsync(ColorScheme.Dark);

        await SubmitEmptyRegistrationAsync(page);
        await ScrollSummaryNearTopAsync(page);

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

    // Scrolls the summary to just below the top of the viewport so the hero shots frame the
    // form's error states rather than the page chrome above them. The margin is a double, not a
    // float: EvaluateAsync's argument serializer does not round-trip System.Single — a float
    // argument silently reaches the page as undefined, so "rect.top - margin" becomes NaN and
    // window.scrollBy(0, NaN) is a no-op the caller never learns about.
    private static Task ScrollSummaryNearTopAsync(IPage page) =>
        page.EvaluateAsync(
            """
            margin => {
                const rect = document.querySelector('.formidable-summary').getBoundingClientRect();
                window.scrollBy(0, rect.top - margin);
            }
            """,
            SummaryTopMargin);

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
