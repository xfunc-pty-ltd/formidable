using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Formidable.Blazor;

/// <summary>The one helper every focus move goes through: <see cref="MoveAsync"/> chooses the field from the visible issues, and <see cref="TryFocusAsync"/> lands a chosen field, a summary click's included.</summary>
// One decision site rather than one per caller: a visitor taken to a different field depending
// on which root the page is built on, or on whether the move was automatic or asked for, would
// be a difference in behaviour that nothing about those callers asks for.
internal static class FirstErrorFocus
{
    /// <summary>The name of both roots' fallback parameter, as the miss diagnostic prints it.</summary>
    private const string FallbackParameterName = "FocusFallback";

    /// <summary>Moves focus to the first visible error, or to the first visible issue when no error shows, and reports whether an element took it.</summary>
    /// <param name="services">The root's provider, for the optional <see cref="IFormidableFocusService"/> and the diagnostic's logger.</param>
    /// <param name="engine">The engine whose visible issues the field is chosen from.</param>
    /// <param name="fallback">The root's <c>FocusFallback</c> parameter, or <see langword="null"/> when unwired.</param>
    /// <param name="prepare">The root's <c>PrepareFocus</c> parameter, or <see langword="null"/> when unwired.</param>
    /// <returns><see langword="true"/> when an element took focus; <see langword="false"/> with no focus service registered, no visible issue, or a miss no fallback recovered.</returns>
    /// <remarks>
    /// "First" is the order <see cref="IFormidableEngine.GetVisibleIssues"/> reports. A miss with
    /// no <paramref name="fallback"/> wired writes the diagnostic <see cref="ReportFallbackMiss"/>
    /// describes; a miss with one wired reports nothing.
    /// </remarks>
    internal static async ValueTask<bool> MoveAsync(
        IServiceProvider services,
        IFormidableEngine engine,
        Func<FieldIdentifier, ValueTask<bool>>? fallback,
        Func<FieldIdentifier, ValueTask>? prepare)
    {
        // The early returns sit above the prepare call, so nothing prepares for a move that is
        // not about to happen.
        var focusService = services.GetService<IFormidableFocusService>();
        if (focusService is null)
        {
            return false;
        }

        // The first error rather than the first issue: a field ahead of the failing one can carry
        // an advisory while the error blocking the submit sits behind it, and the summary, which
        // regroups by severity, leads with the error regardless.
        var issues = engine.GetVisibleIssues();
        var firstIssue = issues.FirstOrDefault(v => v.Issue.Severity == ValidationSeverity.Error)
            ?? issues.FirstOrDefault();
        if (firstIssue is null)
        {
            return false;
        }

        var took = await TryFocusAsync(focusService, firstIssue.Field, prepare, fallback);
        if (!took && fallback is null)
        {
            ReportFallbackMiss(services, firstIssue.Issue);
        }

        return took;
    }

    /// <summary>Awaits <paramref name="prepare"/> once, focuses the field's element, and retries once after a miss when a wired <paramref name="fallback"/> answers <see langword="true"/>.</summary>
    /// <param name="focusService">The service that addresses the element.</param>
    /// <param name="field">The field to land on, chosen by the caller.</param>
    /// <param name="prepare">The page's <c>PrepareFocus</c> callback, or <see langword="null"/> when unwired; awaited once, before the first attempt.</param>
    /// <param name="fallback">The page's <c>FocusFallback</c> callback, or <see langword="null"/> when unwired; consulted once, after a miss.</param>
    /// <returns><see langword="true"/> when the first attempt or the retry landed; <see langword="false"/> with no fallback, a fallback that declined, or a retry that missed again.</returns>
    // Written once for both callers: a page that wires one PrepareFocus or FocusFallback callback
    // to a root and to a summary at once gets one move out of both.
    internal static async ValueTask<bool> TryFocusAsync(
        IFormidableFocusService focusService,
        FieldIdentifier field,
        Func<FieldIdentifier, ValueTask>? prepare,
        Func<FieldIdentifier, ValueTask<bool>>? fallback)
    {
        // Awaited here and nowhere else, below whatever the caller did to decide there is a move
        // to make, and not again before the retry: a callback with a side effect as visible as
        // closing a dialog must not run twice for one move.
        if (prepare is not null)
        {
            await prepare(field);
        }

        if (await focusService.FocusAsync(field))
        {
            return true;
        }

        // A retry into an element the page has just declined to make reachable would only miss
        // again.
        if (fallback is null || !await fallback(field))
        {
            return false;
        }

        return await focusService.FocusAsync(field);
    }

    /// <summary>Writes the focus-miss diagnostic, naming the fallback parameter: a Trace line, and a logged warning when the host has an <see cref="ILoggerFactory"/>.</summary>
    /// <param name="services">The root's provider; without an <see cref="ILoggerFactory"/> the Trace line is the whole report.</param>
    /// <param name="issue">The issue whose field the move could not focus.</param>
    private static void ReportFallbackMiss(IServiceProvider services, ValidationIssue issue)
    {
        var path = DiagnosticPathSanitizer.ForDiagnostic(issue.Path);
        FormidableDiagnostics.Warn(
            FormidableEngineFactory.ResolveLogger(services),
            $"Formidable: the field a focus move aimed at, '{path}', did not take focus: " +
            "either nothing renders its id or the element that does will not accept focus, and no " +
            $"{FallbackParameterName} is wired to make it reachable.",
            "Formidable: the field a focus move aimed at, '{Path}', did not take focus: either " +
            "nothing renders its id or the element that does will not accept focus, and no " +
            "{Parameter} is wired to make it reachable.",
            path, FallbackParameterName);
    }
}
