namespace Formidable.Blazor;

/// <summary>Tells a root's engine that the rendered field set moved, once per registry version, from whichever of the root's render, a posted batch or a direct call reaches it first.</summary>
/// <remarks>
/// Both roots share it. <see cref="FormidableForm{TModel}"/> reconciles in its own
/// <c>OnAfterRenderAsync</c> and posts only for a change no render of the form will reach.
/// <see cref="FormidableValidator{TModel}"/> has no render that reconciles, so it posts for
/// every batch.
/// </remarks>
// Read and written on the renderer's dispatcher only: the registry raises Changed from inside a
// render batch, the post runs back on that dispatcher, and NotifyFieldSetChanged is documented to
// be called from it.
internal sealed class RenderedFieldSetReconciler
{
    private readonly Func<FieldRegistry?> _registry;
    private readonly Action _reconcile;
    private readonly Func<bool> _renderWillReconcile;
    private readonly BatchPost _post;

    // The registry version the last completed reconcile answered; -1 before the first, and again
    // after Reset, so the first change a fresh registry reports always finds something to do.
    private int _reconciledVersion = -1;

    /// <summary>Creates the reconciler over a root's current registry, its engine's reconcile and its render's own reconcile.</summary>
    /// <param name="registry">The current engine's registry, or <see langword="null"/> while the root has no engine to reconcile: none built yet, torn down, or disposed.</param>
    /// <param name="reconcile">The current engine's <see cref="FormidableEngine{TModel}.OnRenderedFieldsChanged"/>.</param>
    /// <param name="renderWillReconcile">Whether a render of the root is still to reach a reconcile of its own, which a post would only duplicate.</param>
    internal RenderedFieldSetReconciler(Func<FieldRegistry?> registry, Action reconcile, Func<bool> renderWillReconcile)
    {
        _registry = registry;
        _reconcile = reconcile;
        _renderWillReconcile = renderWillReconcile;
        _post = new BatchPost(ReconcileIfChanged);
    }

    /// <summary>How many times <see cref="ReconcileIfChanged"/> has run the reconcile rather than skipping on the version, an attempt that threw included.</summary>
    internal int ReconcileCount { get; private set; }

    /// <summary>How many reconciles <see cref="OnRegistryChanged"/> has posted: one per batch of registry changes that no render of the root reaches.</summary>
    internal int PostCount => _post.PostCount;

    /// <summary>The handler for the registry's <see cref="FieldRegistry.Changed"/>: posts one reconcile per batch, unless a render of the root will reconcile first.</summary>
    // Changed fires from inside the batch, while the registry is still moving, so the reconcile
    // waits for the post. Every change the batch makes while that post waits is answered by it,
    // because the post reads the registry's version as it runs, not as it was requested.
    internal void OnRegistryChanged()
    {
        if (_renderWillReconcile())
        {
            return;
        }

        _post.Request();
    }

    /// <summary>Runs the reconcile when the registry's version has moved since the last completed one, and nothing while the root has no engine.</summary>
    // The version is recorded only once the reconcile has returned. The reconcile can rebuild the
    // message store, which raises EditContext.OnValidationStateChanged and the engine's
    // StateChanged into a consumer's own handlers. A throw from one leaves the version behind the
    // registry, so the next render, post or direct call runs the reconcile again, with the
    // re-check it arms. The version is read before the call, so a registry change the reconcile
    // itself causes still finds a later reconcile with something to do.
    internal void ReconcileIfChanged()
    {
        var registry = _registry();
        if (registry is null)
        {
            return;
        }

        var version = registry.Version;
        if (version == _reconciledVersion)
        {
            return;
        }

        ReconcileCount++;
        _reconcile();
        _reconciledVersion = version;
    }

    /// <summary>Forgets the last version reconciled, because a rebuilt engine's registry counts from zero and could match it.</summary>
    internal void Reset() => _reconciledVersion = -1;
}
