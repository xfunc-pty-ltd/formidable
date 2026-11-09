using System.Collections;
using Formidable.Introspection;

namespace Formidable.Tests;

/// <summary>
/// <see cref="IModelIntrospector.ResolveAll"/> answers each path as <see cref="IModelIntrospector.Resolve"/>
/// does. The reflection introspector walks a collection with no list index and no indexer once
/// for the whole call; an implementation that does not override the member answers through its
/// own <c>Resolve</c>, one path at a time.
/// </summary>
public class IntrospectorResolveAllTests
{
    private sealed class Row
    {
        public string Name { get; set; } = string.Empty;

        public Row? Child { get; set; }
    }

    /// <summary>The walks made over one or more counting collections: steps, walks begun, and how many are open at once.</summary>
    private sealed class WalkCounter
    {
        public int GetEnumeratorCalls { get; set; }

        public long MoveNextCalls { get; set; }

        public int Open { get; set; }

        public int MaxOpen { get; set; }
    }

    /// <summary>A collection known only by <see cref="IEnumerable{T}"/>, so its rows are reached only by walking it, which counts its walks and can refuse one at a position or as it is closed.</summary>
    private sealed class CountingRows(IEnumerable<Row> rows, WalkCounter? counter = null) : IEnumerable<Row>
    {
        private readonly List<Row> _rows = [.. rows];

        public WalkCounter Counter { get; } = counter ?? new WalkCounter();

        public int GetEnumeratorCalls => Counter.GetEnumeratorCalls;

        public long MoveNextCalls => Counter.MoveNextCalls;

        /// <summary>The position at which a walk throws instead of moving on; <see langword="null"/> for none.</summary>
        public int? RefuseAt { get; init; }

        /// <summary>Whether closing a walk throws.</summary>
        public bool CloseThrows { get; init; }

        /// <summary>The row at <paramref name="index"/>, read without walking; a method rather than an indexer, so the introspector still has to walk.</summary>
        public Row At(int index) => _rows[index];

        public void Replace(int index, Row row) => _rows[index] = row;

        public IEnumerator<Row> GetEnumerator()
        {
            Counter.GetEnumeratorCalls++;
            Counter.Open++;
            Counter.MaxOpen = Math.Max(Counter.MaxOpen, Counter.Open);
            return new Walk(this);
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private sealed class Walk(CountingRows owner) : IEnumerator<Row>
        {
            private int _position = -1;
            private bool _closed;

            public Row Current => owner._rows[_position];

            object IEnumerator.Current => Current;

            public bool MoveNext()
            {
                owner.Counter.MoveNextCalls++;
                if (_position + 1 == owner.RefuseAt)
                {
                    throw new InvalidOperationException("The rows refuse the walk here.");
                }

                if (_position + 1 == owner._rows.Count)
                {
                    return false;
                }

                _position++;
                return true;
            }

            public void Reset() => throw new NotSupportedException();

            public void Dispose()
            {
                if (!_closed)
                {
                    _closed = true;
                    owner.Counter.Open--;
                }

                if (owner.CloseThrows)
                {
                    throw new InvalidOperationException("The rows refuse to close the walk.");
                }
            }
        }
    }

    /// <summary>A model whose rows come from a getter that builds a new collection over the same rows on every read, as <c>_rows.Where(...)</c> would.</summary>
    private sealed class NewEachRead(IEnumerable<Row> rows)
    {
        private readonly List<Row> _rows = [.. rows];

        public WalkCounter Counter { get; } = new();

        public IEnumerable<Row> Rows => new CountingRows(_rows, Counter);

        public Row At(int index) => _rows[index];
    }

    private sealed class Mixed
    {
        public string Title { get; set; } = "t";

        public Row? Missing { get; set; }

        public Row Present { get; } = new() { Name = "p", Child = new() { Name = "pc" } };

        public List<Row> List { get; } = [new() { Name = "l0" }, new() { Name = "l1" }];

