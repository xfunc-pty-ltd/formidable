using System.Runtime.CompilerServices;
using Bunit;
using FluentValidation;
using Formidable.Blazor.Tests.Fixtures;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;

namespace Formidable.Blazor.Tests;

/// <summary>
/// Pins <c>AddItem</c> and <c>RemoveItem</c> on <c>FormidableFieldContext</c>: each edits the
/// list, then reports the change for the collection field, so the list's own rule answers before
/// a submit. A removal that removed nothing, and an add the list refuses, report nothing.
/// </summary>
// LiveDebounce stays at its default in every test here. With no wait, the live check starts
// inside the change itself, which is what lets a check that ran before the edit show.
public class FormidableFieldContextRowTests : BunitContext
{
    private const string AtLeastOne = "Add at least one item";

    public FormidableFieldContextRowTests()
    {
        Services.AddFormidable();
        Services.AddSingleton<FluentValidation.IValidator<RowListModel>, RowListValidator>();
        Services.AddSingleton<FluentValidation.IValidator<RowCollectionModel>, RowCollectionValidator>();
        Services.AddSingleton<FluentValidation.IValidator<RowMatchModel>, RowMatchValidator>();
    }

    // The validator reads the list when the check runs, so the field passing submit after the add
    // proves the check saw the appended item: an empty list fails "at least one". Mutation that
    // must break it: notify before appending in AddItem, and the check reads the empty list, fails,
    // and the field never passes.
    [Fact]
    public async Task AddItem_appends_then_engages_the_collection_field()
    {
        var model = new RowListModel();
        var captured = new StrongBox<FormidableFieldContext>();
        var form = RenderList(model, captured);
        var engine = form.Instance.Engine!;
        var items = ItemsField(model);
        Assert.False(engine.GetFieldState(items).WouldPassSubmit);

        var added = new RowListItem();
        await form.InvokeAsync(() => captured.Value!.AddItem(model.Items, added));

        Assert.Same(added, Assert.Single(model.Items));
        Assert.True(engine.EditContext.IsModified(items));
        form.WaitForAssertion(() => Assert.True(engine.GetFieldState(items).WouldPassSubmit));
        Assert.Empty(form.FindAll("li"));

        await form.InvokeAsync(() => captured.Value!.RemoveItem(model.Items, added));

        form.WaitForAssertion(() => Assert.Equal(AtLeastOne, form.Find("li").TextContent));
        Assert.False(engine.HasSubmitted);
    }

    // Mutation that must break it: insert at the front in AddItem (list.Insert(0, item)), and the
    // added item lands ahead of the one already there.
    [Fact]
    public async Task AddItem_appends_after_the_items_already_in_the_list()
    {
        var first = new RowListItem();
        var model = new RowListModel { Items = [first] };
        var captured = new StrongBox<FormidableFieldContext>();
        var form = RenderList(model, captured);

        var added = new RowListItem();
        await form.InvokeAsync(() => captured.Value!.AddItem(model.Items, added));

        Assert.Equal([first, added], model.Items);
    }

    // Mutations that must break it: notify before removing in RemoveItem (the check reads the
    // list with the item still in it, passes, and no message appears), and return the negation
    // of what Remove returned (the Assert.True fails).
    [Fact]
    public async Task RemoveItem_returns_true_and_notifies_when_an_item_went()
    {
        var only = new RowListItem();
        var model = new RowListModel { Items = [only] };
        var captured = new StrongBox<FormidableFieldContext>();
        var form = RenderList(model, captured);
        var engine = form.Instance.Engine!;

        var removed = await form.InvokeAsync(() => captured.Value!.RemoveItem(model.Items, only));

        Assert.True(removed);
        Assert.Empty(model.Items);
        Assert.True(engine.EditContext.IsModified(ItemsField(model)));
        form.WaitForAssertion(() => Assert.Equal(AtLeastOne, form.Find("li").TextContent));
        Assert.False(engine.HasSubmitted);
    }

    // The list starts empty, so a notify here would engage a field whose rule fails. IsModified is
    // the assertion that carries the test, because a notify marks the field modified before it
    // returns. Mutation that must break it: notify whatever Remove returned.
    [Fact]
    public async Task RemoveItem_of_an_absent_item_returns_false_and_does_not_engage()
    {
        var model = new RowListModel();
        var captured = new StrongBox<FormidableFieldContext>();
        var form = RenderList(model, captured);
        var engine = form.Instance.Engine!;

        var removed = await form.InvokeAsync(() => captured.Value!.RemoveItem(model.Items, new RowListItem()));

        Assert.False(removed);
        Assert.False(engine.EditContext.IsModified(ItemsField(model)));
        Assert.False(engine.GetFieldState(ItemsField(model)).IsModified);
        Assert.Empty(form.FindAll("li"));
    }

