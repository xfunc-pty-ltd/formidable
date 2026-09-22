namespace Formidable.Blazor;

/// <summary>One root's registration with the displaced-click guard: the context it was established for and the key the script holds it under.</summary>
// Both roots ask the library's own script to guard one element and release it again, on the same
// terms and in the same order, so the sequence lives here rather than twice. The two things held
// here are tracked apart from the layout observer because the guard is established on its own
// terms: once per context, needing nothing but the script, where an observer is reached only
// from an ordering path and a root with no IFormidableFieldOrderService registered never
// establishes one. What differs between the roots stays with them: which element each names,
// and what each does when the script answers that it found nothing to scope to.
internal sealed class ClickRecoveryGuard
{
    private FormidableFormContext? _context;
    private string _rootId = string.Empty;

    /// <summary>Establishes the guard for the root's current context once, first releasing whatever key the previous context registered.</summary>
    /// <param name="context">The root's current form context; a new instance re-establishes the guard.</param>
    /// <param name="rootId">The element id to scope the guard to: the model-level field's rendered id.</param>
    /// <param name="options">The engine's options, read for <see cref="FormidableOptions.ClickRecovery"/>.</param>
    /// <param name="registry">The engine's registry, whose registered fields the script walks up from to a <c>&lt;form&gt;</c> when the id names nothing it can take.</param>
    /// <param name="module">Resolves the root's script module on demand; <see langword="null"/> when the host has no <see cref="Microsoft.JSInterop.IJSRuntime"/>.</param>
    /// <param name="disposed">Whether the calling component has begun tearing down; read before and after the script call.</param>
    /// <returns><see langword="true"/> when the script scoped the guard, <see langword="false"/> when it found nothing to scope to, and <see langword="null"/> when nothing was asked: the context is already established, there is no script, <see cref="FormidableOptions.ClickRecovery"/> asked for no guard, the component is disposing, or the interop boundary failed.</returns>
    /// <remarks>
    /// Attempted once per context and never retried, so a host whose script fails to import on
    /// the first interactive render is left without the guard.
    /// </remarks>
    // Retrying every render would buy an interop round trip per render for a module that is
    // never going to arrive: a script that cannot be imported on the first interactive render
    // describes a host that has no script rather than a transient failure. While the context it
    // was established for is still the one rendering, the whole call costs one reference
    // comparison.
    internal async Task<bool?> EstablishAsync(
        FormidableFormContext? context,
        string rootId,
        FormidableOptions options,
        FieldRegistry registry,
        Func<FormidableJsModule?> module,
        Func<bool> disposed)
    {
        if (ReferenceEquals(_context, context))
        {
            return null;
        }

        _context = context;

        // Read before anything is awaited: a rebuilt root renders a fresh element under a fresh
        // id, so the guard the previous context installed has to be released by the key it was
        // registered under rather than by the one about to replace it.
        var releasing = _rootId;
        _rootId = string.Empty;

        var jsModule = module();
        if (jsModule is null)
        {
            return null;
        }

        bool registered;
        try
        {
            await ReleaseAsync(jsModule, releasing);

            if (options.ClickRecovery != DisplacedClickRecovery.Buttons)
            {
                return null;
            }

            // Checked immediately before the call, not only after it: a root torn down while the
            // release above was awaited has already run its own release and found the key empty,
            // so an entry registered at this point would be one nothing ever takes back, leaving
            // the script holding a detached element, and the document listeners it refcounts, for
            // the life of the document.
            if (disposed())
            {
                return null;
            }

            // The script's fallback candidates, built only once the option has asked for a guard:
            // every field something has registered, for it to walk up from to a <form>.
            var fieldIds = registry.RegisteredFields.Select(FormidableFieldId.For).ToArray();
            registered = await jsModule.InvokeAsync<bool>("registerClickRecovery", rootId, fieldIds);
        }
        catch
        {
            // Prerender, a host carrying no script at all, a test double standing in for the
            // module: with no script there is no guard, and a displaced click is lost the way the
            // browser left it. Written wide open because behind this call is the library's own
            // script, reached through the library's own module, with no consumer code anywhere in
            // it — so there is no implementation bug to preserve for someone to see, and every
            // reason not to take a working form down over a feature it can do without.
            return null;
        }

        if (disposed())
        {
            // Torn down while the round trip above was in flight. There is nothing left here to
            // undo the registration with — the root has already let its module go — and recording
            // the key would only leave it on a component nothing reads again.
            return null;
        }

        if (!registered)
        {
            return false;
        }

        _rootId = rootId;
        return true;
    }

    /// <summary>Hands back the registered key for the caller to release and forgets the context it belonged to.</summary>
    /// <returns>The key, or an empty string when nothing is registered.</returns>
    internal string Take()
    {
        var releasing = _rootId;
        _rootId = string.Empty;
        _context = null;
        return releasing;
    }

    /// <summary>Asks the script to release the guard registered under <paramref name="releasing"/>; an empty key releases nothing.</summary>
    /// <param name="module">The module the registration was made through.</param>
    /// <param name="releasing">The key it was registered under, or an empty string.</param>
    /// <returns>A task that completes when the script has answered, or at once for an empty key.</returns>
    // Worth doing on its own account: the module is loaded once per document and outlives the
    // component that registered through it, so an entry left behind scopes the guard to an
    // element no longer in the document and keeps the document-level listeners it refcounts
    // installed over a page with nothing left to guard.
    internal static async Task ReleaseAsync(FormidableJsModule module, string releasing)
    {
        if (releasing.Length > 0)
        {
            await module.InvokeVoidAsync("releaseClickRecovery", releasing);
        }
    }
}
