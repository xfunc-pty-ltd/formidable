using System.Collections;
using FluentValidation;
using Formidable.Introspection;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Time.Testing;

namespace Formidable.Blazor.Tests;

/// <summary>
/// A collection whose rows are reached only by walking it (a set: no list interface, no indexer)
/// is walked a fixed number of times by each of the engine's reads of a whole report or a whole
/// set of declared fields, however many rows it holds: once to count it where a template expands,
/// once more for each further level a template opens inside its rows, and once for all of the
/// paths the read resolves. FluentValidation's own walks, made while it checks the model, are not
/// counted.
/// </summary>
// Every pin holds 2,000 rows, every row failing, and bounds the steps by the walks its read
// makes (a walk of 2,000 rows is at most 2,001 steps: a count takes the step that finds the end,
// a walk that only reads rows stops at the last row asked). Resolving one path at a time walks
// the set from the start for each row, about two million steps per read (2,003,001 for a
// template's count and its rows). The mutation that must break each pin: route its read back to
// one resolution per path.
public class WalkedCollectionSweepTests
{
    private const int Rows = 2000;

    // One walk of the whole set: a step per row, and the step that finds the end, which only a
    // count takes.
    private const int Walk = Rows + 1;

    private const string CodeTooShort = "Code too short";
    private const string NoteTooLong = "Note too long";
    private const string Taken = "Taken";

    // The required-mark rebuild expands Lines[].Code, the one demanding template: it counts the
    // set and resolves its 2,000 paths, two walks.
    [Fact]
    public void A_required_mark_rebuild_walks_the_set_twice()
    {
        using var form = Build();
        var (model, engine) = (form.Model, form.Engine);
        var row = model.Lines.ElementAt(1234);
        model.Lines.Steps = 0;

        var requirement = engine.GetFieldRequirement(new FieldIdentifier(row, nameof(Line.Code)));

        AssertWalks(model.Lines.Steps, walks: 2);
        Assert.Equal(FieldRequirement.Required, requirement);
    }

    // A nested template expands level by level: the outer set is counted, the 2,000 inner
    // collections are found with one resolve of their paths, and the 2,000 member paths are
    // resolved, three walks of the outer set.
    [Fact]
    public void A_required_mark_rebuild_over_nested_rows_walks_the_outer_set_three_times()
    {
        var teams = new CountingSet<Team>(Enumerable.Range(0, Rows).Select(_ => new Team { Members = [new Member()] }));
        var model = new Crew { Teams = teams };
        using var engine = new FormidableEngine<Crew>(
            model,
            new EditContext(model),
            new UncountedChecks<Crew>(new FluentValidationModelValidator<Crew>(new CrewValidator())),
            new ReflectionModelIntrospector(),
            new FormidableOptions(),
            new FakeTimeProvider());
        var member = teams.ElementAt(1234).Members[0];
        teams.Steps = 0;

        var requirement = engine.GetFieldRequirement(new FieldIdentifier(member, nameof(Member.Alias)));

        AssertWalks(teams.Steps, walks: 3);
        Assert.Equal(FieldRequirement.Required, requirement);
    }

    // A submit's start reads its "checking" scope from every declared path: Lines[].Code and
    // Lines[].Note share one count of the set, and their 4,000 paths are resolved together.
    [Fact]
    public async Task A_submit_start_walks_the_set_twice()
    {
        using var form = Build();
        var (model, engine, slow) = (form.Model, form.Engine, form.Slow);
        slow.Hold();
        var row = new FieldIdentifier(model.Lines.ElementAt(1234), nameof(Line.Code));
        model.Lines.Steps = 0;

        var submit = engine.ValidateForSubmitAsync();
        var steps = model.Lines.Steps;

        AssertWalks(steps, walks: 2);
        Assert.True(engine.GetFieldState(row).IsValidating);
        slow.Release();
        Assert.False((await submit).CanProceed);
    }

    // A move in the rendered field set while a submit runs reads the scope again, as its start did.
    [Fact]
    public async Task A_field_set_move_while_a_submit_runs_walks_the_set_twice()
    {
        using var form = Build();
        var (model, engine, slow) = (form.Model, form.Engine, form.Slow);
        slow.Hold();
        var submit = engine.ValidateForSubmitAsync();
        model.Lines.Steps = 0;

        engine.OnRenderedFieldsChanged();

        AssertWalks(model.Lines.Steps, walks: 2);
        Assert.True(engine.GetFieldState(new FieldIdentifier(model.Lines.ElementAt(1234), nameof(Line.Code))).IsValidating);
        slow.Release();
        Assert.False((await submit).CanProceed);
    }