        public HashSet<Row> Set { get; } = [new() { Name = "s0" }, new() { Name = "s1" }];

        public LinkedList<Row> Linked { get; } = new([new() { Name = "k0" }, new() { Name = "k1" }]);

        public Dictionary<string, Row> Map { get; } = new() { ["k"] = new() { Name = "m" } };

        public CountingRows Walked { get; } = new([new() { Name = "w0", Child = new() { Name = "w0c" } }, new() { Name = "w1" }]);

        public CountingRows Refusing { get; } = new([new() { Name = "r0" }, new() { Name = "r1" }, new() { Name = "r2" }, new() { Name = "r3" }]) { RefuseAt = 2 };

        public string Chars => "abc";
    }

    private static readonly string[] MixedPaths =
    [
        "", "Title", "Missing.Name", "Present.Name", "Present.Child.Name", "List[1].Name", "List[5].Name",
        "Set[0].Name", "Set[1].Name", "Set[2].Name", "Set[-1].Name", "Linked[1].Name", "Map[k].Name",
        "Map[zz].Name", "Nope.Name", "Bad[", "Walked[1].Name", "Walked[0].Child.Name", "Walked[9].Name",
        "Refusing[1].Name", "Refusing[3].Name", "Chars[1]", "Set[1].Name", "Walked[0].Name",
    ];

    // Every shape a path takes: a property, a null intermediate, a list, a set, a linked list, a
    // dictionary key present and missing, an unknown member, a malformed path, a negative and an
    // out-of-range position, a walked collection, a walk refused partway, a string and a repeat.
    // Mutation that must break it: read a walked row from the wrong position of the call's walk.
    [Fact]
    public void ResolveAll_answers_each_path_as_Resolve_does()
    {
        var model = new Mixed();
        var expected = MixedPaths.Select(path => new ReflectionModelIntrospector().Resolve(model, path)).ToList();

        var resolved = new ReflectionModelIntrospector().ResolveAll(model, MixedPaths);

        Assert.Equal(expected, resolved);

        // The two walked rows the comparison could otherwise pass vacuously on: a row before the
        // refusal is reached, and one from it on falls back to the collection.
        Assert.Same(model.Refusing.At(1), resolved[Array.IndexOf(MixedPaths, "Refusing[1].Name")].Owner);
        Assert.Same(model.Refusing, resolved[Array.IndexOf(MixedPaths, "Refusing[3].Name")].Owner);
        Assert.Same(model.Walked.At(0).Child, resolved[Array.IndexOf(MixedPaths, "Walked[0].Child.Name")].Owner);
    }

    // Two thousand rows, named in reverse and some twice: one walk serves them all. Mutation that
    // must break it: read every row by a walk of its own (ElementAtPosition without the call's
    // record), and the collection is walked once per path, about two million steps.
    [Fact]
    public void ResolveAll_walks_a_collection_once_for_all_of_its_rows()
    {
        var rows = new CountingRows(Enumerable.Range(0, 2000).Select(i => new Row { Name = $"r{i}" }));
        var model = new { Rows = rows };
        string[] paths = [.. Enumerable.Range(0, 2000).Reverse().Select(i => $"Rows[{i}].Name"), "Rows[1234].Name", "Rows[2000].Name"];

        var resolved = new ReflectionModelIntrospector().ResolveAll(model, paths);

        Assert.True(
            rows.GetEnumeratorCalls == 1 && rows.MoveNextCalls <= 2001,
            $"GetEnumerator {rows.GetEnumeratorCalls}, MoveNext {rows.MoveNextCalls}");
        Assert.Same(rows.At(1999), resolved[0].Owner);
        Assert.Same(rows.At(0), resolved[1999].Owner);
        Assert.Same(rows.At(1234), resolved[2000].Owner);
        Assert.Same(rows, resolved[2001].Owner);
        Assert.Equal("[2000].Name", resolved[2001].PropertyName);
    }

