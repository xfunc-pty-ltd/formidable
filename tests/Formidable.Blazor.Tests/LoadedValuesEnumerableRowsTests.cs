using System.Collections;
using FluentValidation;
using Formidable.Introspection;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Time.Testing;

namespace Formidable.Blazor.Tests;

/// <summary>
/// A load over a list the engine can only walk (one known by <see cref="IReadOnlyList{T}"/> alone,
/// whose indexer the engine cannot call without knowing the element type) walks that list once,
/// however many of its rows the load reads, and reads each row it reaches as a walk of its own
/// would.
/// </summary>
public class LoadedValuesEnumerableRowsTests
{
    private const string RowTooLong = "Row too long";

    // Two thousand rows, every one read by the load: row 1234 fails and is disclosed, and the
    // rows either side of it pass and are confirmed. The validator's own walks are
    // FluentValidation's and are not counted. Mutation that must break it: give every row read a
    // walk of its own (TryReadElement's walk without the load's record of walked lists), and the
    // list is walked once per row read, about two million steps.
    [Fact]
    public async Task A_load_enumerates_an_enumerable_only_collection_once()
    {
        var rows = new CountingRows(Enumerable.Range(0, 2000).Select(i => i == 1234 ? "toolong" : $"r{i}"));
        var model = new Roster { Rows = rows };
        var editContext = new EditContext(model);
        using var engine = Build(model, editContext, inspectRules: true);

        await engine.DiscloseLoadedValuesAsync();

        Assert.True(
            rows.GetEnumeratorCalls == 1 && rows.MoveNextCalls <= 2001,
            $"GetEnumerator {rows.GetEnumeratorCalls}, MoveNext {rows.MoveNextCalls}");
        Assert.Contains("formidable-valid", Classes(editContext, rows, 0));
        Assert.Contains("formidable-invalid", Classes(editContext, rows, 1234));
        Assert.Contains("formidable-valid", Classes(editContext, rows, 1999));
    }

    // A walk that refuses at the third row: the rows before the refusal are read and the rows
    // from it on are not, as when each row had a walk of its own. Rows 1 and 3 fail, and only
    // row 1 is disclosed. Rule inspection is withdrawn so the load reads the error sites alone
    // (counting the list would meet the same refusal). Mutation that must break it: keep nothing
    // of a walk that refuses partway, and row 1 is never read, so it shows nothing.
    [Fact]
    public async Task A_load_reads_the_rows_before_a_walk_refuses()
    {
        var rows = new CountingRows(["ok", "toolong", "ok", "toolong"]) { RefuseAt = 2 };
        var model = new Roster { Rows = rows };
        var editContext = new EditContext(model);
        using var engine = Build(model, editContext, inspectRules: false);

        await engine.DiscloseLoadedValuesAsync();

        Assert.Contains("formidable-invalid", Classes(editContext, rows, 1));
        Assert.Empty(Classes(editContext, rows, 3));
    }

    public sealed class Roster
    {
        public required CountingRows Rows { get; init; }
    }

    private sealed class RosterValidator : AbstractValidator<Roster>
    {
        public RosterValidator() =>
            RuleForEach(m => m.Rows).NotEmpty().MaximumLength(5).WithMessage(RowTooLong);
    }

    /// <summary>A list known only by <see cref="IReadOnlyList{T}"/> that counts the walks made over it while the validator is not running, and can refuse such a walk at a position.</summary>
    public sealed class CountingRows(IEnumerable<string?> items) : IReadOnlyList<string?>
    {
        private readonly List<string?> _items = [.. items];

        public int GetEnumeratorCalls { get; private set; }

        public int MoveNextCalls { get; private set; }

        /// <summary>The position at which a walk made outside the validator throws instead of moving on; <see langword="null"/> for none.</summary>
        public int? RefuseAt { get; init; }

        /// <summary>Above zero while the validator runs, whose walks are neither counted nor refused.</summary>
        public int ValidatorDepth { get; set; }

        public string? this[int index] => _items[index];

        public int Count => _items.Count;

        public IEnumerator<string?> GetEnumerator()
        {
            if (ValidatorDepth > 0)
            {
                return _items.GetEnumerator();
            }

            GetEnumeratorCalls++;
            return new CountingEnumerator(this, _items.GetEnumerator());
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private sealed class CountingEnumerator(CountingRows owner, IEnumerator<string?> inner) : IEnumerator<string?>
        {
            private int _position;

            public string? Current => inner.Current;

            object? IEnumerator.Current => Current;

            public bool MoveNext()
            {
                owner.MoveNextCalls++;
                if (_position == owner.RefuseAt)
                {
                    throw new InvalidOperationException("The list refuses the walk here.");
                }

                var moved = inner.MoveNext();
                if (moved)
                {
                    _position++;
                }

                return moved;
            }

            public void Reset() => throw new NotSupportedException();

            public void Dispose() => inner.Dispose();
        }
    }

    /// <summary>The FluentValidation validator with the list's counting paused while it runs, and its rule inspection optionally withdrawn.</summary>
    private sealed class UncountedValidator(IModelValidator<Roster> inner, CountingRows rows, bool inspectRules)
        : DelegatingModelValidator<Roster>(inner)
    {
        public override bool CanInspectRules => inspectRules && base.CanInspectRules;

        public override async Task<ValidationReport> ValidateAsync(Roster model, ValidationProfile profile, CancellationToken cancellationToken = default)
        {
            rows.ValidatorDepth++;
            try
            {
                return await base.ValidateAsync(model, profile, cancellationToken);
            }
            finally
            {
                rows.ValidatorDepth--;
            }
        }

        public override ValidationReport Validate(Roster model, ValidationProfile profile)
        {
            rows.ValidatorDepth++;
            try
            {
                return base.Validate(model, profile);
            }
            finally
            {
                rows.ValidatorDepth--;
            }
        }

        public override async Task<RuleLevelResult> ValidateRulesAsync(
            Roster model, ValidationProfile profile, IReadOnlyList<RuleIdentity> rules, CancellationToken cancellationToken = default)
        {
            rows.ValidatorDepth++;
            try
            {
                return await base.ValidateRulesAsync(model, profile, rules, cancellationToken);
            }
            finally
            {
                rows.ValidatorDepth--;
            }
        }
    }

    private static FormidableEngine<Roster> Build(Roster model, EditContext editContext, bool inspectRules) =>
        new(
            model,
            editContext,
            new UncountedValidator(new FluentValidationModelValidator<Roster>(new RosterValidator()), model.Rows, inspectRules),
            new ReflectionModelIntrospector(),
            new FormidableOptions(),
            new FakeTimeProvider());

    private static string[] Classes(EditContext editContext, CountingRows rows, int row) =>
        editContext.FieldCssClass(new FieldIdentifier(rows, row.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
}