    // A blocked submit lands its errors and warnings with one walk. Nothing renders the rows, so
    // every error is reported as suppressed, and the never-registered diagnostic asks about a
    // field the landing already holds without resolving its path again. The publish then reads
    // which fields the submit rules fail from the answers on record (one walk).
    [Fact]
    public async Task A_blocked_submit_landing_walks_the_set_twice()
    {
        var neverRegistered = 0;
        using var form = Build(options => options.NeverRegisteredFieldDiagnostic = _ => neverRegistered++);
        var (model, engine, slow) = (form.Model, form.Engine, form.Slow);
        slow.Hold();
        var submit = engine.ValidateForSubmitAsync();
        model.Lines.Steps = 0;

        slow.Release();
        var outcome = await submit;

        AssertWalks(model.Lines.Steps, walks: 2);
        Assert.False(outcome.CanProceed);
        Assert.Equal(Rows, outcome.Report.Errors.Count);
        Assert.Equal(Rows, neverRegistered);
    }

    // A passing submit with a warning on every row: its scope (two walks) and its landing, which
    // resolves the warnings (one).
    [Fact]
    public async Task A_passing_submit_walks_the_set_three_times()
    {
        using var form = Build(code: "abc");
        var (model, engine) = (form.Model, form.Engine);
        model.Lines.Steps = 0;

        var outcome = await engine.ValidateForSubmitAsync();

        AssertWalks(model.Lines.Steps, walks: 3);
        Assert.True(outcome.CanProceed);
        Assert.Equal(Rows, outcome.Report.Advisories.Count);
    }

    // A keystroke's live check answers the whole model, so its landing resolves every row's error
    // and warning (one walk), and the publish reads which fields the submit rules fail from the
    // answers on record, for each field's state (one).
    [Fact]
    public void A_keystroke_walks_the_set_twice()
    {
        using var form = Build();
        var (model, editContext, engine) = (form.Model, form.EditContext, form.Engine);
        var row = new FieldIdentifier(model.Lines.ElementAt(1234), nameof(Line.Code));
        model.Lines.Steps = 0;

        editContext.NotifyFieldChanged(row);

        Assert.False(engine.IsValidating);
        AssertWalks(model.Lines.Steps, walks: 2);
        Assert.Equal([CodeTooShort], editContext.GetValidationMessages(row));
        Assert.False(engine.GetFieldState(row).WouldPassSubmit);
    }

    // A load lands its report (one walk), counts the set and resolves every declared path to read
    // the loaded values (two), and its live check lands (one); each landing's publish reads which
    // fields the submit rules fail (one each).
    [Fact]
    public async Task A_load_walks_the_set_six_times()
    {
        using var form = Build();
        var (model, editContext, engine) = (form.Model, form.EditContext, form.Engine);
        var row = new FieldIdentifier(model.Lines.ElementAt(1234), nameof(Line.Code));
        model.Lines.Steps = 0;

        await engine.DiscloseLoadedValuesAsync();

        AssertWalks(model.Lines.Steps, walks: 6);
        Assert.True(engine.GetFieldState(row).IsTouched);
        Assert.Equal([CodeTooShort], editContext.GetValidationMessages(row));
    }

    // The whole-form re-check a field-set move arms lands its report (one walk), and its publish
    // reads which fields the submit rules fail (one).
    [Fact]
    public void A_whole_form_recheck_landing_walks_the_set_twice()
    {
        var time = new FakeTimeProvider();
        using var form = Build(time: time);
        var (model, engine) = (form.Model, form.Engine);
        engine.OnRenderedFieldsChanged();
        model.Lines.Steps = 0;

        time.Advance(engine.Options.RefreshDebounce);

        Assert.False(engine.IsValidating);
        AssertWalks(model.Lines.Steps, walks: 2);
    }

    // A validator the engine cannot check rule by rule: the validity check a tracking form starts
    // as it is built resolves its report's errors once, for the valid state class to read.
    [Fact]
    public void A_validity_check_without_rule_by_rule_checks_walks_the_set_once()
    {
        var model = NewSheet("x");
        var editContext = new EditContext(model);

        using var engine = new FormidableEngine<Sheet>(
            model,
            editContext,
            new UncountedChecks<Sheet>(new FluentValidationModelValidator<Sheet>(new SheetValidator(new SlowGate())), byRule: false),
            new ReflectionModelIntrospector(),
            new FormidableOptions { TrackFormValidity = true },
            new FakeTimeProvider());

        AssertWalks(model.Lines.Steps, walks: 1);
        Assert.False(engine.IsFormValid);
    }

