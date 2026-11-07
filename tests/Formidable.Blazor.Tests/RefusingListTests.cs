using System.Collections;
using FluentValidation;
using Formidable.Introspection;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Time.Testing;

namespace Formidable.Blazor.Tests;

/// <summary>
/// A list the engine can read only by walking it (one known by <see cref="IReadOnlyList{T}"/>
/// alone) that FluentValidation enumerates but that refuses the engine's own walk contributes no
/// declared paths, as a list that cannot be read at all contributes none. The rest of the form
/// keeps its answers: a load lands whole, the other fields keep their required marks, and a
/// submit's "checking" stays on the fields the validator declares.
/// </summary>
// The mutation that must break all four: remove the catch around CollectionCount's walk, and the
// refusal escapes it again (the load faults half-applied, every required mark goes, and a submit
// lights every field). Each pin also asserts the list refused at least one walk, so it cannot pass
// with the walk under test never refused.
public class RefusingListTests
{
    private const string NameTooLong = "Name too long";
    private const string AliasTooLong = "Alias too long";
    private const string TagTooLong = "Tag too long";
    private const string Taken = "Taken";

    // A reply on Notes, then a load. The load adopts Name (failing, so its message shows) and
    // Email (passing), and the reply goes from the engine's views and the EditContext store alike,
    // as it does over a model with no list. The list's failing row is an error site the load
    // cannot read, so it stays silent, as an unreadable row does. The two views are compared
    // whole rather than against a fixed list: nothing renders here, so the reply on an unrendered
    // field arms the hidden-error gate, and its sentence stands beside Name's message in both.
    [Fact]
    public async Task A_load_over_a_refusing_list_lands_whole()
    {
        var walks = new WalkGate();
        var model = new Booking
        {
            Name = "toolong",
            Email = "a@b.c",
            Tags = new RefusingList<string?>(walks, ["ab", "toolong"]),
        };
        var editContext = new EditContext(model);
        using var engine = Build(model, editContext, new BookingValidator(), walks);
        var name = new FieldIdentifier(model, nameof(Booking.Name));
        var email = new FieldIdentifier(model, nameof(Booking.Email));
        var notes = new FieldIdentifier(model, nameof(Booking.Notes));

        engine.ApplyServerIssues([new ValidationIssue(nameof(Booking.Notes), Taken)]);
        Assert.Equal([Taken], editContext.GetValidationMessages(notes));

        await engine.DiscloseLoadedValuesAsync();

        Assert.True(model.Tags.Refusals > 0, "the engine's walk of the list should have been refused");
        Assert.True(engine.GetFieldState(name).IsTouched);
        Assert.True(engine.GetFieldState(email).IsTouched);
        Assert.False(engine.GetFieldState(notes).IsTouched);
        Assert.False(engine.GetFieldState(new FieldIdentifier(model.Tags, "1")).IsTouched);
        var shown = engine.GetVisibleIssues().Select(v => v.Issue.Message).Order().ToList();
        var stored = editContext.GetValidationMessages().Order().ToList();
        Assert.Equal(shown, stored);
        Assert.Contains(NameTooLong, shown);
        Assert.DoesNotContain(Taken, shown);
        Assert.DoesNotContain(TagTooLong, shown);
        Assert.Equal([NameTooLong], editContext.GetValidationMessages(name));
        Assert.Empty(editContext.GetValidationMessages(notes));
    }