    // Each collection keeps a walk of its own, keyed by the instance. Mutation that must break it:
    // key the record by the collection's type, and the second collection's rows come from the
    // first's walk.
    [Fact]
    public void ResolveAll_walks_each_collection_on_its_own()
    {
        var first = new CountingRows([new() { Name = "a0" }, new() { Name = "a1" }]);
        var second = new CountingRows([new() { Name = "b0" }, new() { Name = "b1" }]);
        var model = new { First = first, Second = second };

        var resolved = new ReflectionModelIntrospector().ResolveAll(
            model, ["First[1].Name", "Second[1].Name", "First[0].Name", "Second[0].Name"]);

        Assert.Equal((1, 1), (first.GetEnumeratorCalls, second.GetEnumeratorCalls));
        Assert.Equal([first.At(1), second.At(1), first.At(0), second.At(0)], resolved.Select(field => field.Owner));
    }

    // A walk is kept for one call and no longer, so a row replaced between two calls is the row
    // the second call reaches. Mutation that must break it: keep the record on the introspector.
    [Fact]
    public void A_walk_is_never_kept_beyond_its_call()
    {
        var rows = new CountingRows([new() { Name = "old" }]);
        var model = new { Rows = rows };
        var introspector = new ReflectionModelIntrospector();
        var before = introspector.ResolveAll(model, ["Rows[0].Name"]);
        var replacement = new Row { Name = "new" };

        rows.Replace(0, replacement);
        var after = introspector.ResolveAll(model, ["Rows[0].Name"]);

        Assert.NotSame(replacement, before[0].Owner);
        Assert.Same(replacement, after[0].Owner);
        Assert.Equal(2, rows.GetEnumeratorCalls);
    }

    [Fact]
    public void ResolveAll_of_no_paths_is_empty()
    {
        Assert.Empty(new ReflectionModelIntrospector().ResolveAll(new Mixed(), []));
    }

    [Fact]
    public void ResolveAll_refuses_a_null_model_a_null_list_or_a_null_path()
    {
        var introspector = new ReflectionModelIntrospector();

        Assert.Throws<ArgumentNullException>(() => introspector.ResolveAll(null!, ["Title"]));
        Assert.Throws<ArgumentNullException>(() => introspector.ResolveAll(new Mixed(), null!));
        Assert.Throws<ArgumentNullException>(() => introspector.ResolveAll(new Mixed(), ["Title", null!]));
    }

    // A getter that builds a new collection on every read gives each path a collection of its own,
    // so nothing is shared: the call walks each to its row, as Resolve does, and holds no more than
    // one walk open at a time. Mutations that must break it: share a walk per instance whatever
    // the place (every path's walk stays open until the call ends), or walk a recorded collection
    // to its end (the first path's walk passes its row).
    [Fact]
    public void A_getter_that_builds_a_new_collection_on_each_read_costs_no_more_than_Resolve()
    {
        var model = new NewEachRead(Enumerable.Range(0, 2000).Select(i => new Row { Name = $"r{i}" }));
        string[] paths = [.. Enumerable.Range(0, 2000).Select(i => $"Rows[{i}].Name")];
        var introspector = new ReflectionModelIntrospector();

        var expected = paths.Select(path => introspector.Resolve(model, path)).ToList();
        var resolveSteps = model.Counter.MoveNextCalls;
        model.Counter.MoveNextCalls = 0;
        model.Counter.MaxOpen = 0;

        var resolved = introspector.ResolveAll(model, paths);

        Assert.Equal(expected, resolved);
        Assert.Same(model.At(1234), resolved[1234].Owner);
        Assert.True(
            model.Counter.MoveNextCalls <= resolveSteps,
            $"ResolveAll {model.Counter.MoveNextCalls} steps, Resolve per path {resolveSteps}");
        Assert.True(model.Counter.MaxOpen <= 1, $"{model.Counter.MaxOpen} walks open at once");
        Assert.Equal(0, model.Counter.Open);
    }

