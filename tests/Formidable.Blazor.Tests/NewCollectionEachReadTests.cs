using System.Collections;
using FluentValidation;
using Formidable.Introspection;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Time.Testing;

namespace Formidable.Blazor.Tests;

/// <summary>
/// A model member that builds a new collection on each read (a getter returning
/// <c>_rows.Where(...)</c>) gives the engine nothing to share between its reads of one report or
/// one set of declared fields, so each row is reached by a walk of its own: no more steps than
/// reading one path, or one element, at a time, and no more than one walk open at once. A list
/// that throws as its walk is closed is read as reading one element at a time reads it.
/// FluentValidation's own walks, made while it checks the model, are not counted.
/// </summary>
// Mutations that must break these: share a walk per instance whatever the place it came from
// (every walk stays open until the read ends), walk a recorded collection to its end (the first
// read's walk passes its row), and drop the second reading when closing a walk threw (the list's
// rows are read from the shared walk).
public class NewCollectionEachReadTests
{
    private const int Rows = 2000;
    private const string CodeTooShort = "Code too short";
    private const string TagTooLong = "Tag too long";
    private const string GoodTooLong = "Good too long";

    // A keystroke's live check resolves every row's error and the publish reads which fields the
    // submit rules fail, both through the introspector. Against the same keystroke through an
    // introspector that resolves one path at a time, it takes no more steps and holds one walk.
    [Fact]
    public void A_keystroke_over_rows_built_afresh_costs_no_more_than_one_path_at_a_time()
    {
        var batch = Keystroke(new ReflectionModelIntrospector());
        var oneAtATime = Keystroke(new OnePathAtATime());

        Assert.True(
            batch.Steps <= oneAtATime.Steps,
            $"{batch.Steps} steps, against {oneAtATime.Steps} one path at a time");
        Assert.True(batch.Steps >= (long)Rows * (Rows + 1) / 2, $"{batch.Steps} steps: each row should have been walked to");
        Assert.True(batch.MaxOpen <= 1, $"{batch.MaxOpen} walks open at once");
        Assert.Equal(0, batch.Open);
        Assert.Equal([CodeTooShort], batch.Messages);
        Assert.Equal(oneAtATime.Messages, batch.Messages);
    }

    // A load reads each failing row and each declared row of a list known only by
    // IReadOnlyList<T>, which the engine reads by walking. Built afresh on each read, every read
    // walks to its own row: the error sites, the count, then the declared rows, no more than
    // that sum, with one walk open at a time. (Values in such a list have no identity that
    // survives a read, so their messages cannot be filed; only the reading's cost is pinned.)
    [Fact]
    public async Task A_load_over_a_list_built_afresh_reads_no_more_than_one_element_at_a_time()
    {
        var counter = new WalkCounter();
        var model = new Tagged(Enumerable.Range(0, Rows).Select(_ => "toolong"), counter, closeThrows: false);
        var editContext = new EditContext(model);
        using var engine = Build(model, editContext);
        counter.Reset();

        await engine.DiscloseLoadedValuesAsync();

        long perRow = (long)Rows * (Rows + 1) / 2;
        var oneElementAtATime = perRow + (Rows + 1) + perRow;
        Assert.True(
            counter.Steps <= oneElementAtATime,
            $"{counter.Steps} steps, against {oneElementAtATime} one element at a time");
        Assert.True(counter.Steps >= perRow, $"{counter.Steps} steps: the declared rows should each have been read");
        Assert.True(counter.MaxOpen <= 1, $"{counter.MaxOpen} walks open at once");
        Assert.Equal(0, counter.Open);
        Assert.True(engine.GetFieldState(new FieldIdentifier(model.Name, nameof(Label.Value))).IsTouched);
    }

    // A list whose walk throws as it is closed refuses every read of one element, since that read
    // closes its walk before it answers. A load reads it that way: none of its rows is adopted,
    // and the field beside it is. Mutation that must break it: drop the second reading when
    // closing a walk threw, and the failing row is read from the shared walk and disclosed.
    [Fact]
    public async Task A_load_over_a_list_that_throws_as_its_walk_is_closed_reads_as_one_element_at_a_time_does()
    {
        var counter = new WalkCounter();
        var model = new Tagged(["ok", "toolong"], counter, closeThrows: true);
        var editContext = new EditContext(model);
        using var engine = Build(model, editContext);

        await engine.DiscloseLoadedValuesAsync();

        Assert.True(engine.GetFieldState(new FieldIdentifier(model.Name, nameof(Label.Value))).IsTouched);
        Assert.DoesNotContain(TagTooLong, engine.GetVisibleIssues().Select(issue => issue.Issue.Message));
        Assert.True(counter.CloseRefusals > 0, "the list should have refused to close a walk");
    }

