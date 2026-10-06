using System.Linq.Expressions;
using Bunit;
using Formidable.Blazor.Tests.Fixtures;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;

namespace Formidable.Blazor.Tests;

public class RowKeyVerificationTests : BunitContext
{
    /// <summary>
    /// The kit components this theory drives as a collection row. Each resolves a field from its
    /// own accessor, and the check compares that resolved identifier against the one the
    /// component registered; each is also a row a collection can be built out of. Any other
    /// component that resolves a field is covered on identical terms without appearing here,
    /// because the comparison lives on the shared base rather than in any component.
    /// </summary>
    public enum RowComponent
    {
        Input,
        Field,
        Message,
        Anchor,
    }

    public RowKeyVerificationTests()
    {
        Services.AddFormidable();
        Services.AddSingleton<FluentValidation.IValidator<EngineOrder>, EngineOrderValidator>();
    }

    [Theory]
    [InlineData(RowComponent.Input)]
    [InlineData(RowComponent.Field)]
    [InlineData(RowComponent.Message)]
    [InlineData(RowComponent.Anchor)]
    public void Removing_a_row_from_an_unkeyed_list_names_the_field_and_the_fix(RowComponent component)
    {
        var order = Rows("a", "b", "c");
        var cut = RenderRows(order, keyed: false, Verifying(), component);

        order.Items.RemoveAt(0);

        var thrown = Assert.Throws<InvalidOperationException>(() => ReRender(cut, order));
        Assert.Contains(nameof(EngineItem.Sku), thrown.Message, StringComparison.Ordinal);
        Assert.Contains("@key", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_thrown_message_leads_with_the_general_cause_before_the_row_list_case()
    {
        var order = Rows("a", "b", "c");
        var cut = RenderRows(order, keyed: false, Verifying());

        order.Items.RemoveAt(0);

        var thrown = Assert.Throws<InvalidOperationException>(() => ReRender(cut, order));

        var generalCause = thrown.Message.IndexOf(
            "A field is the object owning the value plus a member name", StringComparison.Ordinal);
        var rowListCase = thrown.Message.IndexOf(
            "A row list rendered without @key", StringComparison.Ordinal);

        Assert.True(generalCause >= 0);
        Assert.True(rowListCase >= 0);
        Assert.True(generalCause < rowListCase);
    }

    [Fact]
    public void Replacing_a_keyed_row_in_place_is_not_a_missing_key()
    {
        var order = Rows("a", "b", "c");
        var cut = RenderRows(order, keyed: true, Verifying());

        order.Items[1] = new EngineItem { Sku = "b2" };
        ReRender(cut, order);

        Assert.Equal(3, cut.FindComponents<FormidableInputText>().Count);
    }

    [Fact]
    public void Removing_a_keyed_row_is_not_a_missing_key()
    {
        var order = Rows("a", "b", "c");
        var cut = RenderRows(order, keyed: true, Verifying());

        order.Items.RemoveAt(0);
        ReRender(cut, order);

        Assert.Equal(2, cut.FindComponents<FormidableInputText>().Count);
    }

    [Fact]
    public void Reordering_keyed_rows_keeps_their_components_and_their_fields()
    {
        var order = Rows("a", "b", "c");
        var cut = RenderRows(order, keyed: true, Verifying());
        var before = cut.FindComponents<FormidableInputText>().Select(row => row.Instance).ToList();

        (order.Items[0], order.Items[1]) = (order.Items[1], order.Items[0]);
        ReRender(cut, order);

        // Nothing was disposed and rebuilt: a keyed reorder permutes the components it already has,
        // which is why the check has to pass on the accessors still resolving to the same rows
        // rather than on a teardown that never happens here.
        var after = cut.FindComponents<FormidableInputText>().Select(row => row.Instance).ToList();
        Assert.Equal(before.Count, after.Count);
        Assert.All(after, row => Assert.Contains(row, before));
    }

    [Fact]
    public void Swapping_the_form_model_is_not_a_missing_key()
    {
        var cut = RenderRows(Rows("a", "b"), keyed: true, Verifying());

        ReRender(cut, Rows("x"));

        Assert.Single(cut.FindComponents<FormidableInputText>());
    }

    [Fact]
    public void Verification_is_off_by_default()
    {
        var order = Rows("a", "b", "c");
        var cut = RenderRows(order, keyed: false, new FormidableOptions());

        order.Items.RemoveAt(0);
        ReRender(cut, order);

        Assert.Equal(2, cut.FindComponents<FormidableInputText>().Count);
    }

    // An array grows only by replacement, and an unkeyed loop hands each row's components the
    // accessor of the row now at their position, so a row registered under the old list is bound
    // to the new one. The check catches it and names the fix for a row bound by its index: the
    // element around the loop keyed by the list. Mutation that must break it: give every
    // divergence the object-row advice (DescribeIndexedRowFix answers null), and the message
    // sends the reader to key each row by its row object.
    [Theory]
    [InlineData(ScalarRowComponent.Input, true)]
    [InlineData(ScalarRowComponent.Field, true)]
    [InlineData(ScalarRowComponent.Message, true)]
    [InlineData(ScalarRowComponent.Anchor, true)]
    [InlineData(ScalarRowComponent.Input, false)]
    public void Replacing_a_list_of_index_bound_rows_names_the_key_on_the_loop(ScalarRowComponent component, bool array)
    {
        var model = new ScalarTags { Array = ["a", "b"], List = ["a", "b"] };
        var cut = RenderScalarRows(model, array, ScalarRowKey.None, component);

        if (array)
        {
            model.Array = [.. model.Array, "c"];
        }
        else
        {
            model.List = [.. model.List, "c"];
        }

        var thrown = Assert.Throws<InvalidOperationException>(() => ReRender(cut, model));
        Assert.Contains("same field on a different", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("Key the element around the loop by the list", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("@key=\"Model.Tags\"", thrown.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("row object", thrown.Message, StringComparison.Ordinal);
        Assert.Contains($"{nameof(FormidableOptions)}.{nameof(FormidableOptions.VerifyRowKeys)}", thrown.Message, StringComparison.Ordinal);
    }

    // A row bound by its index and keyed by its value moves with the value when a row before it
    // is removed, so its components are handed the next index along. The check names the fix for
    // that shape, which is no key of the row's own. Mutation that must break it: give every
    // divergence the object-row advice (DescribeIndexedRowFix answers null), and the message
    // tells the reader to key each row by its row object, the key that caused it.
    [Fact]
    public void A_row_bound_by_its_index_and_keyed_by_its_value_is_told_to_drop_the_key()
    {
        var model = new ScalarTags { List = ["a", "b", "c"] };
        var cut = RenderScalarRows(model, array: false, ScalarRowKey.Value);

        model.List.RemoveAt(0);

        var thrown = Assert.Throws<InvalidOperationException>(() => ReRender(cut, model));
        Assert.Contains("now names another index", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("Give such a row no key of its own", thrown.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("row object", thrown.Message, StringComparison.Ordinal);
    }

    // The fix the message names. With the element around the loop keyed by the list, replacing
    // the list rebuilds every row, so no component is handed an accessor for another list. A pin:
    // a loop keyed by its list never trips the check when the page replaces the list. Mutation
    // that must break it: drop the key from the host (ScalarRowKey.None), and the replacement
    // throws.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_loop_keyed_by_its_list_never_trips_the_check_when_the_list_is_replaced(bool array)
    {
        var model = new ScalarTags { Array = ["a", "b"], List = ["a", "b"] };
        var cut = RenderScalarRows(model, array, ScalarRowKey.Loop);

        if (array)
        {
            model.Array = [.. model.Array, "c"];
        }
        else
        {
            model.List = [.. model.List, "c"];
        }

        ReRender(cut, model);

        Assert.Equal(3, cut.FindComponents<FormidableInputText>().Count);
    }

    // A list edited in place keeps its instance, and an unkeyed row keeps its index, so a row's
    // components go on naming the field they registered. A pin: the check stays quiet for an add
    // and a remove, and each row shows the value at its index. Mutation that must break it:
    // replace the list in the edit instead of editing it in place, and the re-render throws.
    [Fact]
    public void An_unkeyed_list_of_index_bound_rows_edited_in_place_never_trips_the_check()
    {
        var model = new ScalarTags { List = ["a", "b", "c"] };
        var cut = RenderScalarRows(model, array: false, ScalarRowKey.None);

        model.List.RemoveAt(0);
        model.List.Add("d");
        ReRender(cut, model);

        Assert.Equal(["b", "c", "d"], cut.FindAll("input").Select(input => input.GetAttribute("value")));
    }

    private static FormidableOptions Verifying() => new() { VerifyRowKeys = true };

    private static void ReRender(IRenderedComponent<ScalarRowsHost> cut, ScalarTags model) =>
        cut.Render(parameters => parameters.Add(p => p.Model, model));

    private IRenderedComponent<ScalarRowsHost> RenderScalarRows(
        ScalarTags model,
        bool array,
        ScalarRowKey key,
        ScalarRowComponent component = ScalarRowComponent.Input) =>
        Render<ScalarRowsHost>(parameters => parameters
            .Add(p => p.Model, model)
            .Add(p => p.UseArray, array)
            .Add(p => p.Key, key)
            .Add(p => p.Component, component)
            .Add(p => p.Options, Verifying()));

    private static EngineOrder Rows(params string[] skus) =>
        new() { Items = [.. skus.Select(sku => new EngineItem { Sku = sku })] };

    /// <summary>
    /// Re-renders the host the way a page re-renders after one of its own buttons edited the list:
    /// same model instance, same key strategy, whatever the list now holds. Parameters the call
    /// does not supply keep the values they already have.
    /// </summary>
    private static void ReRender(IRenderedComponent<RowsHost> cut, EngineOrder order) =>
        cut.Render(parameters => parameters.Add(p => p.Order, order));

    private IRenderedComponent<RowsHost> RenderRows(
        EngineOrder order,
        bool keyed,
        FormidableOptions options,
        RowComponent component = RowComponent.Input) =>
        Render<RowsHost>(parameters => parameters
            .Add(p => p.Order, order)
            .Add(p => p.Keyed, keyed)
            .Add(p => p.Options, options)
            .Add(p => p.Component, component));

    /// <summary>
    /// Renders one row per item of <see cref="Order"/> — as whichever field-bound component
    /// <see cref="Component"/> names — keyed by the row object or not keyed at all, the two markup
    /// shapes the check exists to tell apart.
    /// </summary>
    private sealed class RowsHost : ComponentBase
    {
        [Parameter]
        public EngineOrder Order { get; set; } = default!;

        [Parameter]
        public bool Keyed { get; set; }

        [Parameter]
        public FormidableOptions? Options { get; set; }

        [Parameter]
        public RowComponent Component { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<FormidableForm<EngineOrder>>(0);
            builder.AddComponentParameter(1, nameof(FormidableForm<EngineOrder>.Model), Order);
            builder.AddComponentParameter(2, nameof(FormidableForm<EngineOrder>.Options), Options);
            builder.AddComponentParameter(3, nameof(FormidableForm<EngineOrder>.ChildContent), (RenderFragment<FormidableFormContext>)(_ => inner =>
            {
                foreach (var item in Order.Items)
                {
                    BuildRow(inner, item);
                    inner.CloseComponent();
                }
            }));
            builder.CloseComponent();
        }

        private void BuildRow(RenderTreeBuilder builder, EngineItem item)
        {
            switch (Component)
            {
                case RowComponent.Field:
                    builder.OpenComponent<FormidableField<string>>(10);
                    Key(builder, item);
                    builder.AddComponentParameter(11, nameof(FormidableField<string>.For), (Expression<Func<string>>)(() => item.Sku));
                    builder.AddComponentParameter(12, nameof(FormidableField<string>.ChildContent), (RenderFragment<FormidableFieldContext>)(_ => _ => { }));
                    return;

                case RowComponent.Message:
                    builder.OpenComponent<FormidableFieldMessage<string>>(20);
                    Key(builder, item);
                    builder.AddComponentParameter(21, nameof(FormidableFieldMessage<string>.For), (Expression<Func<string>>)(() => item.Sku));
                    return;

                case RowComponent.Anchor:
                    builder.OpenComponent<FormidableFieldAnchor<string>>(30);
                    Key(builder, item);
                    builder.AddComponentParameter(31, nameof(FormidableFieldAnchor<string>.For), (Expression<Func<string>>)(() => item.Sku));
                    return;

                default:
                    builder.OpenComponent<FormidableInputText>(40);
                    Key(builder, item);
                    builder.AddComponentParameter(41, nameof(FormidableInputText.Value), item.Sku);
                    builder.AddComponentParameter(42, nameof(FormidableInputText.ValueExpression), (Expression<Func<string?>>)(() => item.Sku));
                    return;
            }
        }

        private void Key(RenderTreeBuilder builder, EngineItem item)
        {
            if (Keyed)
            {
                builder.SetKey(item);
            }
        }
    }
}
