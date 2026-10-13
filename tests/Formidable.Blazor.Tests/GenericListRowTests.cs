using System.Collections;
using System.Collections.Immutable;
using System.Linq.Expressions;
using System.Text.Json.Nodes;
using Bunit;
using FluentValidation;
using Formidable.Introspection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Formidable.Blazor.Tests;

/// <summary>
/// A row of a list that implements only the generic <see cref="IList{T}"/> or
/// <see cref="IReadOnlyList{T}"/> (an <see cref="ImmutableArray{T}.Builder"/>, a
/// <see cref="JsonArray"/>, a hand-written list), bound with <c>() => model.Tags[i]</c>, is the
/// field Blazor names by the list and the index, as a <c>List&lt;T&gt;</c> row is. These pin that
/// the row's message, state class, required mark and load answer all reach that field, through kit
/// and native inputs alike, and that a row read by id through an int indexer never wears the
/// valid class.
/// </summary>
// The list mutation the generic-only cases name: make ResolvedFieldExtensions.IsIndexedList
// accept the non-generic IList alone, so a failure on Tags[1] of a generic-only list files under
// the list and "[1]", a field no input binds. The List<T> cases are controls, and pass under it.
public class GenericListRowTests : BunitContext
{
    public const string TagRequired = "Tag required";
    public const string TagTooLong = "Tag too long";

    private readonly FakeTimeProvider _clock = new();