    // A load reads a stable 2,000-row list beside a list that throws as its walk is closed. The
    // load reads again with the list that threw read one element at a time, so the stable list
    // costs a second walk, not a walk per row, and its failing row still shows. Mutation that must
    // break it: read every list one element at a time in the second run.
    [Fact]
    public async Task A_load_over_a_list_beside_one_that_throws_as_its_walk_is_closed_walks_it_twice()
    {
        var goodWalks = new WalkCounter();
        var badWalks = new WalkCounter();
        var model = new TwoLists(
            new CountedRows<string?>([.. Enumerable.Range(0, Rows).Select(_ => "toolong")], goodWalks, closeThrows: false),
            new CountedRows<string?>(["ok", "toolong"], badWalks, closeThrows: true));
        var editContext = new EditContext(model);
        using var engine = new FormidableEngine<TwoLists>(
            model,
            editContext,
            new UncountedChecks<TwoLists>(new FluentValidationModelValidator<TwoLists>(new TwoListsValidator())),
            new ReflectionModelIntrospector(),
            new FormidableOptions(),
            new FakeTimeProvider());
        goodWalks.Reset();

        await engine.DiscloseLoadedValuesAsync();

        Assert.True(
            goodWalks.Steps >= Rows && goodWalks.Steps <= 2L * (Rows + 1),
            $"{goodWalks.Steps} steps over the stable list; expected at most two walks ({2L * (Rows + 1)})");
        Assert.True(badWalks.CloseRefusals > 0, "the other list should have refused to close a walk");
        var shown = engine.GetVisibleIssues().Select(issue => issue.Issue.Message).ToList();
        Assert.Contains(GoodTooLong, shown);
        Assert.DoesNotContain(TagTooLong, shown);
    }

    private static (long Steps, int MaxOpen, int Open, List<string> Messages) Keystroke(IModelIntrospector introspector)
    {
        var counter = new WalkCounter();
        var model = new Sheet(Enumerable.Range(0, Rows).Select(_ => new Line { Code = "x" }), counter);
        var editContext = new EditContext(model);
        using var engine = new FormidableEngine<Sheet>(
            model,
            editContext,
            new UncountedChecks<Sheet>(new FluentValidationModelValidator<Sheet>(new SheetValidator())),
            introspector,
            new FormidableOptions(),
            new FakeTimeProvider());
        var row = new FieldIdentifier(model.At(1234), nameof(Line.Code));
        counter.Reset();

        editContext.NotifyFieldChanged(row);

        Assert.False(engine.IsValidating);
        return (counter.Steps, counter.MaxOpen, counter.Open, [.. editContext.GetValidationMessages(row)]);
    }

    private static FormidableEngine<Tagged> Build(Tagged model, EditContext editContext) =>
        new(
            model,
            editContext,
            new UncountedChecks<Tagged>(new FluentValidationModelValidator<Tagged>(new TaggedValidator())),
            new ReflectionModelIntrospector(),
            new FormidableOptions(),
            new FakeTimeProvider());

    public sealed class Line
    {
        public string? Code { get; set; }
    }

    /// <summary>A model whose rows come from a getter that builds a new collection over the same rows on every read.</summary>
    public sealed class Sheet(IEnumerable<Line> lines, WalkCounter counter)
    {
        private readonly List<Line> _lines = [.. lines];

        public IEnumerable<Line> Lines => new CountedSequence<Line>(_lines, counter);

        public Line At(int index) => _lines[index];
    }

    public sealed class Label
    {
        public string? Value { get; set; } = "abc";
    }

    /// <summary>A model with a list of values known only by <see cref="IReadOnlyList{T}"/>, built afresh on every read, or kept as one instance whose walks throw as they are closed.</summary>
    public sealed class Tagged
    {
        private readonly List<string?> _tags;
        private readonly WalkCounter _counter;
        private readonly CountedRows<string?>? _kept;

        public Tagged(IEnumerable<string?> tags, WalkCounter counter, bool closeThrows)
        {
            _tags = [.. tags];
            _counter = counter;
            _kept = closeThrows ? new CountedRows<string?>(_tags, counter, closeThrows: true) : null;
        }

        public Label Name { get; } = new();

        public IReadOnlyList<string?> Tags => _kept ?? new CountedRows<string?>(_tags, _counter, closeThrows: false);
    }

    private sealed class SheetValidator : AbstractValidator<Sheet>
    {
        public SheetValidator() =>
            RuleForEach(m => m.Lines).ChildRules(line => line.RuleFor(l => l.Code).NotEmpty().MinimumLength(3).WithMessage(CodeTooShort));
    }

    /// <summary>A model with two lists of values known only by <see cref="IReadOnlyList{T}"/>, each one instance.</summary>
    public sealed class TwoLists(IReadOnlyList<string?> good, IReadOnlyList<string?> bad)
    {
        public IReadOnlyList<string?> Good { get; } = good;

        public IReadOnlyList<string?> Bad { get; } = bad;
    }

    private sealed class TwoListsValidator : AbstractValidator<TwoLists>
    {
        public TwoListsValidator()
        {
            RuleForEach(m => m.Good).NotEmpty().MaximumLength(3).WithMessage(GoodTooLong);
            RuleForEach(m => m.Bad).NotEmpty().MaximumLength(3).WithMessage(TagTooLong);
        }
    }

