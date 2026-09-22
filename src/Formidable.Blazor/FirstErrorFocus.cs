using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Formidable.Blazor;

/// <summary>
/// Where a blocked submit sends the visitor. Both roots run this: <c>FormidableForm</c>, which
/// owns its submit pipeline, and <c>FormidableValidator</c>, whose page owns the pipeline and
/// asks for the move by hand. One decision site rather than two is the point: a visitor taken to
/// a different field depending on which root the page happens to be built on would be a
/// difference in behaviour that nothing about the two roots asks for.
/// </summary>
internal static class FirstErrorFocus
{
    /// <summary>
    /// Both roots name their fallback parameter this, so the diagnostic below names something a
    /// consumer can search for whichever root produced the miss.
    /// </summary>
    private const string FallbackParameterName = "FocusFallback";

    /// <summary>
    /// Best-effort: a consumer who never registered <see cref="IFormidableFocusService"/> (or
    /// whose form has, unusually, no visible issue to focus right after a blocked submit) gets
    /// silence rather than an exception — the same tolerance <see cref="FormidableSummary"/>'s own
    /// click-to-focus applies.
    /// </summary>
    /// <remarks>
    /// The first ERROR, not merely the first issue: a field ahead of the failing one can carry an
    /// advisory while the thing actually blocking the submit sits behind it, and taking the
    /// visitor to the advisory would both bury the reason and disagree with
    /// <see cref="FormidableSummary"/>, which regroups by severity and so leads with the error
    /// regardless. "First" is whatever order the engine reports its visible issues in, which is
    /// the page's own reading order under a root that resolves one and the engine's channel order
    /// under a root that does not. The fallback to the first visible issue covers the one way a
    /// blocked submit reaches this call with no error to find: superseded by a second submit
    /// before its own verdict landed, it reports blocked without writing one, leaving whatever
    /// preceded it on screen. Everything else that blocks is error-severity — the all-suppressed
    /// gate's form-level issue and the incomplete-validation fault issue included. A miss on the
    /// element itself (no element on the page carries the field's id: a virtualized row outside
    /// the render window, or a control that renders no such id at all) is handled the same way
    /// <see cref="FormidableSummary.FocusFallback"/> handles a click miss: try, fall back once
    /// when a fallback is wired, retry once. With none wired, the miss reports a diagnostic
    /// instead — see <see cref="ReportFallbackMiss"/> — since a blocked submit's visitor otherwise
    /// gets no signal at all that the field they need is out of reach.
    /// </remarks>
    /// <param name="services">The root's own injected provider, for the focus service and the
    /// diagnostic's logger factory — both are optional registrations.</param>
    /// <param name="engine">The engine whose visible issues the choice is made from.</param>
    /// <param name="fallback">The root's <c>FocusFallback</c> parameter, or null when unwired.</param>
    internal static async ValueTask MoveAsync(
        IServiceProvider services,
        IFormValidationEngine engine,
        Func<FieldIdentifier, ValueTask<bool>>? fallback)
    {
        var focusService = services.GetService<IFormidableFocusService>();
        if (focusService is null)
        {
            return;
        }

        var issues = engine.GetVisibleIssues();
        var firstIssue = issues.FirstOrDefault(v => v.Issue.Severity == ValidationSeverity.Error)
            ?? issues.FirstOrDefault();
        if (firstIssue is null)
        {
            return;
        }

        if (await focusService.FocusAsync(firstIssue.Field))
        {
            return;
        }

        if (fallback is null)
        {
            ReportFallbackMiss(services, firstIssue.Issue);
            return;
        }

        if (!await fallback(firstIssue.Field))
        {
            return;
        }

        await focusService.FocusAsync(firstIssue.Field);
    }

    /// <summary>
    /// The one report a focus miss with no fallback to retry through gets: a Trace line for a
    /// debugger, and a logged warning when the host resolved an <see cref="ILoggerFactory"/> —
    /// mirrors <c>FormValidationEngine.ReportSuppressed</c>'s dual channel, minus the
    /// options-callback channel that has no analogue here. Names the fallback parameter so a
    /// consumer's console points straight at the seam that would close the gap.
    /// </summary>
    /// <param name="services">The root's own injected provider; a host with no logger factory
    /// registered gets the Trace line alone.</param>
    /// <param name="issue">The unfocusable first error.</param>
    private static void ReportFallbackMiss(IServiceProvider services, ValidationIssue issue)
    {
        System.Diagnostics.Trace.WriteLine(
            $"Formidable: the blocked submit's first error at '{issue.Path}' has no rendered element to " +
            $"focus, and no {FallbackParameterName} is wired to make it renderable.");
        ((ILoggerFactory?)services.GetService(typeof(ILoggerFactory)))?.CreateLogger("Formidable").LogWarning(
            "Formidable: the blocked submit's first error at '{Path}' has no rendered element to focus, " +
            "and no {Parameter} is wired to make it renderable.",
            issue.Path, FallbackParameterName);
    }
}