    // An array is a fixed-size IList<T>: its Add throws NotSupportedException, and that exception
    // reaches the caller unchanged. Mutation that must break it: notify before appending in
    // AddItem, and the field is marked modified before the array refuses the item.
    [Fact]
    public async Task AddItem_on_a_fixed_size_list_throws_the_lists_exception_and_does_not_engage()
    {
        var first = new RowListItem();
        var model = new RowListModel { Items = new[] { first } };
        var captured = new StrongBox<FormidableFieldContext>();
        var form = RenderList(model, captured);
        var engine = form.Instance.Engine!;

        await Assert.ThrowsAsync<NotSupportedException>(
            () => form.InvokeAsync(() => captured.Value!.AddItem(model.Items, new RowListItem())));

        Assert.Same(first, Assert.Single(model.Items));
        Assert.False(engine.EditContext.IsModified(ItemsField(model)));
    }

    // Mutation that must break it: drop the null checks, and both calls throw
    // NullReferenceException, which is not the exact type asserted.
    [Fact]
    public async Task AddItem_and_RemoveItem_reject_a_null_list()
    {
        var model = new RowListModel();
        var captured = new StrongBox<FormidableFieldContext>();
        var form = RenderList(model, captured);
        var context = captured.Value!;

        var add = await Assert.ThrowsAsync<ArgumentNullException>(
            () => form.InvokeAsync(() => context.AddItem<RowListItem>(null!, new RowListItem())));
        var remove = await Assert.ThrowsAsync<ArgumentNullException>(
            () => form.InvokeAsync(() => context.RemoveItem<RowListItem>(null!, new RowListItem())));

        Assert.Equal("list", add.ParamName);
        Assert.Equal("list", remove.ParamName);
        Assert.False(form.Instance.Engine!.EditContext.IsModified(ItemsField(model)));
    }

    // The model declares its list as ICollection<T>, the shape an entity's navigation collection
    // takes, and the page passes that property straight in. Mutation that must break it: the
    // parameter back to IList<TItem>, and the test project fails to build.
    [Fact]
    public async Task AddItem_and_RemoveItem_take_a_property_declared_as_ICollection()
    {
        var model = new RowCollectionModel { Items = new List<RowListItem>() };
        var captured = new StrongBox<FormidableFieldContext>();
        var form = this.RenderCollectionField(model, () => model.Items, captured);
        var engine = form.Instance.Engine!;
        var items = new FieldIdentifier(model, nameof(RowCollectionModel.Items));

        var added = new RowListItem();
        await form.InvokeAsync(() => captured.Value!.AddItem(model.Items, added));

        Assert.Same(added, Assert.Single(model.Items));
        form.WaitForAssertion(() => Assert.True(engine.GetFieldState(items).WouldPassSubmit));

        var removed = await form.InvokeAsync(() => captured.Value!.RemoveItem(model.Items, added));

        Assert.True(removed);
        Assert.Empty(model.Items);
        form.WaitForAssertion(() => Assert.Equal(AtLeastOne, form.Find("li").TextContent));
        Assert.False(engine.HasSubmitted);
    }

    // A set has no positions, so RemoveItem hands the item to the set's own Remove. Mutation that
    // must break it: cast every collection to IList<TItem>, and the set throws
    // InvalidCastException on the first call.
    [Fact]
    public async Task RemoveItem_on_a_set_uses_the_sets_own_matching()
    {
        var kept = new RowListItem();
        var model = new RowCollectionModel { Items = new HashSet<RowListItem> { kept } };
        var captured = new StrongBox<FormidableFieldContext>();
        var form = this.RenderCollectionField(model, () => model.Items, captured);
        var engine = form.Instance.Engine!;
        var items = new FieldIdentifier(model, nameof(RowCollectionModel.Items));

        var absent = await form.InvokeAsync(() => captured.Value!.RemoveItem(model.Items, new RowListItem()));

        Assert.False(absent);
        Assert.Same(kept, Assert.Single(model.Items));
        Assert.False(engine.EditContext.IsModified(items));

        var present = await form.InvokeAsync(() => captured.Value!.RemoveItem(model.Items, kept));

        Assert.True(present);
        Assert.Empty(model.Items);
        Assert.True(engine.EditContext.IsModified(items));
        form.WaitForAssertion(() => Assert.Equal(AtLeastOne, form.Find("li").TextContent));
    }

