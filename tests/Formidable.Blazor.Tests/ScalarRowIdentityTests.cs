using System.Linq.Expressions;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using Bunit;
using FluentValidation;
using Formidable.Blazor.Tests.Fixtures;
using Formidable.Introspection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Formidable.Blazor.Tests;

/// <summary>
/// A row with no members of its own (a string in a list or an array), bound with
/// <c>() => model.Tags[i]</c>, is the field Blazor names by the collection and the index. These pin
/// that the row's message, its state class, its load answer and its summary entry all reach that
/// field, through kit inputs and native ones alike, and what a removal and a reorder then show.
/// </summary>
// The identity mutation every test here names: make ResolvedFieldExtensions.ToFieldIdentifier
// return the introspector's member name unchanged, so a failure on Tags[0] files under the
// collection and "[0]", a field no input binds.
public class ScalarRowIdentityTests : BunitContext
{
    private const string MustNotBeEmpty = "'Tags' must not be empty.";
    private const string TooLong = "The length of 'Tags' must be 5 characters or fewer. You entered 7 characters.";

    private readonly FakeTimeProvider _clock = new();

    public ScalarRowIdentityTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<TimeProvider>(_clock);
        Services.AddFormidable();
    }

    public sealed class ListTags
    {
        public List<string?> Tags { get; set; } = [];
    }

    public sealed class ArrayTags
    {
        public string?[] Tags { get; set; } = [];
    }

    private sealed class ListEach : AbstractValidator<ListTags>
    {
        public ListEach() => RuleForEach(m => m.Tags).NotEmpty().MaximumLength(5);
    }

    private sealed class ListForEach : AbstractValidator<ListTags>
    {
        public ListForEach() => RuleFor(m => m.Tags).ForEach(t => t.NotEmpty().MaximumLength(5));
    }

    private sealed class ArrayEach : AbstractValidator<ArrayTags>
    {
        public ArrayEach() => RuleForEach(m => m.Tags).NotEmpty().MaximumLength(5);
    }

    private sealed class ArrayForEach : AbstractValidator<ArrayTags>
    {
        public ArrayForEach() => RuleFor(m => m.Tags).ForEach(t => t.NotEmpty().MaximumLength(5));
    }

    // Mutation that must break it: the identity mutation, and row 0 shows no message and no class
    // (the submit reports the error against the form instead).
    [Theory]
    [InlineData("List", false)]
    [InlineData("List", true)]
    [InlineData("Array", false)]
    [InlineData("Array", true)]
    public void A_failing_row_shows_its_message_at_its_own_input_after_a_blocked_submit(string collection, bool native)
    {
        var rows = Rows(collection, native, "", "ok");

        Submit(rows);

        Assert.Equal([MustNotBeEmpty], rows.Messages(0));
        Assert.Contains("formidable-invalid", rows.Class(0));
        Assert.Equal("true", rows.Input(0).GetAttribute("aria-invalid"));
        Assert.Empty(rows.Messages(1));
        Assert.DoesNotContain("formidable-invalid", rows.Class(1));
    }

    // Named by its display name under the rule every field's entry follows, and listed against the
    // row's own field. Mutation that must break it: the identity mutation, and the summary reads
    // "This form".
    [Fact]
    public void A_blocked_submit_names_a_failing_row_by_its_display_name()
    {
        SubmitOutcome? outcome = null;
        var rows = Rows("List", native: false, ["", "ok"], onInvalid: c => outcome = c.Outcome);

        Submit(rows);

        Assert.NotNull(outcome);
        Assert.Equal(["Tags"], outcome.VisibleErrorSummary);
        var entry = Assert.Single(rows.Engine.GetVisibleIssues());
        Assert.Equal(rows.Field(0), entry.Field);
        Assert.Equal("Tags", entry.DisplayName);
    }

    // A value typed and then cleared is a committed empty value, which the rule fails. Mutation
    // that must break it: the identity mutation, and the row turns formidable-valid with no
    // message while its rule fails.
    [Theory]
    [InlineData("List", false)]
    [InlineData("List", true)]
    [InlineData("Array", false)]
    [InlineData("Array", true)]
    public void A_committed_empty_value_leaves_the_row_invalid_and_never_valid(string collection, bool native)
    {
        var rows = Rows(collection, native, "", "ok");

        rows.Input(0).Change("x");
        rows.Input(0).Change("");

        rows.Cut.WaitForAssertion(() => Assert.Contains("formidable-invalid", rows.Class(0)));
        Assert.DoesNotContain("formidable-valid", rows.Class(0));
        Assert.Equal([MustNotBeEmpty], rows.Messages(0));
        Assert.False(rows.Engine.GetFieldState(rows.Field(0)).WouldPassSubmit);
    }

    // A load considers every row of a per-row rule, in either spelling: the passing one is
    // confirmed and the failing one, not empty, is disclosed. These rows read the ForEach
    // spelling, and PerRowLeafRuleTests reads RuleForEach's. Mutations that must break it:
    // the identity mutation (neither row answers), and reading a loaded value only through the
    // introspector in AdoptLoadedValues (neither row is read, so neither answers).
    [Theory]
    [InlineData("List")]
    [InlineData("Array")]
    public async Task A_load_confirms_a_passing_row_and_discloses_a_failing_one(string collection)
    {
        var (engine, editContext, row0, row1) = await LoadAsync(collection, forEach: true, "toolong", "ok");
        using var _ = engine;

        Assert.Contains("formidable-invalid", editContext.FieldCssClass(row0));
        Assert.Equal([TooLong], editContext.GetValidationMessages(row0));
        Assert.Contains("formidable-valid", editContext.FieldCssClass(row1));
        Assert.Empty(editContext.GetValidationMessages(row1));
    }

    // The same load under the RuleForEach spelling discloses the failing row too.
    // Mutations that must break it: the identity mutation, and reading a loaded value only
    // through the introspector in AdoptLoadedValues.
    [Fact]
    public async Task A_load_discloses_a_failing_row_under_RuleForEach()
    {
        var (engine, editContext, row0, _) = await LoadAsync("List", forEach: false, "toolong", "ok");
        using var __ = engine;

        Assert.Contains("formidable-invalid", editContext.FieldCssClass(row0));
        Assert.Equal([TooLong], editContext.GetValidationMessages(row0));
    }

    public sealed class NumberRows
    {
        public int?[] Array { get; set; } = [0];

        public List<int?> List { get; set; } = [0];

        public ObservableCollection<int?> Observable { get; set; } = [0];

        public List<object?> Objects { get; set; } = [0];

        public List<int> Plain { get; set; } = [0];
    }

    private sealed class NumberRowsValidator : AbstractValidator<NumberRows>
    {
        public NumberRowsValidator()
        {
            RuleFor(m => m.Array).ForEach(v => v.NotNull());
            RuleFor(m => m.List).ForEach(v => v.NotNull());
            RuleFor(m => m.Observable).ForEach(v => v.NotNull());
            RuleFor(m => m.Objects).ForEach(v => v.NotNull());
            RuleFor(m => m.Plain).ForEach(v => v.GreaterThanOrEqualTo(0));
        }
    }

    // A loaded 0 is filled in where the list declares a nullable or object element, as NotEmpty()
    // reads it, and empty where it declares an int, so the first four rows are confirmed and the
    // last stays silent. Mutations that must break it: read every element by the value's own type
    // (the four rows read as empty and stay silent), and read every element as object (the int
    // row is confirmed).
    [Fact]
    public async Task A_load_reads_a_zero_by_the_element_type_its_list_declares()
    {
        var model = new NumberRows();
        var editContext = new EditContext(model);
        using var engine = new FormidableEngine<NumberRows>(
            model, editContext, new FluentValidationModelValidator<NumberRows>(new NumberRowsValidator()),
            new ReflectionModelIntrospector(), new FormidableOptions(), new FakeTimeProvider());

        await engine.DiscloseLoadedValuesAsync();

        Assert.Contains("formidable-valid", editContext.FieldCssClass(FieldIdentifier.Create(() => model.Array[0])));
        Assert.Contains("formidable-valid", editContext.FieldCssClass(FieldIdentifier.Create(() => model.List[0])));
        Assert.Contains("formidable-valid", editContext.FieldCssClass(FieldIdentifier.Create(() => model.Observable[0])));
        Assert.Contains("formidable-valid", editContext.FieldCssClass(FieldIdentifier.Create(() => model.Objects[0])));
        Assert.Equal(string.Empty, editContext.FieldCssClass(FieldIdentifier.Create(() => model.Plain[0])));
    }

    // A presence rule on each row marks each row's own input required. These rows spell it with
    // ForEach, and PerRowLeafRuleTests marks the rows under RuleForEach too. A pin of behaviour
    // that already holds. Mutation that must break it: the identity mutation, and the
    // requirement lands on the collection and "[0]", so neither row's input carries
    // aria-required.
    [Theory]
    [InlineData("List")]
    [InlineData("Array")]
    public void A_presence_rule_spelled_per_row_marks_each_kit_row_required(string collection)
    {
        var rows = RowsForEach(collection, "", "ok");

        Assert.Equal("true", rows.Input(0).GetAttribute("aria-required"));
        Assert.Equal("true", rows.Input(1).GetAttribute("aria-required"));
        Assert.Equal(FieldRequirement.Required, rows.Engine.GetFieldRequirement(rows.Field(0)));
    }

    // An OrderedDictionary is a list as well as a dictionary. FluentValidation reports key 2's
    // empty answer at position 1, and key 1's input is the field named "1", so a failure named by
    // position would sit on the wrong key's input. A dictionary's entry keeps its brackets, so the
    // failure reaches no input and the submit shows the defensive gate, as for a Dictionary.
    // Mutation that must break it: drop the dictionary exclusion from the list test, and key 1's
    // passing input turns invalid and shows the message.
    [Fact]
    public void A_failing_ordered_dictionary_entry_reaches_no_key_s_input()
    {
        var model = new OrderedAnswers();
        model.Answers.Add(1, "fine");
        model.Answers.Add(2, "");
        model.Answers.Add(3, "ok");
        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<OrderedAnswers>>(0);
            builder.AddComponentParameter(1, "Model", model);
            builder.AddComponentParameter(2, "Validator", (IModelValidator<OrderedAnswers>)new FluentValidationModelValidator<OrderedAnswers>(new OrderedAnswersValidator()));
            builder.AddComponentParameter(3, "ChildContent", (RenderFragment<FormidableFormContext>)(_ => inner =>
            {
                foreach (var key in model.Answers.Keys.ToList())
                {
                    RenderCell(inner, key, $"{key}", () => model.Answers[key], v => model.Answers[key] = v);
                }
            }));
            builder.CloseComponent();
        });
        var engine = cut.FindComponent<FormidableForm<OrderedAnswers>>().Instance.Engine!;

        cut.Find("form").Submit();
        cut.WaitForState(() => engine.HasSubmitted && !engine.IsValidating);

        var key1 = cut.Find("[data-cell=\"1\"] input");
        Assert.DoesNotContain("formidable-invalid", key1.GetAttribute("class") ?? string.Empty);
        Assert.Null(key1.GetAttribute("aria-invalid"));
        Assert.Empty(cut.FindAll("[data-cell=\"1\"] li"));
        Assert.Contains(engine.GetVisibleIssues(), v => v.Issue.Message == engine.Options.DefensiveGateMessage);
    }

    // A grid's cells are elements of the lists inside a list. The validator declares each cell's
    // presence rule, and the form expands that declaration to one field per cell, so every cell
    // input carries the mark. Both spellings of the nested rule declare the same shape. Mutation
    // that must break it: read the collection at a path through the introspector alone when
    // counting its rows, and a row of the grid counts no cells, so no cell carries the mark.
    [Theory]
    [InlineData(nameof(CellGridEachRowValidator))]
    [InlineData(nameof(CellGridNestedForEachValidator))]
    public void A_nested_scalar_collection_marks_each_cell_required(string spelling)
    {
        var model = new CellGrid { Matrix = [["a", ""], ["", "b"]] };
        var (cut, engine) = RenderGrid(model, spelling);

        Assert.Equal(FieldRequirement.Required, engine.GetFieldRequirement(FieldIdentifier.Create(() => model.Matrix[0][1])));
        foreach (var cell in new[] { "0-0", "0-1", "1-0", "1-1" })
        {
            Assert.Equal("true", cut.Find($"[data-cell=\"{cell}\"] input").GetAttribute("aria-required"));
        }
    }

    // A cell is named by its own list and its index, so a blocked submit shows each empty cell's
    // message at that cell's input and nowhere else. A pin, under either spelling of the nested
    // rule. Mutation that must break it: the identity mutation, and no cell shows a message (the
    // submit reports against the form instead).
    [Theory]
    [InlineData(nameof(CellGridEachRowValidator))]
    [InlineData(nameof(CellGridNestedForEachValidator))]
    public void A_nested_scalar_collection_shows_each_cell_s_message_at_its_own_input(string spelling)
    {
        var model = new CellGrid { Matrix = [["a", ""], ["", "b"]] };
        var (cut, engine) = RenderGrid(model, spelling);

        cut.Find("form").Submit();
        cut.WaitForState(() => engine.HasSubmitted && !engine.IsValidating);

        Assert.Equal([CellGridEachRowValidator.Required], CellMessages(cut, "0-1"));
        Assert.Equal([CellGridEachRowValidator.Required], CellMessages(cut, "1-0"));
        Assert.Empty(CellMessages(cut, "0-0"));
        Assert.Empty(CellMessages(cut, "1-1"));
    }

    /// <summary>Renders one kit input and message per cell of <paramref name="model"/>'s grid, each bound by both indexes.</summary>
    private (IRenderedComponent<IComponent> Cut, IFormidableEngine Engine) RenderGrid(CellGrid model, string spelling)
    {
        AbstractValidator<CellGrid> validator = spelling == nameof(CellGridEachRowValidator)
            ? new CellGridEachRowValidator()
            : new CellGridNestedForEachValidator();
        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<CellGrid>>(0);
            builder.AddComponentParameter(1, "Model", model);
            builder.AddComponentParameter(2, "Validator", (IModelValidator<CellGrid>)new FluentValidationModelValidator<CellGrid>(validator));
            builder.AddComponentParameter(3, "ChildContent", (RenderFragment<FormidableFormContext>)(_ => inner =>
            {
                for (var i = 0; i < model.Matrix.Count; i++)
                {
                    for (var j = 0; j < model.Matrix[i].Count; j++)
                    {
                        var (row, column) = (i, j);
                        RenderCell(inner, (row * 100) + column, $"{row}-{column}", () => model.Matrix[row][column], v => model.Matrix[row][column] = v);
                    }
                }
            }));
            builder.CloseComponent();
        });
        return (cut, cut.FindComponent<FormidableForm<CellGrid>>().Instance.Engine!);
    }

    private static IReadOnlyList<string> CellMessages(IRenderedComponent<IComponent> cut, string cell) =>
        cut.FindAll($"[data-cell=\"{cell}\"] li").Select(e => e.TextContent.Trim()).ToList();

    /// <summary>Renders one kit input and its message, bound to <paramref name="accessor"/>, inside an element marked <c>data-cell</c>.</summary>
    private void RenderCell(RenderTreeBuilder builder, int region, string cell, Expression<Func<string?>> accessor, Action<string?> assign)
    {
        builder.OpenRegion(region);
        builder.OpenElement(0, "div");
        builder.AddAttribute(1, "data-cell", cell);
        builder.OpenComponent<FormidableInputText>(2);
        builder.AddComponentParameter(3, "Value", accessor.Compile()());
        builder.AddComponentParameter(4, "ValueChanged", EventCallback.Factory.Create(this, assign));
        builder.AddComponentParameter(5, "ValueExpression", accessor);
        builder.CloseComponent();
        builder.OpenComponent<FormidableFieldMessage<string?>>(6);
        builder.AddComponentParameter(7, "For", accessor);
        builder.CloseComponent();
        builder.CloseElement();
        builder.CloseRegion();
    }

    // A row with no members is identified by its index, so removing the failing row 0 moves the
    // passing value into row 0's place. Once the check the removal starts has answered, that row
    // shows its own verdict and nothing of the removed row's. Mutations that must break it: the
    // identity mutation (row 0 shows no message before the removal), and leaving the submit
    // answer as the submit filed it, in both the live pass that runs the submit profile and the
    // refresh, so that only a submit rebuilds it (the row that moved up keeps the removed row's
    // message). Either pass alone rebuilds it, so the mutation takes both.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Removing_a_failing_row_after_a_blocked_submit_leaves_the_row_that_moves_up_clean(bool native)
    {
        var rows = Rows("List", native, "", "ok");
        Submit(rows);
        Assert.Equal([MustNotBeEmpty], rows.Messages(0));

        await rows.Cut.InvokeAsync(() => rows.Context.RemoveItem((ICollection<string?>)rows.Collection, ""));
        await SettleAsync(rows);

        Assert.Equal<string?>(["ok"], rows.Collection);
        Assert.Equal("ok", rows.Input(0).GetAttribute("value"));
        Assert.Empty(rows.Messages(0));
        Assert.DoesNotContain("formidable-invalid", rows.Class(0));
        Assert.True(rows.Engine.GetFieldState(rows.Field(0)).WouldPassSubmit);
    }

    // After a blocked submit over ["ok", ""], removing the passing row 0 moves the failing value
    // into row 0, a row that passed at that submit. Its message shows there at the next submit,
    // and until then row 0 is silent rather than valid. A pin of behaviour that already holds.
    // Mutations that must break it: the identity mutation (row 1 shows no message at the first
    // submit), and revealing every field a refresh finds failing (row 0 speaks before the second
    // submit).
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Removing_a_passing_row_moves_a_failing_value_up_and_it_speaks_at_the_next_submit(bool native)
    {
        var rows = Rows("List", native, "ok", "");
        Submit(rows);
        Assert.Equal([MustNotBeEmpty], rows.Messages(1));
        Assert.Empty(rows.Messages(0));

        await rows.Cut.InvokeAsync(() => rows.Context.RemoveItem((ICollection<string?>)rows.Collection, "ok"));
        await SettleAsync(rows);

        Assert.Equal<string?>([""], rows.Collection);
        Assert.Empty(rows.Messages(0));
        Assert.DoesNotContain("formidable-valid", rows.Class(0));
        Assert.DoesNotContain("formidable-invalid", rows.Class(0));
        Assert.False(rows.Engine.GetFieldState(rows.Field(0)).WouldPassSubmit);

        Submit(rows);

        Assert.Equal([MustNotBeEmpty], rows.Messages(0));
        Assert.Contains("formidable-invalid", rows.Class(0));
    }

    // After a blocked submit, a reorder moves the failing value to row 1, a row the submit did not
    // find failing. The value's message shows there at the next submit, and until then row 1 is
    // silent rather than valid. Mutations that must break it: the identity mutation (no message
    // at either submit), and revealing every field a refresh finds failing (row 1 speaks before
    // the second submit).
    [Theory]
    [InlineData("List", false)]
    [InlineData("List", true)]
    [InlineData("Array", false)]
    [InlineData("Array", true)]
    public async Task A_failing_value_moved_by_a_reorder_shows_its_message_at_the_next_submit(string collection, bool native)
    {
        var rows = Rows(collection, native, "", "ok");
        Submit(rows);
        Assert.Equal([MustNotBeEmpty], rows.Messages(0));

        await rows.Cut.InvokeAsync(() => rows.Context.Edit(() =>
            (rows.Collection[0], rows.Collection[1]) = (rows.Collection[1], rows.Collection[0])));
        await SettleAsync(rows);

        Assert.Empty(rows.Messages(0));
        Assert.DoesNotContain("formidable-invalid", rows.Class(0));
        Assert.Empty(rows.Messages(1));
        Assert.DoesNotContain("formidable-valid", rows.Class(1));
        Assert.DoesNotContain("formidable-invalid", rows.Class(1));
        Assert.False(rows.Engine.GetFieldState(rows.Field(1)).WouldPassSubmit);

        Submit(rows);

        Assert.Equal([MustNotBeEmpty], rows.Messages(1));
        Assert.Contains("formidable-invalid", rows.Class(1));
        Assert.Empty(rows.Messages(0));
    }

    // An array grows only by replacement. With the element around the loop keyed by the array,
    // the replacement rebuilds every row against the new array, so a row cleared after the grow
    // shows its message, aria-invalid and its required mark, kit and native inputs alike. Without
    // the key each row's components stay bound to the array that left, so the cleared row shows
    // none of the three and a blocked submit can only say that something unseen is invalid. A pin:
    // the key on the element around the loop decides whether a replaced array's rows speak.
    // Mutation that must break it: remove the key from the keyed host, and its cleared row shows
    // nothing either.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_loop_keyed_by_its_array_shows_a_cleared_row_after_the_array_grows_and_an_unkeyed_loop_does_not(bool native)
    {
        var keyed = await GrowThenClearRowZeroAsync(native, keyByCollection: true);

        Assert.Equal([MustNotBeEmpty], keyed.Messages(0));
        Assert.Contains("formidable-invalid", keyed.Class(0));
        Assert.Equal("true", keyed.Input(0).GetAttribute("aria-invalid"));
        Assert.Single(keyed.Cut.FindAll("[data-row=\"0\"] .formidable-required"));
        if (!native)
        {
            Assert.Equal("true", keyed.Input(0).GetAttribute("aria-required"));
        }

        var unkeyed = await GrowThenClearRowZeroAsync(native, keyByCollection: false);

        Assert.Empty(unkeyed.Messages(0));
        Assert.DoesNotContain("formidable-invalid", unkeyed.Class(0));
        Assert.Null(unkeyed.Input(0).GetAttribute("aria-invalid"));
        Assert.Empty(unkeyed.Cut.FindAll("[data-row=\"0\"] .formidable-required"));
        Assert.Null(unkeyed.Input(0).GetAttribute("aria-required"));
        Assert.Equal(string.Empty, unkeyed.Collection[0]);
        Submit(unkeyed);
        Assert.Empty(unkeyed.Messages(0));
        Assert.Equal([unkeyed.Engine.Options.DefensiveGateMessage], unkeyed.Engine.GetVisibleIssues().Select(v => v.Issue.Message));
    }

    // A submit shows a field's message once it has shown that field, and it keeps that per field. A
    // row the replacement rebuilt is a new field, and so is the empty row the Add appended, so
    // neither shows its message until the next submit, which shows both; every row carries its
    // required mark throughout. A pin: a replacing Add starts every row afresh, the added row
    // included. Mutation that must break it: reveal every field a refresh finds failing (union the
    // refresh's error fields into the reveal ledger), and row 0 speaks straight after the grow.
    [Fact]
    public async Task A_keyed_array_grown_after_a_blocked_submit_shows_its_rows_again_at_the_next_submit()
    {
        var model = new ArrayTags { Tags = ["", "ok"] };
        var rows = RenderRows(model, () => model.Tags, i => () => model.Tags[i], new ArrayEach(), native: false, onInvalid: null, keyByCollection: true, withIndicator: true);
        Submit(rows);
        Assert.Equal([MustNotBeEmpty], rows.Messages(0));

        await rows.Cut.InvokeAsync(() => rows.Context.Edit(() => model.Tags = [.. model.Tags, string.Empty]));
        await SettleAsync(rows);

        Assert.Equal(3, rows.Collection.Count);
        foreach (var row in new[] { 0, 2 })
        {
            Assert.Empty(rows.Messages(row));
            Assert.DoesNotContain("formidable-invalid", rows.Class(row));
            Assert.DoesNotContain("formidable-valid", rows.Class(row));
        }

        for (var row = 0; row < 3; row++)
        {
            Assert.Single(rows.Cut.FindAll($"[data-row=\"{row}\"] .formidable-required"));
            Assert.Equal("true", rows.Input(row).GetAttribute("aria-required"));
        }

        Submit(rows);

        Assert.Equal([MustNotBeEmpty], rows.Messages(0));
        Assert.Equal([MustNotBeEmpty], rows.Messages(2));
        Assert.Contains("formidable-invalid", rows.Class(2));
        Assert.Empty(rows.Messages(1));
    }

    // The same for a live message. A field shows live messages only once a change to it has been
    // committed, and a rebuilt row is a field no change has touched. A pin: a rebuilt row shows
    // nothing until the visitor edits it again, here by typing a value and then clearing it.
    // Mutation that must break it: engage every field a live pass finds failing (file the report's
    // fields as well as the engaged set's), and row 0 speaks straight after the grow.
    [Fact]
    public async Task A_keyed_array_grown_after_an_edit_shows_the_row_again_at_its_next_edit()
    {
        var model = new ArrayTags { Tags = ["ok", "ok"] };
        var rows = RenderRows(model, () => model.Tags, i => () => model.Tags[i], new ArrayEach(), native: false, onInvalid: null, keyByCollection: true);
        rows.Input(0).Change("");
        rows.Cut.WaitForAssertion(() => Assert.Equal([MustNotBeEmpty], rows.Messages(0)));

        await rows.Cut.InvokeAsync(() => rows.Context.Edit(() => model.Tags = [.. model.Tags, "x"]));
        await SettleAsync(rows);

        Assert.Equal(3, rows.Collection.Count);
        Assert.Empty(rows.Messages(0));
        Assert.DoesNotContain("formidable-invalid", rows.Class(0));

        rows.Input(0).Change("x");
        await SettleAsync(rows);
        Assert.Empty(rows.Messages(0));
        rows.Input(0).Change("");

        rows.Cut.WaitForAssertion(() => Assert.Equal([MustNotBeEmpty], rows.Messages(0)));
        Assert.Contains("formidable-invalid", rows.Class(0));
    }

    // A grid's row is a list of its own. Replacing one row's list hands that row's cells a list the
    // form's checks report on while the cells still speak for the list that left, unless the
    // element around the row's cells is keyed by the row's list. With the key, the replacement
    // rebuilds the row's cells, and a cell cleared after it shows its message; without the key, the
    // cleared cell shows nothing. A pin: the key on each grid row's element decides whether its
    // cells speak after the page replaces its list. Mutation that must break it: drop the key from
    // the keyed case, and its cleared cell shows nothing.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_grid_row_keyed_by_its_list_shows_a_cleared_cell_after_the_page_replaces_that_list(bool keyed)
    {
        var model = new CellGrid { Matrix = [["a", "b"], ["c", "d"]] };
        var captured = new StrongBox<FormidableFieldContext>();
        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<CellGrid>>(0);
            builder.AddComponentParameter(1, "Model", model);
            builder.AddComponentParameter(2, "Validator", (IModelValidator<CellGrid>)new FluentValidationModelValidator<CellGrid>(new CellGridEachRowValidator()));
            builder.AddComponentParameter(3, "ChildContent", (RenderFragment<FormidableFormContext>)(_ => inner =>
            {
                inner.OpenComponent<FormidableField<List<List<string?>>>>(0);
                inner.AddComponentParameter(1, "For", (Expression<Func<List<List<string?>>>>)(() => model.Matrix));
                inner.AddComponentParameter(2, "ChildContent", (RenderFragment<FormidableFieldContext>)(context => content =>
                {
                    captured.Value = context;
                    for (var i = 0; i < model.Matrix.Count; i++)
                    {
                        var row = i;
                        content.OpenElement(0, "div");
                        if (keyed)
                        {
                            content.SetKey(model.Matrix[row]);
                        }

                        for (var j = 0; j < model.Matrix[row].Count; j++)
                        {
                            var column = j;
                            RenderCell(content, column, $"{row}-{column}", () => model.Matrix[row][column], v => model.Matrix[row][column] = v);
                        }

                        content.CloseElement();
                    }
                }));
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        });
        var engine = cut.FindComponent<FormidableForm<CellGrid>>().Instance.Engine!;

        await cut.InvokeAsync(() => captured.Value!.Edit(() => model.Matrix[0] = [.. model.Matrix[0]]));
        cut.WaitForState(() => !engine.IsValidating);
        cut.Find("[data-cell=\"0-0\"] input").Change("");
        cut.WaitForState(() => !engine.IsValidating);

        Assert.Equal(string.Empty, model.Matrix[0][0]);
        if (keyed)
        {
            cut.WaitForAssertion(() => Assert.Equal([CellGridEachRowValidator.Required], CellMessages(cut, "0-0")));
            Assert.Contains("formidable-invalid", cut.Find("[data-cell=\"0-0\"] input").GetAttribute("class"));
        }
        else
        {
            Assert.Empty(CellMessages(cut, "0-0"));
            Assert.DoesNotContain("formidable-invalid", cut.Find("[data-cell=\"0-0\"] input").GetAttribute("class") ?? string.Empty);
            cut.Find("form").Submit();
            cut.WaitForState(() => engine.HasSubmitted && !engine.IsValidating);
            Assert.Empty(CellMessages(cut, "0-0"));
            Assert.Equal([engine.Options.DefensiveGateMessage], engine.GetVisibleIssues().Select(v => v.Issue.Message));
        }
    }

    /// <summary>Renders the array rows with a required mark each, grows the array by replacing it, then clears row 0 and lets every check land.</summary>
    private async Task<RenderedRows> GrowThenClearRowZeroAsync(bool native, bool keyByCollection)
    {
        var model = new ArrayTags { Tags = ["ok"] };
        var rows = RenderRows(model, () => model.Tags, i => () => model.Tags[i], new ArrayEach(), native, onInvalid: null, keyByCollection, withIndicator: true);
        Assert.Single(rows.Cut.FindAll("[data-row=\"0\"] .formidable-required"));

        await rows.Cut.InvokeAsync(() => rows.Context.Edit(() => model.Tags = [.. model.Tags, "x"]));
        await SettleAsync(rows);
        Assert.Equal(2, rows.Cut.FindAll("[data-row]").Count);

        rows.Input(0).Change("");
        await SettleAsync(rows);
        return rows;
    }

    /// <summary>Submits and waits for the submit's answer to render.</summary>
    private static void Submit(RenderedRows rows)
    {
        rows.Cut.Find("form").Submit();
        rows.Cut.WaitForState(() => rows.Engine.HasSubmitted && !rows.Engine.IsValidating);
    }

    /// <summary>Lets the refresh a post-submit change arms run, then waits for every pass to land.</summary>
    private async Task SettleAsync(RenderedRows rows)
    {
        rows.Cut.WaitForState(() => !rows.Engine.IsValidating);
        await rows.Cut.InvokeAsync(() => _clock.Advance(new FormidableOptions().RefreshDebounce + TimeSpan.FromMilliseconds(1)));
        rows.Cut.WaitForState(() => !rows.Engine.IsValidating);
    }

    private static async Task<(IDisposable Engine, EditContext EditContext, FieldIdentifier Row0, FieldIdentifier Row1)> LoadAsync(string collection, bool forEach, params string?[] values)
    {
        if (collection == "List")
        {
            var model = new ListTags { Tags = [.. values] };
            var editContext = new EditContext(model);
            var engine = new FormidableEngine<ListTags>(
                model, editContext, new FluentValidationModelValidator<ListTags>(forEach ? new ListForEach() : new ListEach()),
                new ReflectionModelIntrospector(), new FormidableOptions(), new FakeTimeProvider());
            await engine.DiscloseLoadedValuesAsync();
            return (engine, editContext, FieldIdentifier.Create(() => model.Tags[0]), FieldIdentifier.Create(() => model.Tags[1]));
        }
        else
        {
            var model = new ArrayTags { Tags = [.. values] };
            var editContext = new EditContext(model);
            var engine = new FormidableEngine<ArrayTags>(
                model, editContext, new FluentValidationModelValidator<ArrayTags>(forEach ? new ArrayForEach() : new ArrayEach()),
                new ReflectionModelIntrospector(), new FormidableOptions(), new FakeTimeProvider());
            await engine.DiscloseLoadedValuesAsync();
            return (engine, editContext, FieldIdentifier.Create(() => model.Tags[0]), FieldIdentifier.Create(() => model.Tags[1]));
        }
    }

    private RenderedRows RowsForEach(string collection, params string?[] values)
    {
        if (collection == "List")
        {
            var model = new ListTags { Tags = [.. values] };
            return RenderRows(model, () => model.Tags, i => () => model.Tags[i], new ListForEach(), native: false, onInvalid: null);
        }

        var array = new ArrayTags { Tags = [.. values] };
        return RenderRows(array, () => array.Tags, i => () => array.Tags[i], new ArrayForEach(), native: false, onInvalid: null);
    }

    private RenderedRows Rows(string collection, bool native, params string?[] values) =>
        Rows(collection, native, values, onInvalid: null);

    private RenderedRows Rows(string collection, bool native, string?[] values, Action<FormidableInvalidSubmitContext>? onInvalid)
    {
        if (collection == "List")
        {
            var model = new ListTags { Tags = [.. values] };
            return RenderRows(model, () => model.Tags, i => () => model.Tags[i], new ListEach(), native, onInvalid);
        }

        var array = new ArrayTags { Tags = [.. values] };
        return RenderRows(array, () => array.Tags, i => () => array.Tags[i], new ArrayEach(), native, onInvalid);
    }

    /// <summary>Renders one unkeyed row per element inside a <c>FormidableField</c> over the collection, whose context drives the removal and the reorder; the element around the rows is keyed by the collection when <paramref name="keyByCollection"/> asks.</summary>
    private RenderedRows RenderRows<TModel, TCollection>(
        TModel model,
        Expression<Func<TCollection>> collection,
        Func<int, Expression<Func<string?>>> row,
        AbstractValidator<TModel> validator,
        bool native,
        Action<FormidableInvalidSubmitContext>? onInvalid,
        bool keyByCollection = false,
        bool withIndicator = false)
        where TModel : class
        where TCollection : IList<string?>
    {
        var captured = new StrongBox<FormidableFieldContext>();
        var read = collection.Compile();
        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<TModel>>(0);
            builder.AddComponentParameter(1, "Model", model);
            builder.AddComponentParameter(2, "Validator", (IModelValidator<TModel>)new FluentValidationModelValidator<TModel>(validator));
            if (onInvalid is not null)
            {
                builder.AddComponentParameter(3, "OnInvalidSubmit", EventCallback.Factory.Create(this, onInvalid));
            }

            builder.AddComponentParameter(4, "ChildContent", (RenderFragment<FormidableFormContext>)(_ => inner =>
            {
                inner.OpenComponent<FormidableField<TCollection>>(0);
                inner.AddComponentParameter(1, "For", collection);
                inner.AddComponentParameter(2, "ChildContent", (RenderFragment<FormidableFieldContext>)(context => content =>
                {
                    captured.Value = context;
                    var items = read();
                    content.OpenElement(0, "div");
                    if (keyByCollection)
                    {
                        content.SetKey(items);
                    }

                    for (var i = 0; i < items.Count; i++)
                    {
                        RenderRow(content, items, i, row(i), native, withIndicator);
                    }

                    content.CloseElement();
                }));
                inner.CloseComponent();
                inner.AddMarkupContent(3, "<button type=\"submit\">Save</button>");
            }));
            builder.CloseComponent();
        });

        var engine = cut.FindComponent<FormidableForm<TModel>>().Instance.Engine!;
        return new RenderedRows(cut, engine, captured, () => read(), row);
    }

    private void RenderRow(RenderTreeBuilder builder, IList<string?> items, int index, Expression<Func<string?>> accessor, bool native, bool withIndicator)
    {
        builder.OpenRegion(index);
        builder.OpenElement(0, "div");
        builder.AddAttribute(1, "data-row", index);

        if (native)
        {
            builder.OpenComponent<InputText>(2);
            builder.AddComponentParameter(3, "Value", items[index]);
            builder.AddComponentParameter(4, "ValueChanged", EventCallback.Factory.Create<string?>(this, v => items[index] = v));
            builder.AddComponentParameter(5, "ValueExpression", accessor);
            builder.CloseComponent();
            builder.OpenComponent<ValidationMessage<string?>>(6);
            builder.AddComponentParameter(7, "For", accessor);
            builder.CloseComponent();
            builder.OpenComponent<FormidableFieldAnchor<string?>>(8);
            builder.AddComponentParameter(9, "For", accessor);
            builder.CloseComponent();
        }
        else
        {
            builder.OpenComponent<FormidableInputText>(2);
            builder.AddComponentParameter(3, "Value", items[index]);
            builder.AddComponentParameter(4, "ValueChanged", EventCallback.Factory.Create<string?>(this, v => items[index] = v));
            builder.AddComponentParameter(5, "ValueExpression", accessor);
            builder.CloseComponent();
            builder.OpenComponent<FormidableFieldMessage<string?>>(6);
            builder.AddComponentParameter(7, "For", accessor);
            builder.CloseComponent();
        }

        if (withIndicator)
        {
            builder.OpenComponent<FormidableRequiredIndicator<string?>>(10);
            builder.AddComponentParameter(11, "For", accessor);
            builder.CloseComponent();
        }

        builder.CloseElement();
        builder.CloseRegion();
    }

    private sealed record RenderedRows(
        IRenderedComponent<IComponent> Cut,
        IFormidableEngine Engine,
        StrongBox<FormidableFieldContext> Captured,
        Func<IList<string?>> ReadCollection,
        Func<int, Expression<Func<string?>>> Row)
    {
        public FormidableFieldContext Context => Captured.Value!;

        public IList<string?> Collection => ReadCollection();

        public FieldIdentifier Field(int index) => FieldIdentifier.Create(Row(index));

        public AngleSharp.Dom.IElement Input(int index) => Cut.Find($"[data-row=\"{index}\"] input");

        public string Class(int index) => Input(index).GetAttribute("class") ?? string.Empty;

        public IReadOnlyList<string> Messages(int index) =>
            Cut.FindAll($"[data-row=\"{index}\"] li, [data-row=\"{index}\"] .validation-message")
                .Select(e => e.TextContent.Trim())
                .ToList();
    }
}
