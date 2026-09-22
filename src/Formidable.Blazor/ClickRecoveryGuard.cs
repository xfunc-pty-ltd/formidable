namespace Formidable.Blazor;

/// <summary>
/// One root's registration with the displaced-click guard: which context it was established for,
/// and the key the script holds it under. Both shipped roots ask the library's own script to
/// guard one element and release it again, on the same terms and in the same order, so the
/// sequence lives here rather than twice.
/// </summary>
/// <remarks>
/// The two things it holds are tracked apart from whatever else a root puts in the browser,
/// because the guard is established on its own terms: it is asked for once per context and needs
/// nothing but the script, where a layout observer is reached only from an ordering path and a
/// root with no <see cref="IFormidableFieldOrderService"/> registered never establishes one at
/// all. The key is what a rebuild has to release by — it moves with the model, so the old
/// registration has to be given up by the value it was made with rather than by the one replacing
/// it.
/// <para>
/// What stays with the callers is what genuinely differs between the roots: which element each
/// one names, and what each does when the script answers that it found nothing to scope to. A
/// root that renders its own <c>&lt;form&gt;</c> has an answer whatever that form currently holds;
/// a root that renders no element of its own is the one that can come up empty and has to say so.
/// </para>
/// </remarks>
internal sealed class ClickRecoveryGuard
{
    private FormidableFormContext? _context;
    private string _rootId = string.Empty;

    /// <summary>
    /// Establishes the guard for <paramref name="context"/>, releasing whatever the previous
    /// context registered first.
    /// </summary>
    /// <remarks>
    /// Attempted once per context rather than retried. A script that cannot be imported on the
    /// first interactive render describes a host that has no script rather than a transient
    /// failure, and retrying every render afterwards would buy an interop round trip per render
    /// for a module that is never going to arrive. A root left without the guard loses a displaced
    /// click exactly as it did before there was one, and nothing else about it changes. While the
    /// context it was established for is still the one rendering, the whole call costs one
    /// reference comparison.
    /// </remarks>
    /// <param name="context">The root's current form context, which is the gate: a rebuild
    /// replaces it, and that is what re-establishes the guard.</param>
    /// <param name="rootId">The element id to scope the guard to — the model-level field's
    /// rendered id, which the root caches alongside the engine it derives from.</param>
    /// <param name="options">The engine's options, read for
    /// <see cref="FormidableOptions.ClickRecovery"/>.</param>
    /// <param name="registry">The engine's registry, whose registered fields are the script's
    /// fallback candidates: elements for it to walk up from to a <c>&lt;form&gt;</c>.</param>
    /// <param name="module">The caller's own script module, resolved lazily because the root owns
    /// it — a root may reach the same module for other work — and null when the host has no
    /// <see cref="Microsoft.JSInterop.IJSRuntime"/> at all.</param>
    /// <param name="disposed">Whether the calling component has begun tearing down. Read twice:
    /// a root torn down while this was still awaiting has already run its release, and that
    /// release found the key below still empty — so an entry registered afterwards is one nothing
    /// will ever take back, leaving the script holding a detached element, and the document
    /// listeners it refcounts, for the life of the document.</param>
    /// <returns>What the script answered — <see langword="true"/> when it found an element to
    /// scope the guard to, <see langword="false"/> when it did not — or <see langword="null"/>
    /// when no answer was asked for at all: the guard is already established for this context,
    /// the host has no script, <see cref="FormidableOptions.ClickRecovery"/> asked for no guard,
    /// the component is being disposed, or the interop boundary failed.</returns>
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

            // Checked immediately before the call, not only after it — see the parameter's own
            // remarks for what an entry registered past a release would leave behind.
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

    /// <summary>
    /// Gives up the registered key and forgets the context it belonged to, handing the caller the
    /// key to release through a module the caller still holds. Answers an empty string when there
    /// is nothing registered.
    /// </summary>
    internal string Take()
    {
        var releasing = _rootId;
        _rootId = string.Empty;
        _context = null;
        return releasing;
    }

    /// <summary>
    /// Asks the script to drop a registered root. Worth doing on its own account: the script
    /// module outlives the component that registered through it — a module is loaded once per
    /// document and stays — so an entry left behind scopes the guard to an element no longer in
    /// the document, and the document-level listeners it refcounts stay installed over a page with
    /// nothing left to guard.
    /// </summary>
    /// <param name="module">The module the registration was made through.</param>
    /// <param name="releasing">The key it was registered under, or an empty string for nothing to
    /// release.</param>
    internal static async Task ReleaseAsync(FormidableJsModule module, string releasing)
    {
        if (releasing.Length > 0)
        {
            await module.InvokeVoidAsync("releaseClickRecovery", releasing);
        }
    }
}
