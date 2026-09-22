using Microsoft.Playwright;

namespace Formidable.Sample.E2E;

/// <summary>
/// The locator vocabulary the browser tests share, expressed in the library's own observable
/// shapes: the <c>formidable-*</c> classes, the deterministic element ids, and the message
/// literals the shipped validators write. An element id embeds a hash of the owning model
/// instance, so a field is always addressed by the tail of its id — never by a spelled-out id,
/// and never by position.
/// </summary>
internal static class SamplePage
{
    /// <summary>
    /// Room for a simulated server call (600 ms on /async, 300 ms on /workout), the round trip to
    /// the sample API behind it, and the validation pass it feeds, on top of a cold context's
    /// WASM boot.
    /// </summary>
    public const int AsyncTimeoutMs = 15_000;

    /// <summary>The form-wide issue summary, rendered only while the form has something to show.</summary>
    public static ILocator Summary(IPage page) => page.Locator(".formidable-summary");

    /// <summary>A summary entry, addressed by the message it carries — the only thing a reader of
    /// the summary can see, and what they would click.</summary>
    public static ILocator SummaryEntry(IPage page, string message) =>
        Summary(page).GetByRole(AriaRole.Button, new() { Name = message, Exact = true });

    /// <summary>A field's element, addressed by the tail of the id the kit gives it.</summary>
    public static ILocator Field(IPage page, string field) => page.Locator($"[id$='-{field}']");

    /// <summary>The same field, inside one collection row. Every row of a collection renders the
    /// same field names, so the row — located by content of its own — is what tells them apart.</summary>
    public static ILocator Field(ILocator row, string field) => row.Locator($"[id$='-{field}']");

    /// <summary>A field's message list, addressed the way the kit builds it: the field's element
    /// id with "-messages" appended.</summary>
    public static ILocator MessagesFor(IPage page, string field) =>
        page.Locator($"ul[id$='-{field}-messages'] .formidable-message");

    /// <summary>The same message list, inside one collection row (see <see cref="Field(ILocator, string)"/>).</summary>
    public static ILocator MessagesFor(ILocator row, string field) =>
        row.Locator($"ul[id$='-{field}-messages'] .formidable-message");
}