    // A set ignores an item it already holds, so there is no change to report. Mutation that must
    // break it: report unconditionally, as for any other collection, and the field is marked
    // modified.
    [Fact]
    public async Task AddItem_on_a_set_that_already_holds_the_item_reports_nothing()
    {
        var kept = new RowListItem();
        var model = new RowCollectionModel { Items = new HashSet<RowListItem> { kept } };
        var captured = new StrongBox<FormidableFieldContext>();
        var form = this.RenderCollectionField(model, () => model.Items, captured);

        await form.InvokeAsync(() => captured.Value!.AddItem(model.Items, kept));

        Assert.Same(kept, Assert.Single(model.Items));
        Assert.False(form.Instance.Engine!.EditContext.IsModified(new FieldIdentifier(model, nameof(RowCollectionModel.Items))));
    }

    // A pin, not a red-first test: a set that takes the item reports it. Mutation that must break
    // it: return without reporting for every set, and the field is never marked modified.
    [Fact]
    public async Task AddItem_on_a_set_reports_an_item_it_did_not_hold()
    {
        var model = new RowCollectionModel { Items = new HashSet<RowListItem>() };
        var captured = new StrongBox<FormidableFieldContext>();
        var form = this.RenderCollectionField(model, () => model.Items, captured);
        var engine = form.Instance.Engine!;
        var items = new FieldIdentifier(model, nameof(RowCollectionModel.Items));

        var added = new RowListItem();
        await form.InvokeAsync(() => captured.Value!.AddItem(model.Items, added));

        Assert.Same(added, Assert.Single(model.Items));
        Assert.True(engine.EditContext.IsModified(items));
        form.WaitForAssertion(() => Assert.True(engine.GetFieldState(items).WouldPassSubmit));
    }

    // A pin, not a red-first test: a set of rows with value equality removes an equal row that is
    // not the instance passed, because a set removes by its own rule, not the list rule. Mutation
    // that must break it: match every collection by the list rule (the same instance for a
    // class), and the set keeps its row.
    [Fact]
    public async Task RemoveItem_on_a_set_of_equal_rows_removes_an_equal_row()
    {
        var inSet = new EqualRow(1);
        var equal = new EqualRow(1);
        Assert.False(ReferenceEquals(inSet, equal));
        var model = new RowMatchModel { RowSet = new HashSet<EqualRow> { inSet } };
        var captured = new StrongBox<FormidableFieldContext>();
        var form = this.RenderCollectionField(model, () => model.RowSet, captured);

        var removed = await form.InvokeAsync(() => captured.Value!.RemoveItem(model.RowSet, equal));

        Assert.True(removed);
        Assert.Empty(model.RowSet);
        Assert.True(form.Instance.Engine!.EditContext.IsModified(new FieldIdentifier(model, nameof(RowMatchModel.RowSet))));
    }

    // A pin, not a red-first test: a read-only collection that is not a list refuses its own
    // Remove whether or not it holds the item, and nothing is reported either way. Mutation that
    // must break it: report before the collection's own Remove, and the field is marked modified
    // before the collection refuses.
    [Fact]
    public async Task RemoveItem_on_a_read_only_collection_that_is_not_a_list_throws_either_way()
    {
        var kept = new RowListItem();
        var model = new RowCollectionModel { Items = new Dictionary<RowListItem, int> { [kept] = 1 }.Keys };
        var captured = new StrongBox<FormidableFieldContext>();
        var form = this.RenderCollectionField(model, () => model.Items, captured);

        await Assert.ThrowsAsync<NotSupportedException>(
            () => form.InvokeAsync(() => captured.Value!.RemoveItem(model.Items, new RowListItem())));
        await Assert.ThrowsAsync<NotSupportedException>(
            () => form.InvokeAsync(() => captured.Value!.RemoveItem(model.Items, kept)));

        Assert.Same(kept, Assert.Single(model.Items));
        Assert.False(form.Instance.Engine!.EditContext.IsModified(new FieldIdentifier(model, nameof(RowCollectionModel.Items))));
    }

