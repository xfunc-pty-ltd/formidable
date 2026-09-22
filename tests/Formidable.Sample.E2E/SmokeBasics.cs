namespace Formidable.Sample.E2E;

/// <summary>The plumbing check: a booted sample renders its nav in a real browser.</summary>
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
}
