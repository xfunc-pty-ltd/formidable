using System.Collections;
using FluentValidation;
using Formidable.Introspection;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Time.Testing;

namespace Formidable.Blazor.Tests;

/// <summary>
/// A load reads a row its shared walk of a list refused partway with a walk of its own, as a read
/// of that row alone would. The load keeps one walk of a list open across its reads, so a getter
/// read in between can change the list under it, and a version-checked list then throws at the
/// walk's next step. A walk of the row's own reads the changed list and reaches the row.
/// </summary>
// The mutation that must break it: answer a row the shared walk refused as refused, without a
// walk of its own, and the rows from the refusal on (Tags[2] and the added Tags[3]) are never
// read, so the load leaves them untouched. The pin also asserts the shared walk was refused, so it
// cannot pass with the shape under test never met.
public class LoadedValuesChangedListTests
{
    private const string TagTaken = "Tag taken";
    private const string SummaryTooLong = "Summary too long";

    // The load reads the error sites first: Tags[1], which opens the walk of Tags and leaves it at
    // that row, then Summary, whose first read outside the validator adds a tag. Counting the
    // declared Tags[] template then meets the changed list at the walk's next step, and counts four
    // rows with a walk of its own. Each row from the refusal on is read again the same way.
    [Fact]
    public async Task A_load_reads_again_the_rows_a_getter_changed_its_shared_walk_under()
    {
        var tags = new VersionedTags(["t0", "t1", "t2"]);
        var model = new Board { Tags = tags };
        var editContext = new EditContext(model);
        using var engine = Build(model, editContext);

        await engine.DiscloseLoadedValuesAsync();

        Assert.True(tags.Refusals > 0, "the load's shared walk of the list should have been refused");
        Assert.Equal(["t0", "t1", "t2", "t3"], tags);
        for (var row = 0; row < 4; row++)
        {
            Assert.True(engine.GetFieldState(Row(tags, row)).IsTouched, $"Tags[{row}] should be touched");
        }

        Assert.True(engine.GetFieldState(new FieldIdentifier(model, nameof(Board.Summary))).IsTouched);
        Assert.Equal([TagTaken], editContext.GetValidationMessages(Row(tags, 1)));
    }

    // The other side of the rule: a row past the end of a list the shared walk read whole is not
    // read again, so a list that never refuses costs the load one walk. The validator reports a
    // failure at Tags[5] of a three-row list, which the load meets as an error site and cannot
    // read. Mutation that must break it: read every row the shared walk could not reach with a
    // walk of its own, refused or not, and the error site costs a second walk.
    [Fact]
    public async Task A_row_past_the_end_of_a_list_read_whole_takes_no_walk_of_its_own()
    {
        var tags = new VersionedTags(["t0", "t1", "t2"]);
        var model = new Board { Tags = tags, ReportPastTheEnd = true };
        var editContext = new EditContext(model);
        using var engine = Build(model, editContext);

        await engine.DiscloseLoadedValuesAsync();

        Assert.Equal(0, tags.Refusals);
        Assert.Equal(1, tags.WalksOutsideValidator);
        Assert.True(engine.GetFieldState(Row(tags, 2)).IsTouched);
    }

    private static FieldIdentifier Row(VersionedTags tags, int row) =>
        new(tags, row.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public sealed class Board
    {
        private bool _changed;

        public required VersionedTags Tags { get; init; }

        /// <summary>Whether the validator also reports a failure at a row past the end of <see cref="Tags"/>, and <see cref="Summary"/> leaves the list as it is.</summary>
        public bool ReportPastTheEnd { get; init; }

        /// <summary>Adds a tag on its first read outside the validator, as a getter that builds a default row on first use does.</summary>
        public string Summary
        {
            get
            {
                if (!ReportPastTheEnd && !_changed && Tags.ValidatorDepth == 0)
                {
                    _changed = true;
                    Tags.Add("t3");
                }

                return "toolong";
            }
        }
    }

    private sealed class BoardValidator : AbstractValidator<Board>
    {
        public BoardValidator()
        {
            RuleForEach(m => m.Tags).Must(t => t != "t1").WithMessage(TagTaken);
            RuleFor(m => m.Summary).MaximumLength(3).WithMessage(SummaryTooLong);
            RuleFor(m => m).Custom((m, context) =>
            {
                if (m.ReportPastTheEnd)
                {
                    context.AddFailure("Tags[5]", TagTaken);
                }
            });
        }
    }

    /// <summary>A list known only by <see cref="IReadOnlyList{T}"/> over a <see cref="List{T}"/>, whose walk throws at its next step once the list has changed, and which counts those refusals.</summary>
    public sealed class VersionedTags(IEnumerable<string> items) : IReadOnlyList<string>
    {
        private readonly List<string> _items = [.. items];

        /// <summary>The walks refused so far because the list changed under them.</summary>
        public int Refusals { get; private set; }

        /// <summary>The walks begun while the validator was not running.</summary>
        public int WalksOutsideValidator { get; private set; }

        /// <summary>Above zero while the validator runs.</summary>
        public int ValidatorDepth { get; set; }

        public string this[int index] => _items[index];

        public int Count => _items.Count;

        public void Add(string item) => _items.Add(item);

        public IEnumerator<string> GetEnumerator()
        {
            if (ValidatorDepth == 0)
            {
                WalksOutsideValidator++;
            }

            return new Walk(this, _items.GetEnumerator());
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private sealed class Walk(VersionedTags owner, List<string>.Enumerator inner) : IEnumerator<string>
        {
            private List<string>.Enumerator _inner = inner;

            public string Current => _inner.Current;

            object IEnumerator.Current => Current;

            public bool MoveNext()
            {
                try
                {
                    return _inner.MoveNext();
                }
                catch (InvalidOperationException)
                {
                    owner.Refusals++;
                    throw;
                }
            }

            public void Reset() => throw new NotSupportedException();

            public void Dispose() => _inner.Dispose();
        }
    }

    /// <summary>The FluentValidation validator with the list told while it runs, so the getter changes the list only during the load's own reads.</summary>
    private sealed class ToldValidator(IModelValidator<Board> inner, VersionedTags tags) : DelegatingModelValidator<Board>(inner)
    {
        public override async Task<ValidationReport> ValidateAsync(Board model, ValidationProfile profile, CancellationToken cancellationToken = default)
        {
            tags.ValidatorDepth++;
            try
            {
                return await base.ValidateAsync(model, profile, cancellationToken);
            }
            finally
            {
                tags.ValidatorDepth--;
            }
        }

        public override ValidationReport Validate(Board model, ValidationProfile profile)
        {
            tags.ValidatorDepth++;
            try
            {
                return base.Validate(model, profile);
            }
            finally
            {
                tags.ValidatorDepth--;
            }
        }

        public override async Task<RuleLevelResult> ValidateRulesAsync(
            Board model, ValidationProfile profile, IReadOnlyList<RuleIdentity> rules, CancellationToken cancellationToken = default)
        {
            tags.ValidatorDepth++;
            try
            {
                return await base.ValidateRulesAsync(model, profile, rules, cancellationToken);
            }
            finally
            {
                tags.ValidatorDepth--;
            }
        }
    }

    private static FormidableEngine<Board> Build(Board model, EditContext editContext) =>
        new(
            model,
            editContext,
            new ToldValidator(new FluentValidationModelValidator<Board>(new BoardValidator()), model.Tags),
            new ReflectionModelIntrospector(),
            new FormidableOptions(),
            new FakeTimeProvider());
}