    // Two rows that compare equal are still two rows, and the one passed is the one that goes.
    // Mutation that must break it: list.Remove(item), which removes the first equal row, so the
    // row left is the one passed.
    [Fact]
    public async Task RemoveItem_removes_the_instance_passed_when_two_rows_compare_equal()
    {
        var first = new EqualRow(1);
        var second = new EqualRow(1);
        Assert.Equal(first, second);
        var model = new RowMatchModel { Rows = [first, second] };
        var captured = new StrongBox<FormidableFieldContext>();
        var form = this.RenderCollectionField(model, () => model.Rows, captured);

        var removed = await form.InvokeAsync(() => captured.Value!.RemoveItem(model.Rows, second));

        Assert.True(removed);
        Assert.Same(first, Assert.Single(model.Rows));
        Assert.True(form.Instance.Engine!.EditContext.IsModified(new FieldIdentifier(model, nameof(RowMatchModel.Rows))));
    }

    // The row passed was never in the list; only a row equal to it is. Mutation that must break
    // it: value equality for every reference type, and the equal row goes and is reported.
    [Fact]
    public async Task RemoveItem_of_an_equal_row_not_in_the_list_returns_false_and_reports_nothing()
    {
        var inList = new EqualRow(1);
        var model = new RowMatchModel { Rows = [inList] };
        var captured = new StrongBox<FormidableFieldContext>();
        var form = this.RenderCollectionField(model, () => model.Rows, captured);

        var removed = await form.InvokeAsync(() => captured.Value!.RemoveItem(model.Rows, new EqualRow(1)));

        Assert.False(removed);
        Assert.Same(inList, Assert.Single(model.Rows));
        Assert.False(form.Instance.Engine!.EditContext.IsModified(new FieldIdentifier(model, nameof(RowMatchModel.Rows))));
    }

    // A pin, not a red-first test: removing by value from a value-type list held before this
    // change. A boxed value is a fresh object each time, so a reference test never matches one.
    // Mutation that must break it: a reference test for every TItem, and nothing is removed.
    [Fact]
    public async Task RemoveItem_on_a_value_type_list_matches_by_value()
    {
        var model = new RowMatchModel { Numbers = [1, 2, 3] };
        var captured = new StrongBox<FormidableFieldContext>();
        var form = this.RenderCollectionField(model, () => model.Numbers, captured);

        var removed = await form.InvokeAsync(() => captured.Value!.RemoveItem(model.Numbers, 2));

        Assert.True(removed);
        Assert.Equal([1, 3], model.Numbers);
        Assert.True(form.Instance.Engine!.EditContext.IsModified(new FieldIdentifier(model, nameof(RowMatchModel.Numbers))));
    }

    // A pin, not a red-first test: a string matched by value before this change. A tag typed
    // into an input arrives as a new string instance, never the one in the list. Mutation that
    // must break it: a reference test for string, and the typed tag matches nothing.
    [Fact]
    public async Task RemoveItem_of_a_typed_in_string_removes_the_equal_tag()
    {
        var model = new RowMatchModel { Tags = ["urgent", "later"] };
        var captured = new StrongBox<FormidableFieldContext>();
        var form = this.RenderCollectionField(model, () => model.Tags, captured);
        var typed = new string("urgent".ToCharArray());
        Assert.False(ReferenceEquals(model.Tags[0], typed));

        var removed = await form.InvokeAsync(() => captured.Value!.RemoveItem(model.Tags, typed));

        Assert.True(removed);
        Assert.Equal(["later"], model.Tags);
        Assert.True(form.Instance.Engine!.EditContext.IsModified(new FieldIdentifier(model, nameof(RowMatchModel.Tags))));
    }

    // A pin, not a red-first test: the rule keys on the list's item type, not on what each item
    // holds, so a list of object matches only the instance passed, even for a boxed number.
    // Mutation that must break it: key the rule on the item's own type (item is ValueType or
    // string), and the equal box goes in place of the one passed.
    [Fact]
    public async Task RemoveItem_on_a_list_of_object_matches_only_the_instance_even_when_boxed()
    {
        object box = 2;
        object equal = 2;
        Assert.False(ReferenceEquals(box, equal));
        var model = new RowMatchModel { Objects = [1, box, 3] };
        var captured = new StrongBox<FormidableFieldContext>();
        var form = this.RenderCollectionField(model, () => model.Objects, captured);
        var objects = new FieldIdentifier(model, nameof(RowMatchModel.Objects));

        var removedEqual = await form.InvokeAsync(() => captured.Value!.RemoveItem(model.Objects, equal));

        Assert.False(removedEqual);
        Assert.Same(box, model.Objects[1]);
        Assert.False(form.Instance.Engine!.EditContext.IsModified(objects));

        var removedSame = await form.InvokeAsync(() => captured.Value!.RemoveItem(model.Objects, box));

        Assert.True(removedSame);
        Assert.Equal([1, 3], model.Objects);
        Assert.True(form.Instance.Engine!.EditContext.IsModified(objects));
    }

