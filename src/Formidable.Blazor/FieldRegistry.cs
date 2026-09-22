using Microsoft.AspNetCore.Components.Forms;

namespace Formidable.Blazor;

/// <summary>The fields currently on the page, registered by the components that render them and read at submit to decide which errors may show.</summary>
/// <remarks>
/// At submit, which errors show is decided by what is on screen at that moment: a field nothing
/// registered discloses no submit error. The live channel shows an engaged field's messages
/// whether or not anything renders it, unless <see cref="FormidableOptions.LiveDisclosure"/> is
/// <see cref="LiveIssueDisclosure.EngagedAndVisible"/>; under either setting, a field that leaves
/// the page stops showing live messages.
/// </remarks>
public sealed class FieldRegistry
{
    private readonly Dictionary<FieldIdentifier, int> _counts = [];
    private readonly HashSet<FieldIdentifier> _kept = [];
    private readonly HashSet<FieldIdentifier> _everRegistered = [];
    private int _version;

    /// <summary>Registers a rendered field and returns the handle whose disposal ends the registration.</summary>
    /// <param name="field">The field being rendered.</param>
    /// <param name="keepRegistered"><see langword="true"/> keeps the field registered after the handle is disposed, for a container such as <c>Virtualize</c> that disposes rows still in the form.</param>
    /// <returns>The handle to dispose when the field leaves the page.</returns>
    /// <remarks>
    /// Calling this directly is supported for a field held as a <see cref="FieldIdentifier"/>,
    /// reached through <see cref="FormidableFormContext.Registry"/>. A registration belongs to
    /// one engine: take a new one whenever the cascaded <see cref="FormidableFormContext"/> is a
    /// different instance. A later registration of the same field disposed without
    /// <paramref name="keepRegistered"/> drops the retained entry. Registering computes no
    /// element id, so render <see cref="FormidableFieldId.For(FieldIdentifier)"/> on the control
    /// for a summary click to reach it.
    /// </remarks>
    public FieldRegistration Register(FieldIdentifier field, bool keepRegistered = false)
    {
        _counts[field] = _counts.TryGetValue(field, out var count) ? count + 1 : 1;
        _everRegistered.Add(field);
        _version++;
        Changed?.Invoke();
        return new FieldRegistration(this, field, keepRegistered);
    }

    /// <summary>Whether the field is currently rendered, or retained by a handle disposed with <c>keepRegistered</c>.</summary>
    /// <param name="field">The field to look up.</param>
    /// <returns><see langword="true"/> while the field counts as registered.</returns>
    public bool IsRegistered(FieldIdentifier field) => _counts.ContainsKey(field) || _kept.Contains(field);

    /// <summary>A counter that moves whenever a registration is added or removed, for a host to compare against the value it last acted on.</summary>
    /// <remarks>
    /// Read it only after deferring past the render batch that raised <see cref="Changed"/>: that
    /// event fires while the batch is still moving this counter, so a read inside it sees a value
    /// still settling.
    /// </remarks>
    internal int Version => _version;

    /// <summary>Raised synchronously whenever a registration changes, from inside the render batch that changed it.</summary>
    /// <remarks>
    /// Do not read the registry inside the handler, and do not defer through a component's own
    /// <c>InvokeAsync</c>, which runs inline when the caller is already on the dispatcher. Post
    /// past the batch first, as <see cref="FormidableValidator{TModel}"/> does: through the
    /// current <see cref="SynchronizationContext"/> where one exists, else through the thread pool.
    /// </remarks>
    internal event Action? Changed;

    /// <summary>The fields currently in the render tree, in no order; a field retained only by <c>keepRegistered</c> is absent.</summary>
    // A kept field has left the DOM, so there is no element of its own for an order service to
    // locate; listing it would only ask the browser about an id that renders nowhere.
    internal IReadOnlyCollection<FieldIdentifier> RegisteredFields => _counts.Keys;

    /// <summary>Whether the field has registered at least once, whether or not it still is.</summary>
    /// <param name="field">The field to look up.</param>
    /// <returns><see langword="true"/> once any registration of the field has happened; an unregistration never clears it.</returns>
    // Never cleared, because it tells a field that rendered and later left (unambiguous) apart
    // from one that has never rendered at all, which by itself does not distinguish a miswired
    // field from a section the visitor has not opened yet: see
    // FormidableOptions.NeverRegisteredFieldDiagnostic.
    internal bool HasEverRegistered(FieldIdentifier field) => _everRegistered.Contains(field);

    /// <summary>Removes one registration of the field, the last one keeping or dropping the field by <paramref name="keepRegistered"/>; a field with no counted registration is left untouched.</summary>
    /// <param name="field">The field one registration of which ends.</param>
    /// <param name="keepRegistered"><see langword="true"/> retains the field as registered once its last registration ends; <see langword="false"/> also drops an earlier retention.</param>
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
