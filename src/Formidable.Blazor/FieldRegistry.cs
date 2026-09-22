using Microsoft.AspNetCore.Components.Forms;

namespace Formidable.Blazor;

/// <summary>
/// Tracks which fields are currently rendered. Rendered field components and anchors register
/// on initialization and unregister on dispose, making the markup's conditional rendering the
/// source of truth for progressive disclosure: an issue whose field has no registration is
/// unrevealed and suppressed from inline display.
/// </summary>
public sealed class FieldRegistry
{
    private readonly Dictionary<FieldIdentifier, int> _counts = [];
    private readonly HashSet<FieldIdentifier> _kept = [];
    private readonly HashSet<FieldIdentifier> _everRegistered = [];
    private int _version;

    /// <summary>
    /// Registers a rendered field. Dispose the returned handle when the field leaves the
    /// render tree. With <paramref name="keepRegistered"/> the field stays revealed after
    /// disposal — for containers such as <c>Virtualize</c> that dispose rows scrolled out of
    /// view without the row ceasing to be part of the form. A later registration of the same
    /// field disposed without keepRegistered removes the retained entry — the latest disposal's
    /// intent wins.
    /// </summary>
    public FieldRegistration Register(FieldIdentifier field, bool keepRegistered = false)
    {
        _counts[field] = _counts.TryGetValue(field, out var count) ? count + 1 : 1;
        _everRegistered.Add(field);
        _version++;
        Changed?.Invoke();
        return new FieldRegistration(this, field, keepRegistered);
    }

    /// <summary>True when the field is currently rendered (or retained via keep-registered).</summary>
    public bool IsRevealed(FieldIdentifier field) => _counts.ContainsKey(field) || _kept.Contains(field);

    /// <summary>
    /// Changes whenever a field registers or unregisters. A host polls this to learn whether the
    /// rendered field set moved since it last checked. Paired with <see cref="Changed"/>, which
    /// says only that Version moved, not what it moved to: <see cref="Changed"/> fires from
    /// inside the very batch that is still mutating this counter, so a subscriber that read
    /// Version from inside it would see the same transient, still-settling state a synchronous
    /// read during that batch always risks — the registry's contract is that a subscriber defers
    /// past the batch first (see <see cref="Changed"/>'s own remarks) and only reads Version once
    /// it has, so nothing here re-enters a render that is still in progress.
    /// </summary>
    internal int Version => _version;

    /// <summary>
    /// Fires after <see cref="Register"/> or <see cref="Unregister"/> actually moves
    /// <see cref="Version"/> — synchronously, from inside whatever render batch caused the
    /// registration change (typically a field component's own initialization or disposal). A
    /// subscriber MUST NOT act on the registry from inside this callback: the batch that raised
    /// it may still be mid-diff, so anything read here (including <see cref="Version"/> itself)
    /// is transient. Reacting inline, or through a component's own <c>InvokeAsync</c> (which runs
    /// synchronously, inline, when already on the dispatcher — exactly the case here), does not
    /// defer at all. <see cref="FormidableValidator{TModel}"/> is the reference subscriber: when
    /// <see cref="SynchronizationContext.Current"/> is set (Blazor Server; bUnit resolves the
    /// same shape) it captures and <c>Post</c>s a continuation to it; when nothing is set (Blazor
    /// WebAssembly's default single-threaded runtime, where that is always the case, not a rare
    /// one) it queues one through the thread pool instead. Both genuinely defer past the batch;
    /// see <see cref="FormidableValidator{TModel}.OnFieldRegistryChanged"/> for the two paths.
    /// </summary>
    internal event Action? Changed;

    /// <summary>
    /// The fields currently in the render tree, in no particular order. A field retained only by
    /// keep-registered is absent: it has left the DOM, so there is no element of its own to
    /// locate.
    /// </summary>
    internal IReadOnlyCollection<FieldIdentifier> RevealedFields => _counts.Keys;

    /// <summary>
    /// True when the field has been registered at least once since the engine was built, whether
    /// or not it is still registered now. Never cleared by an unregister — tells apart a field
    /// that was rendered and later stopped being rendered (unambiguous) from one that has never
    /// rendered at all, which by itself does not distinguish a disclosure Pattern 2 miswiring
    /// from a legitimate Pattern 1 section the user has not opened yet — see
    /// <see cref="FormidableOptions.NeverRegisteredFieldDiagnostic"/>.
    /// </summary>
    internal bool HasEverRegistered(FieldIdentifier field) => _everRegistered.Contains(field);

    internal void Unregister(FieldIdentifier field, bool keepRegistered)
    {
        if (!_counts.TryGetValue(field, out var count))
        {
            return;
        }

        _version++;

        if (count <= 1)
        {
            _counts.Remove(field);
            if (keepRegistered)
            {
                _kept.Add(field);
            }
            else
            {
                _kept.Remove(field); // latest disposal intent wins: a non-kept dispose reverses an earlier keep
            }
        }
        else
        {
            _counts[field] = count - 1;
        }

        Changed?.Invoke();
    }
}
