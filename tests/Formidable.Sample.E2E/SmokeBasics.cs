using static Microsoft.Playwright.Assertions;

namespace Formidable.Sample.E2E;

/// <summary>The plumbing checks: a booted sample renders its nav in a real browser, and the splash
/// shown while it boots turns its spinner.</summary>
[Collection("e2e")]
public sealed class SmokeBasics(SampleAppFixture app)
{
    [E2EFact]
    public async Task Home_page_renders_the_nav()
    {
        await using var session = await app.NewPageAsync("/");
        var page = session.Page;

        var nav = await page.Locator("nav").InnerTextAsync();

        Assert.Contains("Full workout", nav, StringComparison.Ordinal);
    }

    // Property: the splash a visitor sees while the runtime downloads shows a spinner that turns.
    // Aborting the runtime's script keeps the splash on screen to be read; the spinner's
    // formidable-spin animation must exist, run, and carry a full turn in its keyframes. With no
    // @keyframes rule of that name, Chromium lists no such animation at all and the ring stands
    // still, so the name check alone fails there; the keyframes are read so that the full turn is
    // pinned too. The context asks for reduced motion, and the spinner turns there too: no
    // reduced-motion rule stops it.
    //
    // Mutations that must break this: removing the @keyframes formidable-spin rule from the
    // sample's app.css; removing the animation declaration from the .boot-spinner rule; turning
    // the keyframes' full turn into a half turn (rotate(180deg)).
    [E2EFact]
    public async Task The_loading_splash_shows_a_turning_spinner()
    {
        await using var session = await app.NewPageAsync("/");
        var page = session.Page;

        await page.RouteAsync("**/_framework/blazor.webassembly*", route => route.AbortAsync());
        await page.ReloadAsync();
        await Expect(page.Locator(".boot-spinner")).ToBeVisibleAsync();

        var spins = await page.EvaluateAsync<SpinAnimation[]>("""
            () => document.getAnimations()
                .filter(a => a.animationName === "formidable-spin")
                .map(a => ({
                    OnSplash: a.effect?.target?.classList.contains("boot-spinner") === true,
                    PlayState: a.playState,
                    Transforms: (a.effect?.getKeyframes() ?? []).map(k => k.transform ?? "").join(" | ")
                }))
            """);

        var spin = Assert.Single(spins, s => s.OnSplash);
        Assert.Equal("running", spin.PlayState);
        Assert.Contains("rotate(360deg)", spin.Transforms, StringComparison.Ordinal);
    }

    // Playwright fills a result object through a parameterless constructor and its setters.
    private sealed class SpinAnimation
    {
        public bool OnSplash { get; set; }

        public string PlayState { get; set; } = "";

        public string Transforms { get; set; } = "";
    }
}
