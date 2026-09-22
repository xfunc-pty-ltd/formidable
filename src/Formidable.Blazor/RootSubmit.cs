namespace Formidable.Blazor;

/// <summary>
/// The submit both shipped roots run, and the one thing neither of them can report honestly on
/// its own: whether the engine the pass ran against is still the engine the root holds.
/// </summary>
internal static class RootSubmit
{
    /// <summary>
    /// The verdict a root reports for a submit whose engine went away under it. Cached because it
    /// carries nothing about the submit that produced it — that is the whole of what it says.
    /// </summary>
    internal static SubmitOutcome Superseded { get; } = new(false, ValidationReport.Empty, []);

    /// <summary>
    /// Runs <paramref name="engine"/>'s submit pipeline and answers for the engine being replaced
    /// or disposed while the pass was still in flight — a <c>Model</c> swap, a reset, a cascaded
    /// <c>EditContext</c> replaced, the root leaving the page.
    /// </summary>
    /// <remarks>
    /// A dead engine's verdict — even a passing one, if its validator outran cancellation — must
    /// not surface as if it were current: it belongs to a submit the swap already abandoned, and
    /// acting on it would route callbacks and move focus over a model nothing is editing any more.
    /// Cancelling the abandoned pass's token is what usually stops it short, which is why this is
    /// a check on the way out rather than a guarantee on the way in. Mirrors
    /// <see cref="FormidableEngine{TModel}"/>'s own precedent for a superseded pass with
    /// nothing to report.
    /// </remarks>
    /// <typeparam name="TModel">The form model type.</typeparam>
    /// <param name="engine">The engine to run the pass against, read once by the caller.</param>
    /// <param name="current">The engine the root holds now, read after the pass has landed.</param>
    /// <returns>The engine's own verdict, or <see langword="null"/> when the engine that produced
    /// it is no longer the root's — the caller's cue to skip everything a live submit does next
    /// and report <see cref="Superseded"/>.</returns>
    internal static async Task<SubmitOutcome?> RunAsync<TModel>(
        FormidableEngine<TModel> engine,
        Func<FormidableEngine<TModel>?> current)
        where TModel : class
    {
        var outcome = await engine.ValidateForSubmitAsync();
        return ReferenceEquals(current(), engine) ? outcome : null;
    }
}
