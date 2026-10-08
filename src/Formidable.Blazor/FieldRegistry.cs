using System.Runtime.ExceptionServices;
using Microsoft.AspNetCore.Components.Forms;

namespace Formidable.Blazor;

/// <summary>The fields currently on the page, registered by the components that render them and read at submit to decide which errors may show.</summary>
/// <remarks>
/// At submit, which errors show is decided by what is on screen at that moment: a field nothing
/// registered discloses no submit error unless <see cref="FormidableOptions.DisclosureOverride"/> or
/// an earlier submit or server reply revealed it. The live channel shows an engaged field's messages
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

    // The fields a holding registration has ended for since the last SettleReleasedHolds. Kept
    // apart from the entries because a released hold can outlive its entry: a field whose last
    // registration left without keepRegistered has no entry, and its hold still stands until the
    // root's reconcile settles it.
    private readonly HashSet<FieldIdentifier> _releasedHolds = [];

    // How many fields IsHeld answers true for, a field held only by a released hold included,
    // moved only as a field's held state flips, so a form that holds nothing answers IsHeld
    // without a lookup.
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
    /// <param name="holdsLiveMessages"><see langword="true"/> holds the field's live messages while this registration stands and holds, then until <see cref="SettleReleasedHolds"/> runs, and while a retention it counts toward does.</param>
    /// <returns>The handle to dispose when the field leaves the page.</returns>
    // No consumer event handler runs here: HeldStateChanged reaches the engine, which rewrites the
    // field's messages and posts its notifications past the batch, and Changed reaches the roots,
    // which post their reconcile. One consumer delegate can run: a plain registration arriving
    // over a holding retention ends that hold at once, and under EngagedAndVisible the engine's
    // rewrite reads DisclosureOverride. If the flip throws, the registration is taken back before
    // the throw leaves, so a caller that never received a handle leaves nothing registered.
    internal FieldRegistration RegisterWithHold(FieldIdentifier field, bool keepRegistered, bool holdsLiveMessages)
    {
        var wasHeld = HeldNow(field);
        var existed = _entries.TryGetValue(field, out var entry);
        var previous = entry;
        var heldFields = _heldFields;
        if (entry.Registrations == 0)
        {
            // A registration arriving on a field with none standing starts a new visit, so the
            // kept holds the last visit counted no longer speak for the field.
            entry.KeptHolds = false;
        }

        entry.Registrations++;
        if (holdsLiveMessages)
        {
            entry.Holding++;
        }

        _entries[field] = entry;
        var firstRegistration = _everRegistered.Add(field);
        _version++;
        try
        {
            NoteHeldState(field, wasHeld, HeldNow(field));
        }
        catch
        {
            // Every change above is undone, so the registry stands as it did before the call and
            // nothing a root keys on the version moved.
            if (existed)
            {
                _entries[field] = previous;
            }
            else
            {
                _entries.Remove(field);
            }

            if (firstRegistration)
            {
                _everRegistered.Remove(field);
            }

            _heldFields = heldFields;
            _version--;
            throw;
        }

        Changed?.Invoke();
        return new FieldRegistration(this, field, keepRegistered, holdsLiveMessages);
    }

    /// <summary>Whether the field is currently rendered, or retained by a handle disposed with <c>keepRegistered</c>.</summary>
    /// <param name="field">The field to look up.</param>
    /// <returns><see langword="true"/> while the field counts as registered.</returns>
    public bool IsRegistered(FieldIdentifier field) => _entries.ContainsKey(field);

    /// <summary>Whether the field's live messages are held: by a hold an ended registration left, until <see cref="SettleReleasedHolds"/>; otherwise, while a component is registered for it, by any current registration that holds; otherwise by its keep-registered retention.</summary>
    /// <param name="field">The field to look up.</param>
    /// <returns><see langword="true"/> while the field is held.</returns>
    internal bool IsHeld(FieldIdentifier field) => _heldFields > 0 && HeldNow(field);

    /// <summary>Whether the field is held, read without the held-field count's shortcut.</summary>
    /// <param name="field">The field to look up.</param>
    /// <returns><see langword="true"/> while a released hold awaits its settle, or while the field's entry holds.</returns>
    private bool HeldNow(FieldIdentifier field) =>
        _releasedHolds.Contains(field) || (_entries.TryGetValue(field, out var entry) && entry.IsHeld);

    /// <summary>Moves one current registration's hold, and raises <see cref="HeldStateChanged"/> when that flips the field's held state.</summary>
    /// <param name="field">The field the registration speaks for.</param>
    /// <param name="holdsLiveMessages">The registration's new hold: <see langword="true"/> counts it among the field's holding registrations, <see langword="false"/> takes it out.</param>
    // Neither the version nor Changed moves: which fields are on the page has not changed, so no
    // root reconciles, clears what it keeps per field, or asks where the fields sit for it. The one
    // caller, FieldRegistration.ChangeHold, calls this only for a registration still standing
    // whose hold actually changed. A change that ends the field's hold can run one consumer
    // delegate: under EngagedAndVisible the engine's rewrite reads DisclosureOverride. If that
    // throws, the new hold stands and the throw reaches the component whose wait changed, which
    // already holds its handle, so nothing is left registered without one.
    internal void ChangeHold(FieldIdentifier field, bool holdsLiveMessages)
    {
        if (!_entries.TryGetValue(field, out var entry) || entry.Registrations == 0)
        {
            return;
        }

        var wasHeld = HeldNow(field);
        if (holdsLiveMessages)
        {
            entry.Holding++;
        }
        else if (entry.Holding > 0)
        {
            entry.Holding--;
        }

        _entries[field] = entry;
        NoteHeldState(field, wasHeld, HeldNow(field));
    }

    /// <summary>Raised synchronously, once, whenever <see cref="IsHeld"/> flips for a field: a registration arriving (a plain one over a holding retention included), a change of hold, a last registration ending over a retention that holds, or <see cref="SettleReleasedHolds"/> ending a hold.</summary>
    /// <remarks>
    /// Raised after the registry has taken the change, so <see cref="IsHeld"/> already answers for
    /// the field passed. A registration arriving or ending, or a change of hold, raises it from
    /// inside the render batch that made the change. A holding registration that ends never flips
    /// the field there: its hold stands until the root's reconcile calls
    /// <see cref="SettleReleasedHolds"/>.
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
    /// <param name="holdsLiveMessages">Whether the ending registration held the field's live messages as it ended: its hold stands until <see cref="SettleReleasedHolds"/>, and a kept one makes the retention its visit leaves hold.</param>
    // Changed is always raised: the one flip that happens here starts a hold (a last plain
    // registration ending over a retention that holds), and the engine's rewrite for a field that
    // becomes held reads no consumer delegate (see RegisterWithHold).
    internal void Unregister(FieldIdentifier field, bool keepRegistered, bool holdsLiveMessages)
    {
        if (!_entries.TryGetValue(field, out var entry) || entry.Registrations == 0)
        {
            return;
        }

        _version++;
        var wasHeld = HeldNow(field);
        entry.Registrations--;
        if (holdsLiveMessages && entry.Holding > 0)
        {
            entry.Holding--;

            // The hold stands until the root's reconcile has decided whether the field left the
            // page, so the field never shows what it held while that is undecided.
            _releasedHolds.Add(field);
        }

        if (keepRegistered && holdsLiveMessages)
        {
            entry.KeptHolds = true;
        }

        if (entry.Registrations == 0)
        {
            // Latest disposal intent decides whether the field is retained at all: a non-kept
            // dispose reverses an earlier keep, and drops the retention's hold with it.
            entry.Kept = keepRegistered;
        }

        if (entry.Registrations == 0 && !entry.Kept)
        {
            _entries.Remove(field);
        }
        else
        {
            _entries[field] = entry;
        }

        NoteHeldState(field, wasHeld, HeldNow(field));
        Changed?.Invoke();
    }

    /// <summary>Ends every hold a holding registration left standing as it ended since the last call, and raises <see cref="HeldStateChanged"/> once for each field that stops being held.</summary>
    /// <remarks>
    /// The root's reconcile calls this once it has dropped the fields that left the page, so a
    /// field that left never shows what it held, and a field still on the page shows it from
    /// here on. A field another holding registration or a holding retention still holds stays
    /// held and raises nothing. A handler that throws for one field does not stop the others;
    /// the throw reaches the caller once every field has settled.
    /// </remarks>
    /// <exception cref="AggregateException">Handlers threw for more than one field; a single throw is rethrown as it was.</exception>
    internal void SettleReleasedHolds()
    {
        if (_releasedHolds.Count == 0)
        {
            return;
        }

        // Copied and cleared first, so every field is read with no released hold left standing,
        // and a handler that reaches back into the registry finds the set already settled. A
        // throw is held until the loop ends: the engine's rewrite for a field that stops being
        // held can read a consumer's DisclosureOverride, and the set is already cleared, so a
        // field the loop had not reached would otherwise never raise its flip.
        var released = new FieldIdentifier[_releasedHolds.Count];
        _releasedHolds.CopyTo(released);
        _releasedHolds.Clear();
        List<Exception>? failures = null;
        foreach (var field in released)
        {
            try
            {
                NoteHeldState(field, wasHeld: true, HeldNow(field));
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }

        if (failures is { Count: 1 })
        {
            ExceptionDispatchInfo.Throw(failures[0]);
        }

        if (failures is not null)
        {
            throw new AggregateException(failures);
        }
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

    /// <summary>One field's registrations: how many stand, how many of those hold, and the retention the field's last visit left.</summary>
    // A visit runs from a registration arriving on a field with none standing to the last one
    // ending.
    private struct FieldEntry
    {
        /// <summary>The components registered for the field now.</summary>
        public int Registrations;

        /// <summary>The current registrations that hold the field's live messages.</summary>
        public int Holding;

        /// <summary>Whether the last registration to end kept the field registered.</summary>
        public bool Kept;

        /// <summary>Whether any registration that ended during the field's current or last visit was kept and held as it ended.</summary>
        // Any, not only the last to end: a kept waiting wrapper disposed ahead of the kept plain
        // input it wraps still holds the retention the two leave. A new visit forgets it.
        public bool KeptHolds;

        /// <summary>Whether the field is held: by its current registrations while any stand, else by its retention.</summary>
        // A retention speaks for the field only while nothing else does. A component registered
        // for the field says what it wants now, so a hold left by a departed one cannot outlive
        // a registration that has switched its own hold off.
        public readonly bool IsHeld => Registrations > 0 ? Holding > 0 : Kept && KeptHolds;
    }
}