    // An array is a fixed-size IList<T>. Looking for a row it does not hold is a question, not an
    // edit, so it answers false like any list. Mutation that must break it: skip the not-found
    // test and call RemoveAt, and the array throws NotSupportedException.
    [Fact]
    public async Task RemoveItem_on_an_array_without_the_item_returns_false_and_reports_nothing()
    {
        var first = new RowListItem();
        var model = new RowListModel { Items = new[] { first } };
        var captured = new StrongBox<FormidableFieldContext>();
        var form = RenderList(model, captured);

        var removed = await form.InvokeAsync(() => captured.Value!.RemoveItem(model.Items, new RowListItem()));

        Assert.False(removed);
        Assert.Same(first, Assert.Single(model.Items));
        Assert.False(form.Instance.Engine!.EditContext.IsModified(ItemsField(model)));
    }

    // A pin, not a red-first test: an array holding the row refuses to shrink with its own
    // exception, and nothing is reported. Mutation that must break it: report before RemoveAt,
    // and the field is marked modified before the array refuses.
    [Fact]
    public async Task RemoveItem_on_an_array_holding_the_item_throws_and_reports_nothing()
    {
        var first = new RowListItem();
        var model = new RowListModel { Items = new[] { first } };
        var captured = new StrongBox<FormidableFieldContext>();
        var form = RenderList(model, captured);

        await Assert.ThrowsAsync<NotSupportedException>(
            () => form.InvokeAsync(() => captured.Value!.RemoveItem(model.Items, first)));

        Assert.Same(first, Assert.Single(model.Items));
        Assert.False(form.Instance.Engine!.EditContext.IsModified(ItemsField(model)));
    }

    private static FieldIdentifier ItemsField(RowListModel model) => new(model, nameof(RowListModel.Items));

    /// <summary>Renders the list the way a page does: a <c>FormidableField</c> wrapping it, and a <c>FormidableCollectionMessage</c> for the list's own rule.</summary>
    private IRenderedComponent<FormidableForm<RowListModel>> RenderList(RowListModel model, StrongBox<FormidableFieldContext> captured) =>
        this.RenderCollectionField(model, () => model.Items, captured);
}

/// <summary>A model whose list can hold a growable list or a fixed-size array.</summary>
public sealed class RowListModel
{
    public IList<RowListItem> Items { get; set; } = new List<RowListItem>();
}

public sealed class RowListItem
{
    public string Name { get; set; } = string.Empty;
}

/// <summary>The list's own rule, in the submit bucket, as the tutorial's member list carries it.</summary>
public sealed class RowListValidator : DraftSubmitValidator<RowListModel>
{
    protected override void ConfigureDraftRules()
    {
    }

    protected override void ConfigureSubmitRules()
    {
        RuleFor(m => m.Items).Must(items => items.Count >= 1).WithMessage("Add at least one item");
    }
}

/// <summary>A model that declares its list as <c>ICollection&lt;T&gt;</c>, as an entity's navigation collection does.</summary>
public sealed class RowCollectionModel
{
    public ICollection<RowListItem> Items { get; set; } = new List<RowListItem>();
}

public sealed class RowCollectionValidator : DraftSubmitValidator<RowCollectionModel>
{
    protected override void ConfigureDraftRules()
    {
    }

    protected override void ConfigureSubmitRules()
    {
        RuleFor(m => m.Items).Must(items => items.Count >= 1).WithMessage("Add at least one item");
    }
}

/// <summary>Lists whose items match differently: rows with value equality, numbers, tags, and objects.</summary>
public sealed class RowMatchModel
{
    public List<EqualRow> Rows { get; set; } = [];

    public List<int> Numbers { get; set; } = [];

    public List<string> Tags { get; set; } = [];

    public List<object> Objects { get; set; } = [];

    public ICollection<EqualRow> RowSet { get; set; } = new HashSet<EqualRow>();
}

/// <summary>A row whose equality compares its <c>Id</c>, so two rows can be equal and still be two rows.</summary>
public sealed record EqualRow(int Id);

public sealed class RowMatchValidator : DraftSubmitValidator<RowMatchModel>
{
    protected override void ConfigureDraftRules()
    {
    }

    protected override void ConfigureSubmitRules()
    {
    }
}
