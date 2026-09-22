using System.Linq.Expressions;
using Bunit;
using Formidable.Blazor.Tests.Fixtures;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;

namespace Formidable.Blazor.Tests;

/// <summary>
/// A <c>For</c> written as <c>Expression&lt;Func&lt;object&gt;&gt;</c>: the shape a shared
/// component takes when one parameter has to name a field of any type.
/// <para>
/// The compiler builds two different trees for it. Over a reference-typed member the body is the
/// member access itself, unwrapped, because a lambda body is allowed to be reference-assignable
/// to the delegate's return type; over a value-typed member the body is a boxing
/// <c>Convert</c> node, because that conversion has to be represented. Only the second shape can
/// tell a resolution that unwraps converts from one that does not, so the value-type test is the
/// discriminating one and the reference-type test holds by construction.
/// </para>
/// <para>
/// Every test here renders the object-typed accessor beside the typed one over the SAME member
/// and compares the two, so a pass means the field resolved identically rather than merely that
/// something rendered. The mutation that must break the file: resolving <c>For</c> by reading
/// its body as a <c>MemberExpression</c> directly instead of through
/// <c>FieldIdentifier.Create</c>, which is what makes a convert node fatal.
/// </para>
/// </summary>
public class ObjectTypedAccessorTests : BunitContext
{
    public ObjectTypedAccessorTests()
    {
        Services.AddFormidable();
        Services.AddSingleton<FluentValidation.IValidator<EngineOrder>, EngineOrderValidator>();
    }

    // Description is a string, so both accessors carry the same bare member access and naming one
    // field twice is structural rather than a resolution the engine has to get right. Kept as the
    // reference-type half of the pair, and as the assert that fails if the two ever diverge.
    [Fact]
    public void An_object_typed_accessor_resolves_the_field_its_typed_twin_resolves()
    {
        var order = new EngineOrder();
        FormidableFieldContext? boxed = null;
        FormidableFieldContext? typed = null;

        RenderForm(order, inner =>
        {
            Field<object>(inner, 0, () => order.Description, ctx => boxed = ctx);
            Field<string>(inner, 10, () => order.Description, ctx => typed = ctx);
        });

        Assert.NotNull(boxed);
        Assert.NotNull(typed);
        Assert.Equal(typed!.Field, boxed!.Field);
        Assert.Equal(typed.ElementId, boxed.ElementId);
        Assert.Equal(new FieldIdentifier(order, nameof(EngineOrder.Description)), boxed.Field);
    }

    // Location is a struct, so the convert node boxes rather than merely widening a reference —
    // the case a resolution that special-cased reference conversions alone would still miss.
    [Fact]
    public void An_object_typed_accessor_resolves_a_value_type_member_through_its_boxing_convert()
    {
        var order = new EngineOrder();
        FormidableFieldContext? boxed = null;
        FormidableFieldContext? typed = null;

        RenderForm(order, inner =>
        {
            Field<object>(inner, 0, () => order.Location, ctx => boxed = ctx);
            Field<EnginePoint>(inner, 10, () => order.Location, ctx => typed = ctx);
        });

        Assert.NotNull(boxed);
        Assert.NotNull(typed);
        Assert.Equal(typed!.Field, boxed!.Field);
        Assert.Equal(new FieldIdentifier(order, nameof(EngineOrder.Location)), boxed.Field);
    }

    // The four components taking a For that the tests above do not render, each reached with
    // TValue = object: the anchor and the collection message register their fields, the field
    // message carries its issues, and the indicator marks it. With FormidableField above, that is
    // all five components whose TValue comes from For alone. This is the breadth half — the shape
    // reaches each of them and lands on the right field — where the value-type test above is what
    // discriminates the resolution.
    [Fact]
    public void An_object_typed_accessor_carries_registration_messages_and_the_required_marker()
    {
        var order = new EngineOrder();
        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<EngineOrder>>(0);
            builder.AddComponentParameter(1, "Model", order);
            builder.AddComponentParameter(2, "ChildContent", (RenderFragment<FormidableFormContext>)(_ => inner =>
            {
                inner.OpenComponent<FormidableFieldAnchor<object>>(0);
                inner.AddComponentParameter(1, "For", (Expression<Func<object>>)(() => order.Description));
                inner.CloseComponent();

                inner.OpenComponent<FormidableFieldMessage<object>>(10);
                inner.AddComponentParameter(11, "For", (Expression<Func<object>>)(() => order.Description));
                inner.CloseComponent();

                inner.OpenComponent<FormidableRequiredIndicator<object>>(20);
                inner.AddComponentParameter(21, "For", (Expression<Func<object>>)(() => order.Description));
                inner.CloseComponent();

                inner.OpenComponent<FormidableCollectionMessage<object>>(30);
                inner.AddComponentParameter(31, "For", (Expression<Func<object>>)(() => order.Items));
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        });

        var form = cut.FindComponent<FormidableForm<EngineOrder>>();
        var registry = form.Instance.Engine!.Registry;
        Assert.True(registry.IsRegistered(new FieldIdentifier(order, nameof(EngineOrder.Description))));
        Assert.True(registry.IsRegistered(new FieldIdentifier(order, nameof(EngineOrder.Items))));
        Assert.Equal("*", cut.Find("span.formidable-required").TextContent);

        cut.InvokeAsync(() => form.Instance.SubmitAsync());

        cut.WaitForAssertion(() =>
            Assert.Contains("Order description", cut.Find("ul.formidable-message-list").TextContent));
    }

    private void RenderForm(EngineOrder order, RenderFragment content) =>
        Render(builder =>
        {
            builder.OpenComponent<FormidableForm<EngineOrder>>(0);
            builder.AddComponentParameter(1, "Model", order);
            builder.AddComponentParameter(
                2,
                "ChildContent",
                (RenderFragment<FormidableFormContext>)(_ => content));
            builder.CloseComponent();
        });

    private static void Field<TValue>(
        RenderTreeBuilder builder,
        int sequence,
        Expression<Func<TValue>> accessor,
        Action<FormidableFieldContext> capture)
    {
        builder.OpenComponent<FormidableField<TValue>>(sequence);
        builder.AddComponentParameter(sequence + 1, "For", accessor);
        builder.AddComponentParameter(
            sequence + 2,
            "ChildContent",
            (RenderFragment<FormidableFieldContext>)(ctx => _ => capture(ctx)));
        builder.CloseComponent();
    }
}