    private sealed class TaggedValidator : AbstractValidator<Tagged>
    {
        public TaggedValidator()
        {
            RuleFor(m => m.Name.Value).NotEmpty();
            RuleForEach(m => m.Tags).NotEmpty().MaximumLength(3).WithMessage(TagTooLong);
        }
    }

    /// <summary>Whether the code running is FluentValidation checking the model, whose walks are not counted.</summary>
    private static readonly AsyncLocal<bool> InsideCheck = new();

    /// <summary>The walks made outside a check over every collection a model builds: steps, walks open now and at most, and closings refused.</summary>
    public sealed class WalkCounter
    {
        public long Steps { get; private set; }

        public int Open { get; private set; }

        public int MaxOpen { get; private set; }

        public int CloseRefusals { get; private set; }

        public void Reset()
        {
            Steps = 0;
            MaxOpen = Open;
        }

        public void Opened()
        {
            Open++;
            MaxOpen = Math.Max(MaxOpen, Open);
        }

        public void Stepped() => Steps++;

        public void Closed(bool refused)
        {
            Open--;
            if (refused)
            {
                CloseRefusals++;
            }
        }
    }

    /// <summary>A collection known only by <see cref="IEnumerable{T}"/> (no indexer, so the introspector reaches its rows only by walking it), which counts the walks made over it outside a check.</summary>
    public sealed class CountedSequence<T>(List<T> rows, WalkCounter counter) : IEnumerable<T>
    {
        private readonly CountedRows<T> _walks = new(rows, counter, closeThrows: false);

        public IEnumerator<T> GetEnumerator() => _walks.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>A collection known only by <see cref="IReadOnlyList{T}"/> (an indexer the introspector can call, none the engine can), which counts the walks made over it outside a check and can refuse to close one.</summary>
    public sealed class CountedRows<T>(List<T> rows, WalkCounter counter, bool closeThrows) : IReadOnlyList<T>
    {
        public T this[int index] => rows[index];

        public int Count => rows.Count;

        public IEnumerator<T> GetEnumerator() =>
            InsideCheck.Value ? rows.GetEnumerator() : new Walk(rows, counter, closeThrows);

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private sealed class Walk : IEnumerator<T>
        {
            private readonly List<T> _rows;
            private readonly WalkCounter _counter;
            private readonly bool _closeThrows;
            private int _position = -1;
            private bool _closed;

            public Walk(List<T> rows, WalkCounter counter, bool closeThrows)
            {
                _rows = rows;
                _counter = counter;
                _closeThrows = closeThrows;
                counter.Opened();
            }

            public T Current => _rows[_position];

            object? IEnumerator.Current => Current;

            public bool MoveNext()
            {
                _counter.Stepped();
                if (_position + 1 == _rows.Count)
                {
                    return false;
                }

                _position++;
                return true;
            }

            public void Reset() => throw new NotSupportedException();

            public void Dispose()
            {
                if (_closed)
                {
                    return;
                }

                _closed = true;
                _counter.Closed(_closeThrows);
                if (_closeThrows)
                {
                    throw new InvalidOperationException("The list refuses to close the walk.");
                }
            }
        }
    }

    /// <summary>An introspector written against <c>Resolve</c> and <c>TryReadValue</c> alone, which resolves a batch one path at a time.</summary>
    private sealed class OnePathAtATime : IModelIntrospector
    {
        private readonly ReflectionModelIntrospector _inner = new();

        public ResolvedField Resolve(object rootModel, string propertyPath) => _inner.Resolve(rootModel, propertyPath);

        public bool TryReadValue(object owner, string propertyName, out object? value, out Type? declaredType) =>
            _inner.TryReadValue(owner, propertyName, out value, out declaredType);
    }

    /// <summary>The FluentValidation validator with its checks marked, so their walks go uncounted.</summary>
    private sealed class UncountedChecks<TModel>(IModelValidator<TModel> inner) : DelegatingModelValidator<TModel>(inner)
    {
        public override async Task<ValidationReport> ValidateAsync(TModel model, ValidationProfile profile, CancellationToken cancellationToken = default)
        {
            InsideCheck.Value = true;
            return await base.ValidateAsync(model, profile, cancellationToken);
        }

        public override ValidationReport Validate(TModel model, ValidationProfile profile)
        {
            var outer = InsideCheck.Value;
            InsideCheck.Value = true;
            try
            {
                return base.Validate(model, profile);
            }
            finally
            {
                InsideCheck.Value = outer;
            }
        }

        public override async Task<RuleLevelResult> ValidateRulesAsync(
            TModel model, ValidationProfile profile, IReadOnlyList<RuleIdentity> rules, CancellationToken cancellationToken = default)
        {
            InsideCheck.Value = true;
            return await base.ValidateRulesAsync(model, profile, rules, cancellationToken);
        }
    }
}
