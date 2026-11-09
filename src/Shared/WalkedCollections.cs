using System.Collections;

namespace Formidable.Shared;

/// <summary>The walks one read makes of the collections it reaches only by walking them: one shared walk per collection instance, taken no further than the furthest row asked.</summary>
// A collection with no list index and no indexer the reader can call (a HashSet<T>, a
// LinkedList<T>, a list known only by IReadOnlyList<T> to code that cannot name its element type)
// is read by walking it from the start, so a read that names every row would walk it once per row.
//
// Walks are kept by instance, never by equality (two equal collections are two objects), so every
// read of one instance shares its walk, from whichever place it was reached, unless that place is
// read one element at a time (below). A shared walk
// advances only as far as the furthest row asked of it, keeps those rows, and is closed as soon as
// it reaches the end or meets a refusal; the record closes the rest when the read ends.
//
// Each read also names its place, the path that reached the collection, because the place is what
// tells a member that builds a new collection on each read (a getter returning _rows.Where(...))
// from one that hands back the same instance. The first instance a place hands back is walked as
// any instance is. When the place hands back another, its earlier walk is closed and dropped, and
// from then on each row it is asked for is reached by a walk of its own, to that row and closed,
// as a read of one path reaches it. So the paths of such a place take no more steps than reading
// each on its own would. A place's first instance keeps its walk open until the walk ends, the
// place hands back another, or the read ends: one at a time for a getter read from one place, one
// per row for a member built afresh per row (Teams[i].Members) and read once per row, each walk no
// longer than the steps that made it.
//
// A walk the collection refuses partway keeps the rows before the refusal, which a walk of their
// own would reach, and a read from the refusal on fails, as a walk to it would. That refusal can
// belong to the shared walk alone: the walk stays open across paths, so a getter read in between
// can change the collection under it (a HashSet throws at its next step), where a later read's own
// walk reads the changed collection. So the record also notes the readers a refusal answered
// (ReadersRefusedPartway), for ResolveAll to answer again on their own, and which walks met one
// (RefusedPartway), for the engine to read a row from the refusal on with a walk of its own
// (WalkTo). The rows before the refusal keep their answers, which a collection refusing every
// later walk would lose if read again. A collection that
// throws as its walk is closed refuses every read of one path, since that read closes its walk
// before it answers, rows already read from the shared walk included. No shared walk can answer
// for that after the fact, so the record tells the reader which answers it served: the paths that
// read the walk (Reader, ReadersOfWalksThatFailedToClose) and the places that reached it
// (PlacesThatFailedToClose), which a second record reads one element at a time (readAlone). A
// collection that throws only when a walk is closed before its end is not caught this way when the
// shared walk reached the end: that walk closes cleanly, where a read of one path, closed early,
// meets the refusal. An iterator whose finally block throws is not caught either: the shared walk
// meets the throw inside its last step, a refusal after the last row, so only the paths past it
// are answered again. Nor is a getter that changes a walked collection without making the walk
// throw (a SortedSet gaining a row ahead of those read): later paths keep the rows the walk read.
// A record lives for one read and no longer, so no later read meets rows the model has since
// replaced.
internal sealed class WalkedCollections : IDisposable
{
    // Stands for a place that has handed back more than one instance in this read.
    private static readonly object NewEachRead = new();

    private readonly HashSet<string>? _readAlone;
    private Dictionary<object, Walk>? _walks;
    private Dictionary<string, object>? _places;
    private List<Walk>? _failed;
    private HashSet<int>? _refusedReaders;

    /// <summary>A record that shares a walk per collection instance.</summary>
    public WalkedCollections()
    {
    }

    /// <summary>A record that shares a walk per collection instance, except at <paramref name="readAlone"/>, whose collections are read one element at a time.</summary>
    /// <param name="readAlone">The places whose collections threw as an earlier record closed their walks.</param>
    public WalkedCollections(IEnumerable<string> readAlone) =>
        _readAlone = new HashSet<string>(readAlone, StringComparer.Ordinal);

    /// <summary>Whether closing a walk threw, which a read of one path meets as a refusal of that collection, so answers taken from the walk may differ from such reads.</summary>
    public bool CloseFailed => _failed is not null;

    /// <summary>The path the caller is answering, noted on each shared walk it reads; below zero notes nothing.</summary>
    public int Reader { get; set; } = -1;

    /// <summary>The readers, as <see cref="Reader"/> named them, of every walk that threw as it was closed.</summary>
    public IEnumerable<int> ReadersOfWalksThatFailedToClose =>
        _failed is null ? [] : _failed.SelectMany(walk => walk.Readers).Distinct();

