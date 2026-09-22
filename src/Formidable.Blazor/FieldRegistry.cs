using Microsoft.AspNetCore.Components.Forms;

namespace Formidable.Blazor;

/// <summary>
/// Tracks which fields are currently rendered. Rendered field components and anchors register
/// on initialization and unregister on dispose, making the markup's conditional rendering the
/// source of truth for the submit channel's progressive disclosure: a submit does not reveal a
/// field with no registration, so its submit errors are suppressed as unrevealed. The live
/// channel answers to engagement rather than registration and consults this registry only under
/// <see cref="FormidableOptions.LiveDisclosure"/>'s opt-in policy — with one exception either
/// way, that a field which leaves the page leaves the engaged set with it. Issue order reads it
/// too: the fields still in the render tree are where a host starts when it asks its order
/// service what to place.
/// </summary>
public sealed class FieldRegistry
{
    private readonly Dictionary<FieldIdentifier, int> _counts = [];
    private readonly HashSet<FieldIdentifier> _kept = [];
    private readonly HashSet<FieldIdentifier> _everRegistered = [];
    private int _version;

    /// <summary>
    /// Registers a rendered field. Dispose the returned handle when the field leaves the
    /// render tree. With <paramref name="keepRegistered"/> the field stays registered after
    /// disposal — for containers such as <c>Virtualize</c> that dispose rows scrolled out of
    /// view without the row ceasing to be part of the form. A later registration of the same
    /// field disposed without keepRegistered removes the retained entry — the latest disposal's
    /// intent wins.
    /// </summary>
    /// <remarks>
    /// Calling this directly is supported, and is the route for a field a caller already holds as
    /// a <see cref="FieldIdentifier"/> rather than as an accessor expression — which is the one
    /// shape <see cref="FormidableFieldAnchor{TValue}"/> cannot express. Reach the registry
    /// through <see cref="FormidableFormContext.Registry"/>. What the call buys is the field
    /// counting as rendered, which is read in the places this type's own summary describes: the
    /// submit channel stops suppressing its errors as unrevealed, and under
    /// <see cref="LiveIssueDisclosure.EngagedAndVisible"/> the live channel stops filtering them
    /// out as well. It also records the field as having registered at all, which is what makes it
    /// eligible for the engagement prune when it later leaves, and what quiets
    /// <see cref="FormidableOptions.NeverRegisteredFieldDiagnostic"/> for it. What
    /// it does not buy is the lifecycle <see cref="FormidableComponentBase"/> runs around the
    /// same call for the kit's own components — dispose the handle when the field leaves the
    /// render tree, and take a fresh registration whenever the cascaded
    /// <see cref="FormidableFormContext"/> is a new instance, since a host that swaps its model
    /// rebuilds engine and registry together and the old registry is then one nothing consults.
    /// Registration alone also computes no element id, so a caller wanting a summary click to
    /// reach its control renders <see cref="FormidableFieldId.For(FieldIdentifier)"/> on it.
    /// </remarks>
    public FieldRegistration Register(FieldIdentifier field, bool keepRegistered = false)
    {
        _counts[field] = _counts.TryGetValue(field, out var count) ? count + 1 : 1;
        _everRegistered.Add(field);
        _version++;
        Changed?.Invoke();
        return new FieldRegistration(this, field, keepRegistered);
    }

    /// <summary>True when the field is currently rendered (or retained via keep-registered).</summary>
    public bool IsRegistered(FieldIdentifier field) => _counts.ContainsKey(field) || _kept.Contains(field);

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
    internal IReadOnlyCollection<FieldIdentifier> RegisteredFields => _counts.Keys;

    /// <summary>
    /// True when the field has been registered at least once since the engine was built, whether
    /// or not it is still registered now. Never cleared by an unregister — tells apart a field
    /// that was rendered and later stopped being rendered (unambiguous) from one that has never
    /// rendered at all, which by itself does not distinguish a disclosure Pattern 2 miswiring
    /// from a legitimate Pattern 1 section the user has not opened yet — see
    /// <see cref="FormidableOptions.NeverRegisteredFieldDiagnostic"/>.
    /// </summary>
    internal bool HasEverRegistered(FieldIdentifier field) => _everRegistered.Contains(field);

    /// <summary>
    /// Removes one registration of <paramref name="field"/> — the reverse of one
    /// <see cref="Register"/> call, and where the handle it returns routes its disposal.
    /// Removing the last one takes the field out of the rendered set, with
    /// <paramref name="keepRegistered"/> deciding whether a retained entry keeps
    /// <see cref="IsRegistered"/> answering true or an earlier retention is dropped: the latest
    /// disposal's intent wins. A field with no counted registration — never registered, or
    /// retained only by keep-registered — is left untouched: <see cref="Version"/> does not
    /// move and <see cref="Changed"/> does not fire.
    /// </summary>
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