    // A server reply naming every row is resolved with one walk.
    [Fact]
    public void A_server_reply_naming_every_row_walks_the_set_once()
    {
        using var form = Build();
        var (model, editContext, engine) = (form.Model, form.EditContext, form.Engine);
        model.Lines.Steps = 0;

        engine.ApplyServerIssues(Enumerable.Range(0, Rows).Select(i => new ValidationIssue($"Lines[{i}].Code", Taken)));

        AssertWalks(model.Lines.Steps, walks: 1);
        Assert.Equal([Taken], editContext.GetValidationMessages(new FieldIdentifier(model.Lines.ElementAt(1234), nameof(Line.Code))));
    }

    // Every row leaves the page at once under EngagedAndVisible, with an override that keeps each
    // row's message showing: whether each row's message still names it is read with one walk.
    [Fact]
    public async Task Rows_leaving_at_once_under_a_keeping_override_walk_the_set_once()
    {
        using var form = Build(options =>
        {
            options.LiveDisclosure = LiveIssueDisclosure.EngagedAndVisible;
            options.DisclosureOverride = _ => true;
        });
        var (model, editContext, engine) = (form.Model, form.EditContext, form.Engine);
        var registrations = model.Lines
            .Select(line => engine.Registry.Register(new FieldIdentifier(line, nameof(Line.Code))))
            .ToList();
        await engine.DiscloseLoadedValuesAsync();
        foreach (var registration in registrations)
        {
            registration.Dispose();
        }

        model.Lines.Steps = 0;

        engine.OnRenderedFieldsChanged();

        AssertWalks(model.Lines.Steps, walks: 1);
        Assert.Equal([CodeTooShort], editContext.GetValidationMessages(new FieldIdentifier(model.Lines.ElementAt(1234), nameof(Line.Code))));
    }

    // An introspector written against Resolve and TryReadValue alone, as one written before the
    // batch member existed was, still places the required marks, "checking", the load's messages
    // and a server reply's where the reflection introspector does: the batch member's default
    // answers through its Resolve.
    [Fact]
    public async Task An_introspector_resolving_one_path_at_a_time_places_everything_as_before()
    {
        var oneAtATime = new OnePathAtATime();

        var expected = await Observe(new ReflectionModelIntrospector());
        var observed = await Observe(oneAtATime);

        Assert.Equal(expected, observed);
        Assert.Contains("Lines[3].Code", oneAtATime.Asked);
        Assert.Contains(CodeTooShort, observed);
        Assert.Contains(Taken, observed);
    }

    private static async Task<List<string>> Observe(IModelIntrospector introspector)
    {
        var model = new Sheet { Lines = new CountingSet<Line>(Enumerable.Range(0, 5).Select(_ => new Line { Code = "x", Note = "n" })) };
        var editContext = new EditContext(model);
        var slow = new SlowGate();
        using var engine = new FormidableEngine<Sheet>(
            model,
            editContext,
            new UncountedChecks<Sheet>(new FluentValidationModelValidator<Sheet>(new SheetValidator(slow))),
            introspector,
            new FormidableOptions(),
            new FakeTimeProvider());
        var lines = model.Lines.ToList();
        var observed = new List<string>();
        observed.AddRange(lines.Select(line => engine.GetFieldRequirement(new FieldIdentifier(line, nameof(Line.Code))).ToString()));

        slow.Hold();
        var submit = engine.ValidateForSubmitAsync();
        observed.AddRange(lines.Select(line => engine.GetFieldState(new FieldIdentifier(line, nameof(Line.Code))).IsValidating.ToString()));
        slow.Release();
        await submit;

        await engine.DiscloseLoadedValuesAsync();
        engine.ApplyServerIssues([new ValidationIssue("Lines[3].Code", Taken)]);
        foreach (var line in lines)
        {
            observed.AddRange(editContext.GetValidationMessages(new FieldIdentifier(line, nameof(Line.Code))));
            observed.Add("|");
        }

        return observed;
    }

    /// <summary>An introspector written against <c>Resolve</c> and <c>TryReadValue</c> alone, which records every path it resolves.</summary>
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

    private static void AssertWalks(long steps, int walks) =>
        Assert.True(
            steps >= Rows && steps <= (long)walks * Walk,
            $"{steps} steps over a set of {Rows} rows; expected at least a walk to the last row ({Rows}) and at most {walks} walks ({(long)walks * Walk})");

    private static Sheet NewSheet(string code) =>
        new() { Lines = new CountingSet<Line>(Enumerable.Range(0, Rows).Select(_ => new Line { Code = code, Note = "long" })) };