    /// <summary>The readers, as <see cref="Reader"/> named them, that a walk refused partway answered with a refusal: each asked for a row at or after the refusal.</summary>
    public IEnumerable<int> ReadersRefusedPartway => _refusedReaders ?? [];

    /// <summary>Whether <see cref="ReadersRefusedPartway"/> holds any reader.</summary>
    public bool RefusedAnyReader => _refusedReaders is not null;

    /// <summary>The places that reached a walk that threw as it was closed.</summary>
    public IEnumerable<string> PlacesThatFailedToClose =>
        _failed is null ? [] : _failed.SelectMany(walk => walk.Places).Distinct(StringComparer.Ordinal);

    /// <summary>Reads the element at <paramref name="index"/> of <paramref name="collection"/>, from the shared walk of it unless <paramref name="place"/> builds a new collection on each read, otherwise by a walk of its own.</summary>
    /// <param name="collection">A collection read only by walking it.</param>
    /// <param name="place">The path the collection was reached by, which tells a collection met again from a new one.</param>
    /// <param name="index">The element's position in the order the collection enumerates.</param>
    /// <param name="value">The element read; <see langword="null"/> when it cannot be read.</param>
    /// <returns><see langword="true"/> when the element was read; <see langword="false"/> past the end, or from the position where the collection refused the walk.</returns>
    public bool TryRead(IEnumerable collection, string place, int index, out object? value) =>
        WalkFor(collection, place) is { } walk
            ? walk.TryRead(index, out value)
            : WalkTo(collection, index, out value);

    /// <summary>Counts the elements of <paramref name="collection"/> through its shared walk, which this takes to the end.</summary>
    /// <param name="collection">A collection read only by walking it.</param>
    /// <param name="place">The path the collection was reached by.</param>
    /// <param name="count">The element count when the walk reached the end; otherwise the number of elements before the refusal, or zero for a collection read one element at a time.</param>
    /// <returns><see langword="true"/> when the walk reached the end; <see langword="false"/> when the collection refused it, or when <paramref name="place"/> is read one element at a time, which leaves the count to a walk of the caller's own.</returns>
    public bool TryCount(IEnumerable collection, string place, out int count)
    {
        if (WalkFor(collection, place) is { } walk)
        {
            return walk.TryCount(out count);
        }

        count = 0;
        return false;
    }

    /// <summary>Whether this record's shared walk of <paramref name="collection"/> met a refusal before its end, so a row it could not read may be reached by a walk of its own.</summary>
    /// <param name="collection">A collection read only by walking it.</param>
    /// <returns><see langword="true"/> when the record holds a walk of that instance that the collection refused partway.</returns>
    public bool RefusedPartway(IEnumerable collection) =>
        _walks is not null && _walks.TryGetValue(collection, out var walk) && walk.Refused;

    /// <summary>Closes every walk still open.</summary>
    public void Dispose()
    {
        if (_walks is null)
        {
            return;
        }

        foreach (var walk in _walks.Values)
        {
            walk.Close();
        }

        _walks.Clear();
    }

    /// <summary>Walks <paramref name="collection"/> from the start to the element at <paramref name="index"/> and closes the walk, as a read of one path does.</summary>
    /// <param name="collection">The collection.</param>
    /// <param name="index">The element's position.</param>
    /// <param name="value">The element read; <see langword="null"/> when it cannot be read.</param>
    /// <returns><see langword="true"/> when the element was read; <see langword="false"/> past the end, or when the collection throws at any point of the walk, its closing included.</returns>
    internal static bool WalkTo(IEnumerable collection, int index, out object? value)
    {
        try
        {
            var position = 0;
            foreach (var item in collection)
            {
                if (position++ == index)
                {
                    value = item;
                    return true;
                }
            }
        }
        catch
        {
            // A collection that refuses the walk (a consumer's own collection over a source it
            // reads once, or one that throws as the walk is closed) has no element to hand back,
            // which claims nothing, as for a member whose getter throws.
        }

        value = null;
        return false;
    }