    /// <summary>A lazy collection of one row repeated, which counts its walks.</summary>
    private sealed class Repeated(Row row, int count) : IEnumerable<Row>
    {
        public WalkCounter Counter { get; } = new();

        public IEnumerator<Row> GetEnumerator()
        {
            Counter.GetEnumeratorCalls++;
            Counter.Open++;
            return Walk();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private IEnumerator<Row> Walk()
        {
            try
            {
                for (var position = 0; ; position++)
                {
                    Counter.MoveNextCalls++;
                    if (position == count)
                    {
                        yield break;
                    }

                    yield return row;
                }
            }
            finally
            {
                Counter.Open--;
            }
        }
    }

    // One path into a long lazy collection walks only to its row, as Resolve does, and the walk is
    // closed when the call ends. Mutation that must break it: walk a recorded collection to its
    // end (a million steps).
    [Fact]
    public void One_path_into_a_long_lazy_collection_walks_only_to_its_row()
    {
        var rows = new Repeated(new Row { Name = "r" }, 1_000_000);
        var model = new { Rows = rows };

        var resolved = new ReflectionModelIntrospector().ResolveAll(model, ["Rows[2].Name"]);

        Assert.Equal(nameof(Row.Name), resolved[0].PropertyName);
        Assert.True(rows.Counter.MoveNextCalls <= 2 + 2, $"{rows.Counter.MoveNextCalls} steps to reach row 2");
        Assert.Equal(0, rows.Counter.Open);
    }

    // A walk shared across the call is closed as the call ends, though no path asked for the end.
    [Fact]
    public void A_shared_walk_is_closed_when_the_call_ends()
    {
        var rows = new CountingRows(Enumerable.Range(0, 10).Select(i => new Row { Name = $"r{i}" }));
        var model = new { Rows = rows };

        new ReflectionModelIntrospector().ResolveAll(model, ["Rows[3].Name", "Rows[1].Name"]);

        Assert.Equal((1, 4L, 0), (rows.GetEnumeratorCalls, rows.MoveNextCalls, rows.Counter.Open));
    }

    // Resolve walks to the row and closes the walk, so a collection that throws as its walk is
    // closed answers every path as a refusal, the collection with the rest of the path. ResolveAll
    // gives the same answers. Mutation that must break it: drop the second answering when closing
    // a walk threw, and the rows read from the shared walk are answered as rows.
    [Fact]
    public void A_collection_that_throws_as_its_walk_is_closed_answers_as_Resolve_does()
    {
        var rows = new CountingRows([new Row { Name = "r0" }, new Row { Name = "r1" }]) { CloseThrows = true };
        var model = new { Rows = rows };
        string[] paths = ["Rows[0].Name", "Rows[1].Name", "Rows[5].Name", "Rows"];
        var introspector = new ReflectionModelIntrospector();
        var expected = paths.Select(path => introspector.Resolve(model, path)).ToList();

        var resolved = introspector.ResolveAll(model, paths);

        Assert.Equal(expected, resolved);
        Assert.Same(rows, resolved[0].Owner);
        Assert.Equal("[0].Name", resolved[0].PropertyName);
    }