    private static Form Build(
        Action<FormidableOptions>? configure = null, string code = "x", FakeTimeProvider? time = null)
    {
        var model = NewSheet(code);
        var editContext = new EditContext(model);
        var slow = new SlowGate();
        var options = new FormidableOptions();
        configure?.Invoke(options);
        var engine = new FormidableEngine<Sheet>(
            model,
            editContext,
            new UncountedChecks<Sheet>(new FluentValidationModelValidator<Sheet>(new SheetValidator(slow))),
            new ReflectionModelIntrospector(),
            options,
            time ?? new FakeTimeProvider());
        return new Form(model, editContext, engine, slow);
    }

    private sealed record Form(Sheet Model, EditContext EditContext, FormidableEngine<Sheet> Engine, SlowGate Slow) : IDisposable
    {
        public void Dispose() => Engine.Dispose();
    }

    public sealed class Sheet
    {
        public string? Slow { get; set; } = "ok";

        public required CountingSet<Line> Lines { get; init; }
    }

    public sealed class Line
    {
        public string? Code { get; set; }

        public string? Note { get; set; }
    }

    public sealed class Crew
    {
        public required CountingSet<Team> Teams { get; init; }
    }

    public sealed class Team
    {
        public required List<Member> Members { get; init; }
    }

    public sealed class Member
    {
        public string? Alias { get; set; } = "ok";
    }

    /// <summary>Holds the slow rule's check open while a test needs a submit running; open otherwise.</summary>
    public sealed class SlowGate
    {
        private TaskCompletionSource? _held;

        public Task Open => _held?.Task ?? Task.CompletedTask;

        public void Hold() => _held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _held?.SetResult();
    }

    private sealed class SheetValidator : AbstractValidator<Sheet>
    {
        public SheetValidator(SlowGate slow)
        {
            // Declared first, so a held submit waits here before any row rule runs.
            RuleFor(m => m.Slow).MustAsync(async (_, ct) =>
            {
                await slow.Open.WaitAsync(ct);
                return true;
            });
            RuleForEach(m => m.Lines).SetValidator(new LineValidator());
        }
    }

    private sealed class LineValidator : AbstractValidator<Line>
    {
        public LineValidator()
        {
            RuleFor(l => l.Code).NotEmpty().MinimumLength(3).WithMessage(CodeTooShort);
            RuleFor(l => l.Note).MaximumLength(2).WithSeverity(Severity.Warning).WithMessage(NoteTooLong);
        }
    }

    private sealed class CrewValidator : AbstractValidator<Crew>
    {
        public CrewValidator() =>
            RuleForEach(m => m.Teams).ChildRules(team =>
                team.RuleForEach(t => t.Members).ChildRules(member => member.RuleFor(x => x.Alias).NotEmpty()));
    }

    /// <summary>Whether the code running is FluentValidation checking the model, whose walks of a counting set are not counted.</summary>
    // Flow-local, so a held check stays marked as its own while the test goes on outside it: the
    // engine's reads made beside a running submit are counted.
    private static readonly AsyncLocal<bool> InsideCheck = new();

    /// <summary>A set of rows as the engine meets a <see cref="HashSet{T}"/>: no list interface, no indexer and no non-generic count, so it is reached only by walking it. Counts the steps of every walk made outside a check.</summary>
    public sealed class CountingSet<T>(IEnumerable<T> items) : ICollection<T>, IReadOnlyCollection<T>
    {
        private readonly HashSet<T> _items = [.. items];

        /// <summary>The <c>MoveNext</c> calls made by walks outside a check.</summary>
        public long Steps { get; set; }

        public int Count => _items.Count;

        public bool IsReadOnly => false;

        public void Add(T item) => _items.Add(item);

        public void Clear() => _items.Clear();

        public bool Contains(T item) => _items.Contains(item);

        public void CopyTo(T[] array, int arrayIndex) => _items.CopyTo(array, arrayIndex);

        public bool Remove(T item) => _items.Remove(item);

        public IEnumerator<T> GetEnumerator() =>
            InsideCheck.Value ? _items.GetEnumerator() : new CountingEnumerator(this, _items.GetEnumerator());

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private sealed class CountingEnumerator(CountingSet<T> owner, IEnumerator<T> inner) : IEnumerator<T>
        {
            public T Current => inner.Current;

            object? IEnumerator.Current => Current;

            public bool MoveNext()
            {
                owner.Steps++;
                return inner.MoveNext();
            }

            public void Reset() => inner.Reset();

            public void Dispose() => inner.Dispose();
        }
    }

    /// <summary>The FluentValidation validator with its checks marked, so their walks go uncounted, and its rule-by-rule seam optionally withdrawn.</summary>
    private sealed class UncountedChecks<TModel>(IModelValidator<TModel> inner, bool byRule = true)
        : DelegatingModelValidator<TModel>(inner)
    {
        public override bool CanValidateByRule => byRule && base.CanValidateByRule;

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
