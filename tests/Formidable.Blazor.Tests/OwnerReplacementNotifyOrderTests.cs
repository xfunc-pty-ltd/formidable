using System.Linq.Expressions;
using Bunit;
using Formidable.Blazor.Tests.Fixtures;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;

namespace Formidable.Blazor.Tests;

/// <summary>
/// Pins the ordering rule for a page-driven owner replacement: render before notifying any field
/// under the replaced instance. The engine publishes state synchronously inside
/// <c>EditContext.NotifyFieldChanged</c> — a fresh owner is a fresh <see cref="FieldIdentifier"/>,
/// so the touched flip publishes unconditionally, and on the default cadence the live pass it
/// starts publishes in the same window — and each publish re-renders the components observing the
/// engine. A re-rendering wrapper (here a <see cref="FormidableField{TValue}"/>) re-supplies the
/// bound components inside it, whose accessors then resolve the replacement while their
/// registrations still name the old instance: with <see cref="FormidableOptions.VerifyRowKeys"/>
/// on, that is the row-key exception raised in the middle of the page's own notify. Rendering
/// first lets the keyed diff retire the old components and register the replacement, so the same
/// notify meets accessors and registrations that already agree.
/// </summary>
public class OwnerReplacementNotifyOrderTests : BunitContext
{
    public OwnerReplacementNotifyOrderTests()
    {
        Services.AddFormidable();
        Services.AddSingleton<FluentValidation.IValidator<EngineOrder>, DeclarationOrderValidator>();
    }

    [Fact]
    public async Task Notifying_before_rendering_a_replaced_owner_throws_the_row_key_exception_inside_the_notify()
    {
        var order = new EngineOrder { Customer = new EngineCustomer { Name = "original" } };
        var cut = Render<NestedOwnerHost>(parameters => parameters
            .Add(p => p.Order, order)
            .Add(p => p.Options, new FormidableOptions { VerifyRowKeys = true }));
        var engine = cut.FindComponent<FormidableForm<EngineOrder>>().Instance.Engine!;

        // The exception cannot surface on the notify frames. The publish reaches each observing
        // component as an ordinary event, the handler re-renders through InvokeAsync (whose task
        // it discards), and the renderer catches a synchronous throw from its own render batch
        // rather than letting it propagate to whoever queued the render — so NotifyFieldChanged
        // returns normally and the throw completes Renderer.UnhandledException, bUnit's view of
        // the renderer's unhandled-exception path. The flag read after the notify pins that it
        // surfaced there before the consumer's own call returned: the mid-notify timing is the
        // whole hazard the ordering rule exists for.
        var surfacedInsideTheNotify = false;
        Exception? thrownFromNotify = null;
        await cut.InvokeAsync(() =>
        {
            order.Customer = new EngineCustomer();
            try
            {
                engine.EditContext.NotifyFieldChanged(
                    FieldIdentifier.Create(() => order.Customer!.Name));
            }
            catch (Exception exception)
            {
                thrownFromNotify = exception;
            }

            surfacedInsideTheNotify = Renderer.UnhandledException.IsCompleted;
        });

        Assert.Null(thrownFromNotify);
        Assert.True(surfacedInsideTheNotify);

        var thrown = await Renderer.UnhandledException.WaitAsync(TimeSpan.FromSeconds(5));
        var invalid = Assert.IsType<InvalidOperationException>(thrown);
        Assert.Contains(
            $"{nameof(FormidableOptions)}.{nameof(FormidableOptions.VerifyRowKeys)}",
            invalid.Message,
            StringComparison.Ordinal);
        Assert.Contains("same field on a different EngineCustomer", invalid.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rendering_before_notifying_rebuilds_the_bound_components_and_the_new_owner_discloses()
    {
        var order = new EngineOrder { Customer = new EngineCustomer { Name = "original" } };
        var cut = Render<NestedOwnerHost>(parameters => parameters
            .Add(p => p.Order, order)
            .Add(p => p.Options, new FormidableOptions { VerifyRowKeys = true }));
        var engine = cut.FindComponent<FormidableForm<EngineOrder>>().Instance.Engine!;

        // The documented safe order: replace, render (the keyed diff retires the old components
        // and the fresh ones resolve the replacement as they bind), and only then notify.
        order.Customer = new EngineCustomer();
        cut.Render(parameters => parameters.Add(p => p.Order, order));
        await cut.InvokeAsync(() =>
            engine.EditContext.NotifyFieldChanged(FieldIdentifier.Create(() => order.Customer!.Name)));

        Assert.False(Renderer.UnhandledException.IsCompleted);
        cut.WaitForAssertion(() =>
            Assert.Contains("Customer name is required", cut.Markup, StringComparison.Ordinal));
    }

    /// <summary>
    /// The markup shape the collections page teaches, reduced to one nested owner: a
    /// <see cref="FormidableField{TValue}"/> keyed by the owner instance, wrapping a
    /// <see cref="FormidableFieldMessage{TValue}"/>, both with accessors that navigate to the
    /// owner through the stable model (<c>Order.Customer!.Name</c>) rather than closing over the
    /// instance itself — which is what lets a replacement re-point them without any rebuild.
    /// </summary>
    private sealed class NestedOwnerHost : ComponentBase
    {
        [Parameter]
        public EngineOrder Order { get; set; } = default!;

        [Parameter]
        public FormidableOptions? Options { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<FormidableForm<EngineOrder>>(0);
            builder.AddComponentParameter(1, nameof(FormidableForm<EngineOrder>.Model), Order);
            builder.AddComponentParameter(2, nameof(FormidableForm<EngineOrder>.Options), Options);
            builder.AddComponentParameter(3, nameof(FormidableForm<EngineOrder>.ChildContent), (RenderFragment<FormidableFormContext>)(_ => inner =>
            {
                inner.OpenComponent<FormidableField<string>>(10);
                inner.SetKey(Order.Customer);
                inner.AddComponentParameter(11, nameof(FormidableField<string>.For), (Expression<Func<string>>)(() => Order.Customer!.Name));
                inner.AddComponentParameter(12, nameof(FormidableField<string>.ChildContent), (RenderFragment<FormidableFieldContext>)(_ => content =>
                {
                    content.OpenComponent<FormidableFieldMessage<string>>(0);
                    content.AddComponentParameter(1, nameof(FormidableFieldMessage<string>.For), (Expression<Func<string>>)(() => Order.Customer!.Name));
                    content.CloseComponent();
                }));
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        }
    }
}