    // Only the paths that read the collection whose walk threw as it closed are answered again; a
    // stable collection beside it keeps its one walk. Mutation that must break it: answer every
    // path again when any walk threw as it closed, and the stable rows are walked once per path.
    [Fact]
    public void A_collection_beside_one_that_throws_as_its_walk_is_closed_keeps_its_one_walk()
    {
        var good = new CountingRows(Enumerable.Range(0, 2000).Select(i => new Row { Name = $"g{i}" }));
        var bad = new CountingRows([new Row { Name = "b0" }, new Row { Name = "b1" }]) { CloseThrows = true };
        var model = new { Good = good, Bad = bad };
        string[] paths = [.. Enumerable.Range(0, 2000).Select(i => $"Good[{i}].Name"), "Bad[0].Name"];
        var introspector = new ReflectionModelIntrospector();
        var expected = paths.Select(path => introspector.Resolve(model, path)).ToList();
        good.Counter.MoveNextCalls = 0;
        good.Counter.GetEnumeratorCalls = 0;

        var resolved = introspector.ResolveAll(model, paths);

        Assert.Equal(expected, resolved);
        Assert.Same(good.At(1234), resolved[1234].Owner);
        Assert.Same(bad, resolved[^1].Owner);
        Assert.True(
            good.GetEnumeratorCalls == 1 && good.MoveNextCalls <= 2001,
            $"GetEnumerator {good.GetEnumeratorCalls}, MoveNext {good.MoveNextCalls}");
    }

    /// <summary>A row of <see cref="GrowingSet"/> whose child is made on its first read, and joins a new row to the set as it is made.</summary>
    private sealed class GrowingRow(GrowingSet owner, string name)
    {
        private Row? _child;

        public string Name { get; } = name;

        public Row Child => _child ??= owner.Grow(this);
    }

    /// <summary>A model over a <see cref="HashSet{T}"/>, read only by walking it, that a getter on one of its rows adds to.</summary>
    private sealed class GrowingSet
    {
        public GrowingSet()
        {
            foreach (var name in new[] { "r0", "r1", "r2" })
            {
                Rows.Add(new GrowingRow(this, name));
            }
        }

        public HashSet<GrowingRow> Rows { get; } = [];

        public Row Grow(GrowingRow parent)
        {
            Rows.Add(new GrowingRow(this, $"{parent.Name}-late"));
            return new Row { Name = $"{parent.Name}.child" };
        }
    }

    /// <summary>Who answered a path, by name, so answers over two copies of a model compare.</summary>
    private static (string Owner, string Name) Named(ResolvedField field) => field.Owner switch
    {
        GrowingRow row => (row.Name, field.PropertyName),
        Row row => (row.Name, field.PropertyName),
        HashSet<GrowingRow> => ("set", field.PropertyName),
        _ => (field.Owner.GetType().Name, field.PropertyName),
    };

    // A row's child made on its first read joins a new row to the set the call is walking, so the
    // next step of the shared walk throws, as a HashSet does once it changes under a walk. A walk
    // of its own reads the changed set and reaches the row, so the paths the throw answered are
    // answered again as Resolve answers them. Mutation that must break it: answer again only the
    // readers of a walk that threw as it closed, and Rows[1].Child.Name and Rows[2].Name resolve to
    // the set.
    [Fact]
    public void A_getter_that_changes_a_walked_set_has_the_rows_after_it_answered_as_Resolve_does()
    {
        string[] paths = ["Rows[0].Child.Name", "Rows[1].Child.Name", "Rows[2].Name"];
        var introspector = new ReflectionModelIntrospector();
        var once = new GrowingSet();
        var expected = paths.Select(path => Named(introspector.Resolve(once, path))).ToList();

        // The control: reading a row's child while walking the set makes the walk throw.
        var probe = new GrowingSet();
        using (var walk = probe.Rows.GetEnumerator())
        {
            Assert.True(walk.MoveNext());
            _ = walk.Current.Child;
            Assert.Throws<InvalidOperationException>(() => walk.MoveNext());
        }

        var resolved = introspector.ResolveAll(new GrowingSet(), paths).Select(Named).ToList();

        Assert.Equal([("r0.child", "Name"), ("r1.child", "Name"), ("r2", "Name")], expected);
        Assert.Equal(expected, resolved);
    }

    /// <summary>A collection read only by walking it whose first walk refuses at a position and whose every later walk refuses at once, as a one-pass source does once read.</summary>
    private sealed class ReadOnceRows(IEnumerable<Row> rows, int refuseAt) : IEnumerable<Row>
    {
        private readonly List<Row> _rows = [.. rows];
        private bool _walked;