    // Name's presence rule marks it required while the list beside it refuses the walk that
    // expands the list's own template. The rows carry a presence rule too, because only a
    // template that demands something is expanded for the marks. The rows themselves get no mark:
    // a list the engine cannot walk names no rows.
    [Fact]
    public void A_refusing_list_keeps_the_other_fields_required_marks()
    {
        var walks = new WalkGate();
        var model = new Booking { Tags = new RefusingList<string?>(walks, ["ab"]) };
        var editContext = new EditContext(model);
        using var engine = Build(model, editContext, new BookingValidator(), walks);

        var name = engine.GetFieldRequirement(new FieldIdentifier(model, nameof(Booking.Name)));

        Assert.True(model.Tags.Refusals > 0, "the engine's walk of the list should have been refused");
        Assert.Equal(FieldRequirement.Required, name);
        Assert.Equal(FieldRequirement.Required, engine.GetFieldRequirement(new FieldIdentifier(model, nameof(Booking.Email))));
        Assert.Equal(FieldRequirement.NotRequired, engine.GetFieldRequirement(new FieldIdentifier(model, nameof(Booking.Notes))));
        Assert.Equal(FieldRequirement.NotRequired, engine.GetFieldRequirement(new FieldIdentifier(model.Tags, "0")));
    }

    // A submit held open by Email's async rule. The fields the validator declares show
    // "checking"; Notes carries no rule and shows none. The list's rows show none either, since a
    // list the engine cannot walk contributes no declared paths, though the submit still checks
    // them.
    [Fact]
    public async Task A_submit_beside_a_refusing_list_marks_only_the_declared_fields_checking()
    {
        var walks = new WalkGate();
        var model = new Booking { Name = "ok", Email = "a@b.c", Tags = new RefusingList<string?>(walks, ["ab"]) };
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var editContext = new EditContext(model);
        using var engine = Build(model, editContext, new BookingValidator(gate.Task), walks);

        var submit = engine.ValidateForSubmitAsync();

        Assert.True(engine.IsValidating, "the async rule should hold the submit open");
        Assert.True(model.Tags.Refusals > 0, "the engine's walk of the list should have been refused");
        Assert.True(engine.GetFieldState(new FieldIdentifier(model, nameof(Booking.Name))).IsValidating);
        Assert.True(engine.GetFieldState(new FieldIdentifier(model, nameof(Booking.Email))).IsValidating);
        Assert.False(engine.GetFieldState(new FieldIdentifier(model, nameof(Booking.Notes))).IsValidating);
        Assert.False(engine.GetFieldState(new FieldIdentifier(model.Tags, "0")).IsValidating);

        gate.SetResult(true);
        Assert.True((await submit).CanProceed);
    }

    // Each member holds a list that refuses the engine's walk. The refusal is met one level
    // inside the walked member rows and stops there: each member's own Alias keeps its required
    // mark, and a load lands whole, disclosing the failing alias and confirming the passing one.
    [Fact]
    public async Task A_refusing_list_inside_a_walked_row_keeps_the_outer_rows_marks()
    {
        var walks = new WalkGate();
        var first = new Member { Alias = "toolong", Badges = new RefusingList<string?>(walks, ["ab"]) };
        var second = new Member { Alias = "ok", Badges = new RefusingList<string?>(walks, ["cd"]) };
        var model = new Crew { Members = [first, second] };
        var editContext = new EditContext(model);
        using var engine = Build(model, editContext, new CrewValidator(), walks);
        var firstAlias = new FieldIdentifier(first, nameof(Member.Alias));
        var secondAlias = new FieldIdentifier(second, nameof(Member.Alias));

        Assert.Equal(FieldRequirement.Required, engine.GetFieldRequirement(firstAlias));
        Assert.Equal(FieldRequirement.Required, engine.GetFieldRequirement(secondAlias));

        await engine.DiscloseLoadedValuesAsync();

        Assert.True(first.Badges.Refusals > 0, "the engine's walk of the inner list should have been refused");
        Assert.True(engine.GetFieldState(firstAlias).IsTouched);
        Assert.True(engine.GetFieldState(secondAlias).IsTouched);
        Assert.Equal([AliasTooLong], editContext.GetValidationMessages(firstAlias));
        Assert.Empty(editContext.GetValidationMessages(secondAlias));
        Assert.Equal([AliasTooLong], engine.GetVisibleIssues().Select(v => v.Issue.Message));
    }

    public sealed class Booking
    {
        public string? Name { get; set; }

        public string? Email { get; set; }

        public string? Notes { get; set; }

        public required RefusingList<string?> Tags { get; init; }
    }

