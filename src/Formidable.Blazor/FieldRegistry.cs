using Microsoft.AspNetCore.Components.Forms;

namespace Formidable.Blazor;

/// <summary>The fields currently on the page, registered by the components that render them and read at submit to decide which errors may show.</summary>
/// <remarks>
/// At submit, which errors show is decided by what is on screen at that moment: a field nothing
/// registered discloses no submit error. The live channel shows an engaged field's messages
/// whether or not anything renders it, unless <see cref="FormidableOptions.LiveDisclosure"/> is
/// <see cref="LiveIssueDisclosure.EngagedAndVisible"/>; under either setting, a field that leaves
/// the page stops showing live messages. A field a component renders with <c>WaitForSubmit</c>
/// shows none until a submit or server reply answers.
/// </remarks>
public sealed class FieldRegistry
{
    // One entry per field, standing while a component is registered for it or a keep-registered
    // retention remains: the count, the hold and the retention live in one record, so they
    // cannot drift apart.
    private readonly Dictionary<FieldIdentifier, FieldEntry> _entries = [];
    private readonly HashSet<FieldIdentifier> _everRegistered = [];

    // How many fields IsHeld answers true for, moved only as a field's held state flips, so a
    // form that holds nothing answers IsHeld without a lookup.
    private int _heldFields;
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
    public FieldRegistration Register(FieldIdentifier field, bool keepRegistered = false) =>
        RegisterWithHold(field, keepRegistered, holdsLiveMessages: false);

    /// <summary>Registers a rendered field, optionally holding its live messages until a submit or server reply answers.</summary>
    /// <param name="field">The field being rendered.</param>
    /// <param name="keepRegistered">As for <see cref="Register"/>.</param>
    /// <param name="holdsLiveMessages"><see langword="true"/> holds the field's live messages while this registration stands and holds, or while the retention it leaves does.</param>
    /// <returns>The handle to dispose when the field leaves the page.</returns>
    internal FieldRegistration RegisterWithHold(FieldIdentifier field, bool keepRegistered, bool holdsLiveMessages)
    {
        _entries.TryGetValue(field, out var entry);
        var wasHeld = entry.IsHeld;
        entry.Registrations++;
        if (holdsLiveMessages)
        {
            entry.Holding++;
        }

        _entries[field] = entry;
        _everRegistered.Add(field);
        _version++;
        NoteHeldState(field, wasHeld, entry.IsHeld);
        Changed?.Invoke();
        return new FieldRegistration(this, field, keepRegistered, holdsLiveMessages);
    }

    /// <summary>Whether the field is currently rendered, or retained by a handle disposed with <c>keepRegistered</c>.</summary>
    /// <param name="field">The field to look up.</param>
    /// <returns><see langword="true"/> while the field counts as registered.</returns>
    public bool IsRegistered(FieldIdentifier field) => _entries.ContainsKey(field);

    /// <summary>Whether the field's live messages are held: while a component is registered for it, whether any current registration holds; otherwise whether its keep-registered retention was left by one that held.</summary>
    /// <param name="field">The field to look up.</param>
    /// <returns><see langword="true"/> while the field is held.</returns>
    internal bool IsHeld(FieldIdentifier field) =>
        _heldFields > 0 && _entries.TryGetValue(field, out var entry) && entry.IsHeld;

    /// <summary>Moves one current registration's hold, and raises <see cref="HeldStateChanged"/> when that flips the field's held state.</summary>
    /// <param name="field">The field the registration speaks for.</param>
    /// <param name="holdsLiveMessages">The registration's new hold: <see langword="true"/> counts it among the field's holding registrations, <see langword="false"/> takes it out.</param>
    // Neither the version nor Changed moves: which fields are on the page has not changed, so no
    // root reconciles, clears what it keeps per field, or asks where the fields sit for it. The one
    // caller, FieldRegistration.ChangeHold, calls this only for a registration still standing
    // whose hold actually changed.
    internal void ChangeHold(FieldIdentifier field, bool holdsLiveMessages)
    {
        if (!_entries.TryGetValue(field, out var entry) || entry.Registrations == 0)
        {
            return;
        }

        var wasHeld = entry.IsHeld;
        if (holdsLiveMessages)
        {
            entry.Holding++;
        }
        else if (entry.Holding > 0)
        {
            entry.Holding--;
        }

        _entries[field] = entry;
        NoteHeldState(field, wasHeld, entry.IsHeld);
    }

