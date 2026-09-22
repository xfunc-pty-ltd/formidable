using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Formidable.Blazor;

/// <summary>
/// Where a blocked submit sends the visitor. Both roots run this: <c>FormidableForm</c>, which
/// owns its submit pipeline, and <c>FormidableValidator</c>, whose page owns the pipeline and
/// asks for the move by hand. Each also exposes it as <c>FocusFirstErrorAsync</c>, so a page that
/// took the announcement over — a dialog, a banner — can ask for the same move at the moment it
/// hands the form back. One decision site rather than one per caller is the point: a visitor taken
/// to a different field depending on which root the page happens to be built on, or on whether the
/// move was automatic or asked for, would be a difference in behaviour that nothing about those
/// callers asks for.
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
    /// whose form has no visible issue to focus at the moment the move is asked for) gets
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
    /// under a root that does not. The fallback to the first visible issue covers a call that
    /// finds no error to land on while some other issue is still on screen. From a blocked submit
    /// that means the submit was superseded before its own verdict landed: it reports blocked
    /// without writing one, leaving whatever preceded it on screen, and anything a caller starts
    /// and awaits can be what supersedes it — a second submit, or the pass
    /// <see cref="IFormValidationEngine.DiscloseLoadedValuesAsync"/> runs. Every other way a
    /// submit blocks writes an error — the all-suppressed gate's form-level issue and the
    /// incomplete-validation fault issue included. A page asking for the move itself reaches the
    /// same state by simpler routes, since it chooses the moment: the errors were fixed while its
    /// dialog was open, or the form carried nothing worse than advisories to begin with. A miss on
    /// the element itself (no element on the page carries the field's id: a virtualized row
    /// outside the render window, or a control that renders no such id at all) is handled the same
    /// way <see cref="FormidableSummary.FocusFallback"/> handles a click miss: try, fall back once
    /// when a fallback is wired, retry once. With none wired, the miss reports a diagnostic
    /// instead — see <see cref="ReportFallbackMiss"/> — since the visitor otherwise gets no
    /// signal at all that the field they need is out of reach.
    /// </remarks>
    /// <param name="services">The root's own injected provider, for the focus service and the
    /// diagnostic's logger factory — both are optional registrations.</param>
    /// <param name="engine">The engine whose visible issues the choice is made from.</param>
    /// <param name="fallback">The root's <c>FocusFallback</c> parameter, or null when unwired.</param>
    /// <param name="prepare">The root's <c>PrepareFocus</c> parameter, or null when unwired.
    /// Awaited once the field is chosen and before the first attempt on it, so a page that has to
    /// clear something out of the way — a modal over the field, a collapsed section around it —
    /// finishes doing that first. It is deliberately not awaited again before the fallback's
    /// retry: the preparation was for this move, and a callback with a side effect as visible as
    /// closing a dialog must not run twice for one of them. Both the early returns above it stay
    /// early returns, so nothing prepares for a move that is not about to happen.</param>
    /// <returns><see langword="true"/> when an element took focus, and <see langword="false"/>
    /// when nothing did: no <see cref="IFormidableFocusService"/> is registered, the engine
    /// reports no visible issue to choose from, or the chosen field's element could not be
    /// focused — no fallback wired, a fallback that declined, or a retry that missed again.
    /// The roots' <c>FocusFirstErrorAsync</c> surfaces the answer, since the page that asked for
    /// the move may have somewhere else to send the visitor; the paths that move focus of their
    /// own accord discard it, having no second move to fall to.</returns>
    internal static async ValueTask<bool> MoveAsync(
        IServiceProvider services,
        IFormValidationEngine engine,
        Func<FieldIdentifier, ValueTask<bool>>? fallback,
        Func<FieldIdentifier, ValueTask>? prepare)
    {
        var focusService = services.GetService<IFormidableFocusService>();
        if (focusService is null)
        {
            return false;
        }

        var issues = engine.GetVisibleIssues();
        var firstIssue = issues.FirstOrDefault(v => v.Issue.Severity == ValidationSeverity.Error)
            ?? issues.FirstOrDefault();
        if (firstIssue is null)
        {
            return false;
        }

        if (prepare is not null)
        {
            await prepare(firstIssue.Field);
        }

        if (await focusService.FocusAsync(firstIssue.Field))
        {
            return true;
        }

        if (fallback is null)
        {
            ReportFallbackMiss(services, firstIssue.Issue);
            return false;
        }

        if (!await fallback(firstIssue.Field))
        {
            return false;
        }

        return await focusService.FocusAsync(firstIssue.Field);
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
    /// <param name="issue">The issue whose field the move aimed at and could not focus: the
    /// first error, or the first visible issue of any severity when the move found no
    /// error.</param>
    private static void ReportFallbackMiss(IServiceProvider services, ValidationIssue issue)
    {
        System.Diagnostics.Trace.WriteLine(
            $"Formidable: the field a focus move aimed at, '{issue.Path}', has no rendered " +
            $"element to focus, and no {FallbackParameterName} is wired to make it renderable.");
        ((ILoggerFactory?)services.GetService(typeof(ILoggerFactory)))?.CreateLogger("Formidable").LogWarning(
            "Formidable: the field a focus move aimed at, '{Path}', has no rendered element to " +
            "focus, and no {Parameter} is wired to make it renderable.",
            issue.Path, FallbackParameterName);
    }
}