    public sealed class Crew
    {
        public required List<Member> Members { get; init; }
    }

    public sealed class Member
    {
        public string? Alias { get; set; }

        public required RefusingList<string?> Badges { get; init; }
    }

    private sealed class BookingValidator : AbstractValidator<Booking>
    {
        public BookingValidator(Task<bool>? emailCheck = null)
        {
            RuleFor(m => m.Name).NotEmpty().MaximumLength(5).WithMessage(NameTooLong);
            RuleFor(m => m.Email).NotEmpty();
            RuleForEach(m => m.Tags).NotEmpty().MaximumLength(3).WithMessage(TagTooLong);
            if (emailCheck is not null)
            {
                RuleFor(m => m.Email).MustAsync(async (_, ct) => await emailCheck.WaitAsync(ct));
            }
        }
    }

    private sealed class CrewValidator : AbstractValidator<Crew>
    {
        public CrewValidator() =>
            RuleForEach(m => m.Members).ChildRules(member =>
            {
                member.RuleFor(x => x.Alias).NotEmpty().MaximumLength(5).WithMessage(AliasTooLong);
                member.RuleForEach(x => x.Badges).NotEmpty().MaximumLength(3).WithMessage(TagTooLong);
            });
    }

    /// <summary>Whether the validator is running, shared by every list of one model: the lists answer the validator's walks and refuse every other.</summary>
    public sealed class WalkGate
    {
        /// <summary>Above zero while the validator runs.</summary>
        public int ValidatorDepth { get; set; }
    }

    /// <summary>A list known only by <see cref="IReadOnlyList{T}"/> that FluentValidation can enumerate and that refuses every other enumeration, as a list over a one-pass source does once the validator has read it.</summary>
    public sealed class RefusingList<T>(WalkGate walks, IEnumerable<T> items) : IReadOnlyList<T>
    {
        private readonly List<T> _items = [.. items];

        /// <summary>The enumerations refused so far.</summary>
        public int Refusals { get; private set; }

        public T this[int index] => _items[index];

        public int Count => _items.Count;

        public IEnumerator<T> GetEnumerator()
        {
            if (walks.ValidatorDepth > 0)
            {
                return _items.GetEnumerator();
            }

            Refusals++;
            throw new InvalidOperationException("The list can be enumerated only once.");
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>The FluentValidation validator with the model's lists answering its walks while it runs.</summary>
    private sealed class GatedWalksValidator<TModel>(IModelValidator<TModel> inner, WalkGate walks)
        : DelegatingModelValidator<TModel>(inner)
    {
        public override async Task<ValidationReport> ValidateAsync(TModel model, ValidationProfile profile, CancellationToken cancellationToken = default)
        {
            walks.ValidatorDepth++;
            try
            {
                return await base.ValidateAsync(model, profile, cancellationToken);
            }
            finally
            {
                walks.ValidatorDepth--;
            }
        }

        public override ValidationReport Validate(TModel model, ValidationProfile profile)
        {
            walks.ValidatorDepth++;
            try
            {
                return base.Validate(model, profile);
            }
            finally
            {
                walks.ValidatorDepth--;
            }
        }

        public override async Task<RuleLevelResult> ValidateRulesAsync(
            TModel model, ValidationProfile profile, IReadOnlyList<RuleIdentity> rules, CancellationToken cancellationToken = default)
        {
            walks.ValidatorDepth++;
            try
            {
                return await base.ValidateRulesAsync(model, profile, rules, cancellationToken);
            }
            finally
            {
                walks.ValidatorDepth--;
            }
        }
    }

    private static FormidableEngine<TModel> Build<TModel>(TModel model, EditContext editContext, IValidator<TModel> validator, WalkGate walks)
        where TModel : class =>
        new(
            model,
            editContext,
            new GatedWalksValidator<TModel>(new FluentValidationModelValidator<TModel>(validator), walks),
            new ReflectionModelIntrospector(),
            new FormidableOptions(),
            new FakeTimeProvider());
}
