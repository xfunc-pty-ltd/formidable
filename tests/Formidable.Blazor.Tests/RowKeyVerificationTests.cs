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

    private static FormidableOptions Verifying() => new() { VerifyRowKeys = true };

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