    /// <summary>The shared walk of <paramref name="collection"/>, or <see langword="null"/> when <paramref name="place"/> is read one element at a time.</summary>
    /// <param name="collection">A collection read only by walking it.</param>
    /// <param name="place">The path the collection was reached by.</param>
    /// <returns>The walk, made on the first ask; <see langword="null"/> for a place that has handed back more than one instance, or one this record reads alone.</returns>
    private Walk? WalkFor(IEnumerable collection, string place)
    {
        if (_readAlone is not null && _readAlone.Contains(place))
        {
            return null;
        }

        _places ??= new Dictionary<string, object>(StringComparer.Ordinal);
        if (_places.TryGetValue(place, out var seen))
        {
            if (ReferenceEquals(seen, NewEachRead))
            {
                return null;
            }

            if (!ReferenceEquals(seen, collection))
            {
                // The place built a new collection for this read, so the walk of the one it built
                // before will not be asked again from here: close it now rather than hold it.
                _places[place] = NewEachRead;
                if (_walks is not null && _walks.Remove(seen, out var earlier))
                {
                    earlier.Close();
                }

                return null;
            }
        }
        else
        {
            _places.Add(place, collection);
        }

        _walks ??= new Dictionary<object, Walk>(ReferenceEqualityComparer.Instance);
        if (!_walks.TryGetValue(collection, out var walk))
        {
            walk = new Walk(this, collection);
            _walks.Add(collection, walk);
        }

        walk.ReachedFrom(place);
        return walk;
    }

    /// <summary>One walk of a collection, advanced only as far as it has been asked: the elements it has reached, whether it reached the end, and who read it.</summary>
    /// <param name="owner">The record the walk belongs to, told when closing the walk throws.</param>
    /// <param name="collection">The collection.</param>
    private sealed class Walk(WalkedCollections owner, IEnumerable collection)
    {
        private readonly List<object?> _elements = [];
        private readonly List<int> _readers = [];
        private readonly HashSet<string> _places = new(StringComparer.Ordinal);
        private IEnumerator? _enumerator;
        private bool _finished;
        private bool _complete;
        private bool _refused;

        /// <summary>The readers that read this walk, as the owner's <see cref="WalkedCollections.Reader"/> named them.</summary>
        public IReadOnlyList<int> Readers => _readers;

        /// <summary>The places this walk was reached from.</summary>
        public IReadOnlyCollection<string> Places => _places;

        /// <summary>Whether the collection refused this walk before its end.</summary>
        public bool Refused => _refused;

        /// <summary>Notes a place the walk was reached from.</summary>
        /// <param name="place">The place.</param>
        public void ReachedFrom(string place) => _places.Add(place);

        /// <summary>Reads the element at <paramref name="index"/>, advancing the walk to it when it has not got that far, and notes the owner's current reader.</summary>
        /// <param name="index">The element's position.</param>
        /// <param name="value">The element read; <see langword="null"/> when it cannot be read.</param>
        /// <returns><see langword="true"/> when the element was read.</returns>
        public bool TryRead(int index, out object? value)
        {
            var reader = owner.Reader;
            if (reader >= 0 && (_readers.Count == 0 || _readers[^1] != reader))
            {
                _readers.Add(reader);
            }

            AdvanceTo(index);
            if (index < _elements.Count)
            {
                value = _elements[index];
                return true;
            }

            if (_refused && reader >= 0)
            {
                (owner._refusedReaders ??= []).Add(reader);
            }

            value = null;
            return false;
        }

        /// <summary>Counts the elements, advancing the walk to the end.</summary>
        /// <param name="count">The elements reached.</param>
        /// <returns><see langword="true"/> when the walk reached the end without a refusal.</returns>
        public bool TryCount(out int count)
        {
            AdvanceTo(int.MaxValue);
            count = _elements.Count;
            return _complete;
        }

        /// <summary>Closes the walk; a collection that throws as it is closed is answered as refused from then on, and the owner is told.</summary>
        public void Close()
        {
            var enumerator = _enumerator;
            _enumerator = null;
            _finished = true;
            if (enumerator is not IDisposable disposable)
            {
                return;
            }

            try
            {
                disposable.Dispose();
            }
            catch
            {
                _elements.Clear();
                _complete = false;
                (owner._failed ??= []).Add(this);
            }
        }

        /// <summary>Advances the walk until it holds the element at <paramref name="index"/>, reaches the end, or meets a refusal.</summary>
        /// <param name="index">The position to reach.</param>
        private void AdvanceTo(int index)
        {
            if (_finished)
            {
                return;
            }

            try
            {
                _enumerator ??= collection.GetEnumerator();
                while (_elements.Count <= index)
                {
                    if (!_enumerator.MoveNext())
                    {
                        _complete = true;
                        Close();
                        return;
                    }

                    _elements.Add(_enumerator.Current);
                }
            }
            catch
            {
                // A collection that refuses the walk partway leaves its rows from the refusal on
                // unread, which claims nothing, as for a member whose getter throws. The refusal
                // is noted, because it may belong to this walk alone (a getter read since the walk
                // began changed the collection) and a walk of a later read's own may get further.
                _refused = true;
                Close();
            }
        }
    }
}