    /// <summary>Raised synchronously, once, whenever <see cref="IsHeld"/> flips for a field: a registration arriving (a plain one over a holding retention included), one ending, or a change of hold.</summary>
    /// <remarks>
    /// Raised after the registry has taken the change, so <see cref="IsHeld"/> already answers for
    /// the field passed, and from inside the render batch that made the change.
    /// </remarks>
    internal event Action<FieldIdentifier>? HeldStateChanged;

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
    /// past the batch first, as both <see cref="FormidableForm{TModel}"/> and
    /// <see cref="FormidableValidator{TModel}"/> do: through the current
    /// <see cref="SynchronizationContext"/> where one exists, else through the thread pool.
    /// </remarks>
    internal event Action? Changed;

    /// <summary>The fields currently in the render tree, in no order; a field retained only by <c>keepRegistered</c> is absent.</summary>
    // A kept field has left the DOM, so there is no element of its own for an order service to
    // locate; listing it would only ask the browser about an id that renders nowhere.
    internal IEnumerable<FieldIdentifier> RegisteredFields
    {
        get
        {
            foreach (var (id, entry) in _entries)
            {
                if (entry.Registrations > 0)
                {
                    yield return id;
                }
            }
        }
    }

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
    /// <param name="holdsLiveMessages">Whether the ending registration held the field's live messages as it ended; a retention it leaves holds too.</param>
    internal void Unregister(FieldIdentifier field, bool keepRegistered, bool holdsLiveMessages)
    {
        if (!_entries.TryGetValue(field, out var entry) || entry.Registrations == 0)
        {
            return;
        }

        _version++;
        var wasHeld = entry.IsHeld;
        entry.Registrations--;
        if (holdsLiveMessages && entry.Holding > 0)
        {
            entry.Holding--;
        }

        if (entry.Registrations == 0)
        {
            // Latest disposal intent wins: a non-kept dispose reverses an earlier keep, and a kept
            // one holds only if the registration held as it ended.
            entry.Kept = keepRegistered;
            entry.KeptHolds = keepRegistered && holdsLiveMessages;
        }

        if (entry.Registrations == 0 && !entry.Kept)
        {
            _entries.Remove(field);
        }
        else
        {
            _entries[field] = entry;
        }

        NoteHeldState(field, wasHeld, entry.IsHeld);
        Changed?.Invoke();
    }

    /// <summary>Keeps the held-field count, and raises <see cref="HeldStateChanged"/>, when a change flipped the field's held state.</summary>
    /// <param name="field">The field the change touched.</param>
    /// <param name="wasHeld">Whether the field was held before the change.</param>
    /// <param name="isHeld">Whether it is held after.</param>
    private void NoteHeldState(FieldIdentifier field, bool wasHeld, bool isHeld)
    {
        if (wasHeld == isHeld)
        {
            return;
        }

        _heldFields += isHeld ? 1 : -1;
        HeldStateChanged?.Invoke(field);
    }

    /// <summary>One field's registrations: how many stand, how many of those hold, and the retention the last one to end left.</summary>
    private struct FieldEntry
    {
        /// <summary>The components registered for the field now.</summary>
        public int Registrations;

        /// <summary>The current registrations that hold the field's live messages.</summary>
        public int Holding;

        /// <summary>Whether the last registration to end kept the field registered.</summary>
        public bool Kept;

        /// <summary>Whether that kept registration held as it ended.</summary>
        public bool KeptHolds;

        /// <summary>Whether the field is held: by its current registrations while any stand, else by its retention.</summary>
        // A retention speaks for the field only while nothing else does. A component registered
        // for the field says what it wants now, so a hold left by a departed one cannot outlive
        // a registration that has switched its own hold off.
        public readonly bool IsHeld => Registrations > 0 ? Holding > 0 : Kept && KeptHolds;
    }
}