    public GenericListRowTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<TimeProvider>(_clock);
        Services.AddFormidableBlazor();
    }

    /// <summary>A list that implements only the generic <see cref="IList{T}"/>, as a hand-written collection does.</summary>
    public sealed class GenericOnlyList<T> : IList<T>
    {
        private readonly List<T> _items;

        public GenericOnlyList() => _items = [];

        public GenericOnlyList(IEnumerable<T> items) => _items = [.. items];

        public T this[int index] { get => _items[index]; set => _items[index] = value; }

        public int Count => _items.Count;

        public bool IsReadOnly => false;

        public void Add(T item) => _items.Add(item);

        public void Clear() => _items.Clear();

        public bool Contains(T item) => _items.Contains(item);

        public void CopyTo(T[] array, int arrayIndex) => _items.CopyTo(array, arrayIndex);

        public IEnumerator<T> GetEnumerator() => _items.GetEnumerator();

        public int IndexOf(T item) => _items.IndexOf(item);

        public void Insert(int index, T item) => _items.Insert(index, item);

        public bool Remove(T item) => _items.Remove(item);

        public void RemoveAt(int index) => _items.RemoveAt(index);

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>A list that implements only <see cref="IReadOnlyList{T}"/>, written through a method of its own.</summary>
    public sealed class ReadOnlyOnlyList<T>(IEnumerable<T> items) : IReadOnlyList<T>
    {
        private readonly List<T> _items = [.. items];

        public T this[int index] => _items[index];

        public int Count => _items.Count;

        public void Set(int index, T value) => _items[index] = value;

        public IEnumerator<T> GetEnumerator() => _items.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>A collection with an int indexer that reads by id rather than by position, and no list interface: id 7 sits at position 0 and id 0 at position 1.</summary>
    public sealed class IdIndexedBag : IEnumerable<string?>
    {
        private readonly List<(int Id, string? Value)> _items = [(7, "a"), (0, "b")];

        public string? this[int id] => _items.Single(item => item.Id == id).Value;

        public void Set(int id, string? value) => _items[_items.FindIndex(item => item.Id == id)] = (id, value);

        public IEnumerator<string?> GetEnumerator() => _items.Select(item => item.Value).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    public sealed class Holder<TList>
    {
        public required TList Tags { get; set; }
    }

    private sealed class TextTagsValidator<TList> : AbstractValidator<Holder<TList>>
        where TList : class, IEnumerable<string?>
    {
        public TextTagsValidator() =>
            RuleForEach(m => (IEnumerable<string?>)m.Tags)
                .NotEmpty().WithMessage(TagRequired)
                .MaximumLength(3).WithMessage(TagTooLong);
    }

    // NotNull gives the rows their required mark; the two Must rules read the text a JsonValue
    // holds, which NotEmpty() would read as filled whatever it held.
    private sealed class NodeTagsValidator : AbstractValidator<Holder<JsonArray>>
    {
        public NodeTagsValidator() =>
            RuleForEach(m => m.Tags)
                .NotNull().WithMessage(TagRequired)
                .Must(node => node is null || Text(node).Length > 0).WithMessage(TagRequired)
                .Must(node => node is null || Text(node).Length <= 3).WithMessage(TagTooLong);
    }

    // Row 1 cleared, then a blocked submit. The cleared row shows its message and the invalid
    // class after the edit and after the submit, and its neighbour keeps its vouch. Mutation that
    // must break it: the list mutation, and each generic-only row shows no message and wears
    // formidable-valid while its rule fails. Dropping the IReadOnlyList<T> arm alone breaks the
    // ReadOnlyOnly cases.
    [Theory]
    [InlineData("ImmutableArrayBuilder", false)]
    [InlineData("ImmutableArrayBuilder", true)]
    [InlineData("JsonArray", false)]
    [InlineData("JsonArray", true)]
    [InlineData("GenericOnly", false)]
    [InlineData("GenericOnly", true)]
    [InlineData("ReadOnlyOnly", false)]
    [InlineData("ReadOnlyOnly", true)]
    [InlineData("List", false)]
    [InlineData("List", true)]
    public async Task A_failing_row_of_a_generic_only_list_shows_its_message_and_invalid_class(string collection, bool native)
    {
        var rows = RowsFor(collection, native, "ab", "cd");

        await rows.Cut.InvokeAsync(() => rows.Input(1).Change(""));
        Settle(rows);

        Assert.Equal([TagRequired], rows.Messages(1));
        Assert.Contains("formidable-invalid", rows.Class(1).Split(' '));
        Assert.DoesNotContain("formidable-valid", rows.Class(1).Split(' '));

        await SubmitAsync(rows);

        Assert.Equal([TagRequired], rows.Messages(1));
        Assert.Contains("formidable-invalid", rows.Class(1).Split(' '));
        Assert.False(rows.Engine.GetFieldState(rows.Field(1)).WouldPassSubmit);
        Assert.Empty(rows.Messages(0));
        Assert.True(rows.Engine.GetFieldState(rows.Field(0)).WouldPassSubmit);
    }

    // A presence rule on each row marks each row's own kit input required. Mutation that must
    // break it: the list mutation, and the requirement lands on the list and "[0]", so no row's
    // input carries aria-required.
    [Theory]
    [InlineData("ImmutableArrayBuilder")]
    [InlineData("JsonArray")]
    [InlineData("GenericOnly")]
    [InlineData("ReadOnlyOnly")]
    [InlineData("List")]
    public void Generic_only_rows_carry_the_required_mark(string collection)
    {
        var rows = RowsFor(collection, native: false, "ab", "");

        Assert.Equal("true", rows.Required(0));
        Assert.Equal("true", rows.Required(1));
        Assert.Equal(FieldRequirement.Required, rows.Engine.GetFieldRequirement(rows.Field(1)));
    }

    // A load: "ok" passes and is confirmed, "toolong" fails and is disclosed, and the empty row
    // stays silent. Mutations that must break it: the list mutation (no row answers), and reading
    // an element through the non-generic IList alone in the load (no generic-only row is read, so
    // none answers).
    [Theory]
    [InlineData("ImmutableArrayBuilder")]
    [InlineData("JsonArray")]
    [InlineData("GenericOnly")]
    [InlineData("ReadOnlyOnly")]
    [InlineData("List")]
    public async Task A_load_answers_generic_only_rows(string collection)
    {
        var rows = RowsFor(collection, native: false, "ok", "toolong", null);
        Settle(rows);

        await rows.Cut.InvokeAsync(() => rows.Engine.DiscloseLoadedValuesAsync());
        Settle(rows);

        Assert.Contains("formidable-valid", rows.Class(0).Split(' '));
        Assert.Empty(rows.Messages(0));
        Assert.Equal([TagTooLong], rows.Messages(1));
        Assert.Contains("formidable-invalid", rows.Class(1).Split(' '));
        Assert.Empty(rows.Messages(2));
        Assert.DoesNotContain("formidable-invalid", rows.Class(2).Split(' '));
        Assert.DoesNotContain("formidable-valid", rows.Class(2).Split(' '));
    }

    public sealed class NumberRows
    {
        public ImmutableArray<int?>.Builder Builder { get; set; } = ImmutableArray.CreateBuilder<int?>();

        public GenericOnlyList<int?> Nullable { get; set; } = new([0]);

        public ReadOnlyOnlyList<object?> Objects { get; set; } = new([0]);

        public GenericOnlyList<int> Plain { get; set; } = new([0]);

        public ImmutableList<int?> Immutable { get; set; } = [0];
    }

    private sealed class NumberRowsValidator : AbstractValidator<NumberRows>
    {
        public NumberRowsValidator()
        {
            RuleFor(m => m.Builder).ForEach(v => v.NotNull());
            RuleFor(m => m.Nullable).ForEach(v => v.NotNull());
            RuleFor(m => m.Objects).ForEach(v => v.NotNull());
            RuleFor(m => m.Plain).ForEach(v => v.GreaterThanOrEqualTo(0));
            RuleFor(m => m.Immutable).ForEach(v => v.NotNull());
        }
    }

    // A loaded 0 is filled in where a generic-only list declares a nullable or object element, as
    // NotEmpty() reads it, and empty where it declares an int, so the first three rows are
    // confirmed and the int row stays silent. Only a generic-only list lends its interface's
    // element type: an ImmutableList<int?> is a non-generic IList that is no List<T> or
    // Collection<T>, so its 0 is read by the value's own type and stays silent too. Mutations that
    // must break it: give a generic-only list no declared element type, so the value's own type
    // stands in (the two nullable rows read as empty and stay silent), or take the interface's
    // element type for every list (the ImmutableList row is confirmed).
    [Fact]
    public async Task A_load_reads_a_zero_by_the_element_type_a_generic_only_list_declares()
    {
        var model = new NumberRows();
        model.Builder.Add(0);
        var editContext = new EditContext(model);
        using var engine = new FormidableEngine<NumberRows>(
            model, editContext, new FluentValidationModelValidator<NumberRows>(new NumberRowsValidator()),
            new ReflectionModelIntrospector(), new FormidableOptions(), new FakeTimeProvider());

        await engine.DiscloseLoadedValuesAsync();

        Assert.Contains("formidable-valid", editContext.FieldCssClass(FieldIdentifier.Create(() => model.Builder[0])));
        Assert.Contains("formidable-valid", editContext.FieldCssClass(FieldIdentifier.Create(() => model.Nullable[0])));
        Assert.Contains("formidable-valid", editContext.FieldCssClass(FieldIdentifier.Create(() => model.Objects[0])));
        Assert.Equal(string.Empty, editContext.FieldCssClass(FieldIdentifier.Create(() => model.Plain[0])));
        Assert.Equal(string.Empty, editContext.FieldCssClass(FieldIdentifier.Create(() => model.Immutable[0])));
    }

    public sealed class Grid<TGrid>
    {
        public required TGrid Matrix { get; set; }
    }

    private sealed class GridValidator<TGrid> : AbstractValidator<Grid<TGrid>>
        where TGrid : class, IEnumerable<IEnumerable<string?>>
    {
        public GridValidator() =>
            RuleForEach(m => (IEnumerable<IEnumerable<string?>>)m.Matrix)
                .ForEach(cell => cell.NotEmpty().WithMessage(TagRequired));
    }

    // A list of lists: the required mark reaches each cell, and a load confirms each filled cell
    // and leaves the empty one silent. Counting a row that is itself a generic-only list reads it
    // from its list, as the load does. Mutations that must break it: the list mutation (no cell
    // carries the mark), and counting such a row through the non-generic IList alone (the row
    // counts no cells, so none carries the mark).
    [Theory]
    [InlineData("GenericOnly")]
    [InlineData("ReadOnlyOnly")]
    [InlineData("List")]
    public async Task A_list_of_generic_only_lists_marks_each_cell(string collection)
    {
        IRenderedComponent<IComponent> cut;
        IFormidableEngine engine;
        switch (collection)
        {
            case "GenericOnly":
            {
                var model = new Grid<GenericOnlyList<GenericOnlyList<string?>>> { Matrix = new([new(["ok", ""]), new(["", "ok"])]) };
                (cut, engine) = RenderGrid(model, (r, c) => () => model.Matrix[r][c]);
                break;
            }

            case "ReadOnlyOnly":
            {
                var model = new Grid<ReadOnlyOnlyList<ReadOnlyOnlyList<string?>>> { Matrix = new([new(["ok", ""]), new(["", "ok"])]) };
                (cut, engine) = RenderGrid(model, (r, c) => () => model.Matrix[r][c]);
                break;
            }

            default:
            {
                var model = new Grid<List<List<string?>>> { Matrix = [["ok", ""], ["", "ok"]] };
                (cut, engine) = RenderGrid(model, (r, c) => () => model.Matrix[r][c]);
                break;
            }
        }

        foreach (var cell in new[] { "0-0", "0-1", "1-0", "1-1" })
        {
            Assert.Equal("true", cut.Find($"[data-entry=\"{cell}\"] input").GetAttribute("aria-required"));
        }

        await cut.InvokeAsync(() => engine.DiscloseLoadedValuesAsync());
        cut.WaitForState(() => !engine.IsValidating);
        await cut.InvokeAsync(() => _clock.Advance(TimeSpan.FromSeconds(1)));
        cut.WaitForState(() => !engine.IsValidating);

        string Class(string cell) => cut.Find($"[data-entry=\"{cell}\"] input").GetAttribute("class") ?? string.Empty;
        Assert.Contains("formidable-valid", Class("0-0").Split(' '));
        Assert.Contains("formidable-valid", Class("1-1").Split(' '));
        Assert.DoesNotContain("formidable-invalid", Class("0-1").Split(' '));
        Assert.DoesNotContain("formidable-invalid", Class("1-0").Split(' '));
    }

    // A row of a generic-only list that a page replaced gets the advice a List<T> row gets: key
    // the element around the loop by the list. Mutation that must break it: the list mutation,
    // and the generic-only row gets the object-row advice (null here).
    [Theory]
    [InlineData("GenericOnly")]
    [InlineData("ReadOnlyOnly")]
    [InlineData("ImmutableArrayBuilder")]
    public void Row_key_advice_names_the_index_row_fix_for_a_generic_only_list(string collection)
    {
        var oldPlain = new List<string?> { "a" };
        var newPlain = new List<string?> { "a" };
        var plain = FormidableComponentBase.DescribeIndexedRowFix(
            FieldIdentifier.Create(() => oldPlain[0]), FieldIdentifier.Create(() => newPlain[0]));

        var (registered, current) = collection switch
        {
            "GenericOnly" => Pair(new GenericOnlyList<string?>(["a"]), new GenericOnlyList<string?>(["a"])),
            "ReadOnlyOnly" => Pair(new ReadOnlyOnlyList<string?>(["a"]), new ReadOnlyOnlyList<string?>(["a"])),
            _ => Pair(ImmutableArray.CreateBuilder<string?>(), ImmutableArray.CreateBuilder<string?>()),
        };
        var generic = FormidableComponentBase.DescribeIndexedRowFix(registered, current);

        Assert.NotNull(plain);
        Assert.Equal(plain, generic);

        static (FieldIdentifier, FieldIdentifier) Pair(object before, object after) =>
            (new FieldIdentifier(before, "0"), new FieldIdentifier(after, "0"));
    }

    // An input bound through an int indexer that reads by id is reached by no answer:
    // FluentValidation numbers the bag's items by position, so the failure on id 0 (at position
    // 1) files under "[1]". Such a row never wears the valid class, before the submit or after
    // it. Mutation that must break it: remove the guard on a digit-named field whose model is no
    // list from SubmitCoverageTracker.WouldPassSubmit, and the cleared row wears formidable-valid
    // with WouldPassSubmit true.
    [Fact]
    public async Task A_row_read_by_id_through_an_int_indexer_never_wears_the_valid_class()
    {
        var model = new Holder<IdIndexedBag> { Tags = new IdIndexedBag() };
        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<Holder<IdIndexedBag>>>(0);
            builder.AddComponentParameter(1, "Model", model);
            builder.AddComponentParameter(2, "Validator", (IModelValidator<Holder<IdIndexedBag>>)new FluentValidationModelValidator<Holder<IdIndexedBag>>(new TextTagsValidator<IdIndexedBag>()));
            builder.AddComponentParameter(3, "ChildContent", (RenderFragment<FormidableFormContext>)(_ => inner =>
                RenderTextRow(inner, 0, 0, () => model.Tags[0], value => model.Tags.Set(0, value), native: false)));
            builder.CloseComponent();
        });
        var rows = new Rows(cut, cut.FindComponent<FormidableForm<Holder<IdIndexedBag>>>().Instance.Engine!, _ => FieldIdentifier.Create(() => model.Tags[0]));
        Assert.False(ResolvedFieldExtensions.IsIndexedList(model.Tags));

        await rows.Cut.InvokeAsync(() => rows.Input(0).Change(""));
        Settle(rows);

        Assert.DoesNotContain("formidable-valid", rows.Class(0).Split(' '));
        Assert.False(rows.Engine.GetFieldState(rows.Field(0)).WouldPassSubmit);

        await SubmitAsync(rows);

        Assert.DoesNotContain("formidable-valid", rows.Class(0).Split(' '));
        Assert.False(rows.Engine.GetFieldState(rows.Field(0)).WouldPassSubmit);
    }

    public sealed class Pairs<TList>
    {
        public required TList Entries { get; set; }
    }

    private sealed class PairsValidator<TList> : AbstractValidator<Pairs<TList>>
        where TList : class, IEnumerable<KeyValuePair<int, string?>>
    {
        public PairsValidator() =>
            RuleForEach(m => (IEnumerable<KeyValuePair<int, string?>>)m.Entries)
                .Must(pair => !string.IsNullOrEmpty(pair.Value)).WithMessage(TagRequired);
    }

    // A generic-only list of key-value pairs is a list, not a dictionary: its rows are named by
    // index, and a passing row keeps the vouch a List<T> of pairs gives it. Mutation that must
    // break it: let SubmitCoverageTracker.IsDictionary read a generic-only list's first item as it
    // reads any other collection's, and the generic-only list reads as a dictionary whose passing
    // row is never vouched for.
    [Theory]
    [InlineData("GenericOnly")]
    [InlineData("List")]
    public async Task A_generic_only_list_of_pairs_is_a_list_and_keeps_its_vouch(string collection)
    {
        KeyValuePair<int, string?>[] seed = [new(1, "fine"), new(2, "")];
        if (collection == "GenericOnly")
        {
            var model = new Pairs<GenericOnlyList<KeyValuePair<int, string?>>> { Entries = new(seed) };
            await AssertPairsAsync(model, model.Entries, FieldIdentifier.Create(() => model.Entries[0]), FieldIdentifier.Create(() => model.Entries[1]));
        }
        else
        {
            var model = new Pairs<List<KeyValuePair<int, string?>>> { Entries = [.. seed] };
            await AssertPairsAsync(model, model.Entries, FieldIdentifier.Create(() => model.Entries[0]), FieldIdentifier.Create(() => model.Entries[1]));
        }
    }

    private static async Task AssertPairsAsync<TList>(Pairs<TList> model, TList entries, FieldIdentifier passing, FieldIdentifier failing)
        where TList : class, IEnumerable<KeyValuePair<int, string?>>
    {
        using var engine = new FormidableEngine<Pairs<TList>>(
            model, new EditContext(model), new FluentValidationModelValidator<Pairs<TList>>(new PairsValidator<TList>()),
            new ReflectionModelIntrospector(), new FormidableOptions(), new FakeTimeProvider());

        var outcome = await engine.ValidateForSubmitAsync();

        Assert.False(SubmitCoverageTracker.IsDictionary(entries));
        Assert.Equal(["Entries[1]"], outcome.Report.Issues.Select(issue => issue.Path));
        Assert.True(engine.GetFieldState(passing).WouldPassSubmit);
        Assert.False(engine.GetFieldState(failing).WouldPassSubmit);
    }

    private static string Text(JsonNode node) => node.GetValue<string>();

    /// <summary>Renders the rows of <paramref name="collection"/> seeded with <paramref name="seed"/>, each bound by its index with a kit or a native input and its message.</summary>
    private Rows RowsFor(string collection, bool native, params string?[] seed) => collection switch
    {
        "ImmutableArrayBuilder" => TextRows(BuildArrayBuilder(seed), (h, i) => () => h.Tags[i], (h, i, v) => h.Tags[i] = v, native),
        "GenericOnly" => TextRows(new Holder<GenericOnlyList<string?>> { Tags = new(seed) }, (h, i) => () => h.Tags[i], (h, i, v) => h.Tags[i] = v, native),
        "ReadOnlyOnly" => TextRows(new Holder<ReadOnlyOnlyList<string?>> { Tags = new(seed) }, (h, i) => () => h.Tags[i], (h, i, v) => h.Tags.Set(i, v), native),
        "List" => TextRows(new Holder<List<string?>> { Tags = [.. seed] }, (h, i) => () => h.Tags[i], (h, i, v) => h.Tags[i] = v, native),
        "JsonArray" => NodeRows(new Holder<JsonArray> { Tags = new JsonArray(seed.Select(Node).ToArray()) }, native),
        _ => throw new ArgumentOutOfRangeException(nameof(collection), collection, null),
    };

    private static JsonNode? Node(string? text) => text is null ? null : JsonValue.Create(text);

    private static Holder<ImmutableArray<string?>.Builder> BuildArrayBuilder(string?[] seed)
    {
        var builder = ImmutableArray.CreateBuilder<string?>();
        builder.AddRange(seed);
        return new Holder<ImmutableArray<string?>.Builder> { Tags = builder };
    }

    private Rows TextRows<TList>(
        Holder<TList> model,
        Func<Holder<TList>, int, Expression<Func<string?>>> row,
        Action<Holder<TList>, int, string?> write,
        bool native)
        where TList : class, IEnumerable<string?>
    {
        var count = model.Tags.Count();
        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<Holder<TList>>>(0);
            builder.AddComponentParameter(1, "Model", model);
            builder.AddComponentParameter(2, "Validator", (IModelValidator<Holder<TList>>)new FluentValidationModelValidator<Holder<TList>>(new TextTagsValidator<TList>()));
            builder.AddComponentParameter(3, "ChildContent", (RenderFragment<FormidableFormContext>)(_ => inner =>
            {
                for (var i = 0; i < count; i++)
                {
                    var index = i;
                    RenderTextRow(inner, index, index, row(model, index), value => write(model, index, value), native);
                }

                inner.AddMarkupContent(100, "<button type=\"submit\">Save</button>");
            }));
            builder.CloseComponent();
        });

        return new Rows(cut, cut.FindComponent<FormidableForm<Holder<TList>>>().Instance.Engine!, index => FieldIdentifier.Create(row(model, index)));
    }

    private Rows NodeRows(Holder<JsonArray> model, bool native)
    {
        var count = model.Tags.Count;
        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<Holder<JsonArray>>>(0);
            builder.AddComponentParameter(1, "Model", model);
            builder.AddComponentParameter(2, "Validator", (IModelValidator<Holder<JsonArray>>)new FluentValidationModelValidator<Holder<JsonArray>>(new NodeTagsValidator()));
            builder.AddComponentParameter(3, "ChildContent", (RenderFragment<FormidableFormContext>)(_ => inner =>
            {
                for (var i = 0; i < count; i++)
                {
                    var index = i;
                    RenderNodeRow(inner, index, index, () => model.Tags[index], value => model.Tags[index] = Node(value), native);
                }

                inner.AddMarkupContent(100, "<button type=\"submit\">Save</button>");
            }));
            builder.CloseComponent();
        });

        return new Rows(cut, cut.FindComponent<FormidableForm<Holder<JsonArray>>>().Instance.Engine!, index => FieldIdentifier.Create(() => model.Tags[index]));
    }

    /// <summary>Renders a string row inside an element marked <c>data-entry</c>: a kit input and its message, or a native <c>InputText</c> with its message and anchor.</summary>
    private void RenderTextRow(RenderTreeBuilder builder, int region, int name, Expression<Func<string?>> accessor, Action<string?> assign, bool native)
    {
        builder.OpenRegion(region);
        builder.OpenElement(0, "div");
        builder.AddAttribute(1, "data-entry", name.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (native)
        {
            builder.OpenComponent<InputText>(2);
            builder.AddComponentParameter(3, "Value", accessor.Compile()());
            builder.AddComponentParameter(4, "ValueChanged", EventCallback.Factory.Create(this, assign));
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
            builder.AddComponentParameter(3, "Value", accessor.Compile()());
            builder.AddComponentParameter(4, "ValueChanged", EventCallback.Factory.Create(this, assign));
            builder.AddComponentParameter(5, "ValueExpression", accessor);
            builder.CloseComponent();
            builder.OpenComponent<FormidableFieldMessage<string?>>(6);
            builder.AddComponentParameter(7, "For", accessor);
            builder.CloseComponent();
        }

        builder.CloseElement();
        builder.CloseRegion();
    }

    /// <summary>Renders a <see cref="JsonArray"/> row inside an element marked <c>data-entry</c>: a <c>FormidableField</c> around a plain input and its message, or a native input of the page's own with its message and anchor.</summary>
    private void RenderNodeRow(RenderTreeBuilder builder, int region, int name, Expression<Func<JsonNode?>> accessor, Action<string?> assign, bool native)
    {
        builder.OpenRegion(region);
        builder.OpenElement(0, "div");
        builder.AddAttribute(1, "data-entry", name.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (native)
        {
            builder.OpenComponent<JsonTextInput>(2);
            builder.AddComponentParameter(3, "Value", accessor.Compile()());
            builder.AddComponentParameter(4, "ValueChanged", EventCallback.Factory.Create<JsonNode?>(this, node => assign(node is null ? null : Text(node))));
            builder.AddComponentParameter(5, "ValueExpression", accessor);
            builder.CloseComponent();
            builder.OpenComponent<ValidationMessage<JsonNode?>>(6);
            builder.AddComponentParameter(7, "For", accessor);
            builder.CloseComponent();
            builder.OpenComponent<FormidableFieldAnchor<JsonNode?>>(8);
            builder.AddComponentParameter(9, "For", accessor);
            builder.CloseComponent();
        }
        else
        {
            builder.OpenComponent<FormidableField<JsonNode?>>(2);
            builder.AddComponentParameter(3, "For", accessor);
            builder.AddComponentParameter(4, "ChildContent", (RenderFragment<FormidableFieldContext>)(field => input =>
            {
                input.OpenElement(0, "input");
                input.AddMultipleAttributes(1, field.InputAttributes);
                input.AddAttribute(2, "value", accessor.Compile()() is { } node ? Text(node) : string.Empty);
                input.AddAttribute(3, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this, e => field.Edit(() => assign(e.Value as string))));
                input.CloseElement();
            }));
            builder.CloseComponent();
            builder.OpenComponent<FormidableFieldMessage<JsonNode?>>(5);
            builder.AddComponentParameter(6, "For", accessor);
            builder.CloseComponent();
        }

        builder.CloseElement();
        builder.CloseRegion();
    }

    /// <summary>A page's own input for a <see cref="JsonNode"/> holding text, built on Blazor's <see cref="InputBase{TValue}"/>.</summary>
    public sealed class JsonTextInput : InputBase<JsonNode?>
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenElement(0, "input");
            builder.AddMultipleAttributes(1, AdditionalAttributes);
            builder.AddAttribute(2, "class", CssClass);
            builder.AddAttribute(3, "value", CurrentValueAsString);
            builder.AddAttribute(4, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this, e => CurrentValueAsString = e.Value as string));
            builder.CloseElement();
        }

        protected override string? FormatValueAsString(JsonNode? value) => value is null ? null : Text(value);

        protected override bool TryParseValueFromString(string? value, out JsonNode? result, out string validationErrorMessage)
        {
            result = Node(value);
            validationErrorMessage = string.Empty;
            return true;
        }
    }

    /// <summary>Renders one kit input per cell of a two-by-two grid, each bound with the accessor <paramref name="cell"/> gives, <c>() => model.Matrix[row][column]</c>.</summary>
    private (IRenderedComponent<IComponent> Cut, IFormidableEngine Engine) RenderGrid<TGrid>(Grid<TGrid> model, Func<int, int, Expression<Func<string?>>> cell)
        where TGrid : class, IEnumerable<IEnumerable<string?>>
    {
        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<Grid<TGrid>>>(0);
            builder.AddComponentParameter(1, "Model", model);
            builder.AddComponentParameter(2, "Validator", (IModelValidator<Grid<TGrid>>)new FluentValidationModelValidator<Grid<TGrid>>(new GridValidator<TGrid>()));
            builder.AddComponentParameter(3, "ChildContent", (RenderFragment<FormidableFormContext>)(_ => inner =>
            {
                var seq = 0;
                for (var r = 0; r < 2; r++)
                {
                    for (var c = 0; c < 2; c++)
                    {
                        var (row, column) = (r, c);
                        inner.OpenRegion(seq++);
                        inner.OpenElement(0, "div");
                        inner.AddAttribute(1, "data-entry", $"{row}-{column}");
                        inner.OpenComponent<FormidableInputText>(2);
                        inner.AddComponentParameter(3, "Value", cell(row, column).Compile()());
                        inner.AddComponentParameter(4, "ValueChanged", EventCallback.Factory.Create<string?>(this, _ => { }));
                        inner.AddComponentParameter(5, "ValueExpression", cell(row, column));
                        inner.CloseComponent();
                        inner.CloseElement();
                        inner.CloseRegion();
                    }
                }
            }));
            builder.CloseComponent();
        });

        return (cut, cut.FindComponent<FormidableForm<Grid<TGrid>>>().Instance.Engine!);
    }

    /// <summary>Waits for every check to land, lets the whole-form check a change arms run, then waits again.</summary>
    private void Settle(Rows rows)
    {
        rows.Cut.WaitForState(() => !rows.Engine.IsValidating);
        rows.Cut.InvokeAsync(() => _clock.Advance(new FormidableOptions().RefreshDebounce + TimeSpan.FromMilliseconds(1))).GetAwaiter().GetResult();
        rows.Cut.WaitForState(() => !rows.Engine.IsValidating);
    }

    /// <summary>Submits and waits for the submit's answer to render.</summary>
    private static async Task SubmitAsync(Rows rows)
    {
        await rows.Cut.InvokeAsync(() => rows.Cut.Find("form").Submit());
        rows.Cut.WaitForState(() => rows.Engine.HasSubmitted && !rows.Engine.IsValidating);
        await rows.Cut.InvokeAsync(() => { });
    }

    private sealed record Rows(IRenderedComponent<IComponent> Cut, IFormidableEngine Engine, Func<int, FieldIdentifier> Field)
    {
        public AngleSharp.Dom.IElement Input(int row) => Cut.Find($"[data-entry=\"{row}\"] input");

        public string Class(int row) => Input(row).GetAttribute("class") ?? string.Empty;

        public string Required(int row) => Input(row).GetAttribute("aria-required") ?? "-";

        public IReadOnlyList<string> Messages(int row) =>
            Cut.FindAll($"[data-entry=\"{row}\"] li, [data-entry=\"{row}\"] .validation-message")
                .Select(element => element.TextContent.Trim())
                .ToList();
    }
}
