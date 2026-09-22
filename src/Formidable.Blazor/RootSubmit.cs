namespace Formidable.Blazor;

/// <summary>The submit path both roots share, which also checks that the engine the pass ran against is still the root's.</summary>
internal static class RootSubmit
{
    /// <summary>The blocked, empty outcome a root returns for a submit whose engine is no longer the one it holds when the pass lands: rebuilt on either root, or torn down by <see cref="FormidableValidator{TModel}"/>'s disposal.</summary>
    // One instance serves every such submit: the outcome carries nothing about the submit that
    // produced it.
    internal static SubmitOutcome Superseded { get; } = new(false, ValidationReport.Empty, []);

    /// <summary>Runs the engine's submit and returns its outcome, or <see langword="null"/> when the root no longer holds that engine.</summary>
    /// <typeparam name="TModel">The form model type.</typeparam>
    /// <param name="engine">The engine to run the submit against.</param>
    /// <param name="current">Reads the engine the root holds, consulted after the submit lands.</param>
    /// <returns>The engine's own outcome, or <see langword="null"/> for the caller to report <see cref="Superseded"/> in its place.</returns>
    internal static async Task<SubmitOutcome?> RunAsync<TModel>(
        FormidableEngine<TModel> engine,
        Func<FormidableEngine<TModel>?> current)
        where TModel : class
    {
        var outcome = await engine.ValidateForSubmitAsync();
        return ReferenceEquals(current(), engine) ? outcome : null;
    }
}