        public Row At(int index) => _rows[index];

        public IEnumerator<Row> GetEnumerator()
        {
            if (_walked)
            {
                throw new InvalidOperationException("The rows can be walked only once.");
            }

            _walked = true;
            return Walk();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private IEnumerator<Row> Walk()
        {
            for (var position = 0; position < _rows.Count; position++)
            {
                if (position == refuseAt)
                {
                    throw new InvalidOperationException("The rows refuse the walk here.");
                }

                yield return _rows[position];
            }
        }
    }

    // A walk refused partway keeps the rows before the refusal, which a walk of their own reaches,
    // so the paths it answered with a row keep their answers. Only the path the refusal answered
    // is answered again, and Resolve refuses it too. Each answer is the one Resolve gives reading
    // that path alone on a collection not yet walked. Mutation that must break it: answer again
    // every reader of a walk refused partway, and the two rows before the refusal resolve to the
    // collection, since it refuses every later walk.
    [Fact]
    public void A_walk_refused_partway_keeps_its_rows_on_a_collection_that_refuses_every_later_walk()
    {
        string[] paths = ["Rows[0].Name", "Rows[1].Name", "Rows[5].Name"];
        var introspector = new ReflectionModelIntrospector();
        ReadOnceRows Fresh() => new([new Row { Name = "o0" }, new Row { Name = "o1" }, new Row { Name = "o2" }], refuseAt: 2);
        var expected = paths
            .Select(path => { var rows = Fresh(); var field = introspector.Resolve(new { Rows = rows }, path); return (field.Owner == rows ? "rows" : ((Row)field.Owner).Name, field.PropertyName); })
            .ToList();

        var model = Fresh();
        var resolved = introspector.ResolveAll(new { Rows = model }, paths)
            .Select(field => (field.Owner == model ? "rows" : ((Row)field.Owner).Name, field.PropertyName))
            .ToList();

        Assert.Equal([("o0", "Name"), ("o1", "Name"), ("rows", "[5].Name")], expected);
        Assert.Equal(expected, resolved);
    }

    /// <summary>An introspector written against <c>Resolve</c> and <c>TryReadValue</c> alone, as one written before <c>ResolveAll</c> existed was, which records every path it is asked to resolve.</summary>
    private sealed class OnePathAtATime : IModelIntrospector
    {
        private readonly ReflectionModelIntrospector _inner = new();

        public List<string> Asked { get; } = [];

        public ResolvedField Resolve(object rootModel, string propertyPath)
        {
            Asked.Add(propertyPath);
            return _inner.Resolve(rootModel, propertyPath);
        }

        public bool TryReadValue(object owner, string propertyName, out object? value, out Type? declaredType) =>
            _inner.TryReadValue(owner, propertyName, out value, out declaredType);
    }

    // The default answers through the implementation's own Resolve, once per path and in order,
    // so an implementation that does not override ResolveAll resolves exactly as it did.
    [Fact]
    public void An_introspector_that_does_not_override_ResolveAll_answers_through_its_own_Resolve()
    {
        var model = new Mixed();
        var introspector = new OnePathAtATime();
        var expected = MixedPaths.Select(path => new ReflectionModelIntrospector().Resolve(model, path)).ToList();

        var resolved = ((IModelIntrospector)introspector).ResolveAll(model, MixedPaths);

        Assert.Equal(expected, resolved);
        Assert.Equal(MixedPaths, introspector.Asked);
    }

    [Fact]
    public void The_default_refuses_a_null_model_or_a_null_list()
    {
        IModelIntrospector introspector = new OnePathAtATime();

        Assert.Throws<ArgumentNullException>(() => introspector.ResolveAll(null!, ["Title"]));
        Assert.Throws<ArgumentNullException>(() => introspector.ResolveAll(new Mixed(), null!));
    }
}
