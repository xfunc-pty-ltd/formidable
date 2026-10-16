using System.Linq.Expressions;
using Bunit;
using Formidable.Blazor.Tests.Fixtures;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;

namespace Formidable.Blazor.Tests;

/// <summary>
/// The row-key check compares the field a component registered with the field its accessor names
/// now. A page can replace an owner object from an <c>EditContext.OnFieldChanged</c> handler: the
/// engine publishes inside that same notify, ahead of the handler, so the re-renders the publish
/// queues run before the page's own render applies the new keys. Each old wrapper then re-sets the
/// components inside it, whose accessors already reach the new owner. The keys retire those
/// components in the same batch, so the divergence is one the batch repairs, not a misfiled row.
/// The check must judge a divergence once the batch has settled, which a component the batch
/// retired never reaches.
/// </summary>
public class RowKeyCheckAfterBatchTests : BunitContext
{
    public RowKeyCheckAfterBatchTests()
    {
        Services.AddFormidable();
        Services.AddSingleton<FluentValidation.IValidator<EngineOrder>, DeclarationOrderValidator>();
    }

    // The description input stands in for a radio: committing a value notifies its field, the
    // engine publishes inside that notify, and the host's handler on the same event replaces the
    // customer and renders. Mutation that must break this: throw at the parameter set, before the
    // batch has run the host's render.
    [Fact]
    public async Task Replacing_the_owner_from_a_field_changed_handler_is_not_a_misfiled_row_when_the_keys_rebuild_it()
    {
        var order = new EngineOrder { Customer = new EngineCustomer { Name = "original" } };
        var original = order.Customer;
        var cut = Render<OwnerReplacingHost>(parameters => parameters
            .Add(p => p.Order, order)
            .Add(p => p.Options, new FormidableOptions { VerifyRowKeys = true }));
        var engine = cut.FindComponent<FormidableForm<EngineOrder>>().Instance.Engine!;

        await cut.InvokeAsync(() => cut.Find("input[id$='-description']").Change("x"));

        Assert.False(Renderer.UnhandledException.IsCompleted);
        Assert.NotSame(original, order.Customer);
        cut.WaitForAssertion(() =>
        {
            Assert.True(engine.Registry.IsRegistered(new FieldIdentifier(order.Customer!, nameof(EngineCustomer.Name))));
            Assert.False(engine.Registry.IsRegistered(new FieldIdentifier(original, nameof(EngineCustomer.Name))));
        });
        Assert.False(Renderer.UnhandledException.IsCompleted);
    }

    // The reporting mode judges the same way: a divergence the batch repairs reaches no report.
    [Fact]
    public async Task Replacing_the_owner_from_a_field_changed_handler_reports_nothing_when_the_keys_rebuild_it()
    {
        var reports = new List<StaleRegistrationReport>();
        var order = new EngineOrder { Customer = new EngineCustomer { Name = "original" } };
        var cut = Render<OwnerReplacingHost>(parameters => parameters
            .Add(p => p.Order, order)
            .Add(p => p.Options, new FormidableOptions
            {
                ReportStaleRegistrations = true,
                StaleRegistrationDiagnostic = reports.Add,
            }));
        var engine = cut.FindComponent<FormidableForm<EngineOrder>>().Instance.Engine!;

        await cut.InvokeAsync(() => cut.Find("input[id$='-description']").Change("x"));

        cut.WaitForAssertion(() =>
            Assert.True(engine.Registry.IsRegistered(new FieldIdentifier(order.Customer!, nameof(EngineCustomer.Name)))));
        Assert.Empty(reports);
        Assert.False(Renderer.UnhandledException.IsCompleted);
    }

    // The same replacement with no key is the misfiled row the check exists for: the wrapper
    // survives the batch bound to the new owner while registered for the old one, and the check
    // throws as before. Mutation that must break this: drop the check from the settled batch.
    [Fact]
    public async Task Replacing_the_owner_from_a_field_changed_handler_still_throws_when_nothing_rebuilds_the_row()
    {
        var order = new EngineOrder { Customer = new EngineCustomer { Name = "original" } };
        var cut = Render<OwnerReplacingHost>(parameters => parameters
            .Add(p => p.Order, order)
            .Add(p => p.Keyed, false)
            .Add(p => p.Options, new FormidableOptions { VerifyRowKeys = true }));

        // The renderer hands the throw to its unhandled path, which bUnit rethrows from the event
        // dispatch that provoked it.
        var invalid = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cut.InvokeAsync(() => cut.Find("input[id$='-description']").Change("x")));

        Assert.Contains("same field on a different EngineCustomer", invalid.Message, StringComparison.Ordinal);
        Assert.Contains(
            $"{nameof(FormidableOptions)}.{nameof(FormidableOptions.VerifyRowKeys)}",
            invalid.Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A page in the shape that trips the check: a kit input whose change the page listens for on
    /// the engine's own <c>EditContext</c>, and a wrapper keyed by the customer the handler replaces.
    /// The subscription is taken from inside the form's fragment, after the engine's own, as a page
    /// that reads the context's <c>EditContext</c> takes it.
    /// </summary>
    private sealed class OwnerReplacingHost : ComponentBase
    {
        private EditContext? _subscribed;

        [Parameter]
        public EngineOrder Order { get; set; } = default!;

        [Parameter]
        public FormidableOptions? Options { get; set; }

        [Parameter]
        public bool Keyed { get; set; } = true;

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<FormidableForm<EngineOrder>>(0);
            builder.AddComponentParameter(1, nameof(FormidableForm<EngineOrder>.Model), Order);
            builder.AddComponentParameter(2, nameof(FormidableForm<EngineOrder>.Options), Options);
            builder.AddComponentParameter(3, nameof(FormidableForm<EngineOrder>.ChildContent), (RenderFragment<FormidableFormContext>)(form => inner =>
            {
                Subscribe(form.EditContext);

                inner.OpenComponent<FormidableInputText>(0);
                inner.AddComponentParameter(1, nameof(FormidableInputText.For), (Expression<Func<string?>>)(() => Order.Description));
                inner.AddComponentParameter(2, "Value", Order.Description);
                inner.AddComponentParameter(
                    3, "ValueChanged", EventCallback.Factory.Create<string?>(this, v => Order.Description = v ?? string.Empty));
                inner.CloseComponent();

                inner.OpenComponent<KeyedWrapper>(4);
                if (Keyed)
                {
                    inner.SetKey(Order.Customer);
                }

                inner.AddComponentParameter(5, nameof(KeyedWrapper.For), (Expression<Func<string>>)(() => Order.Customer!.Name));
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        }

        private void Subscribe(EditContext editContext)
        {
            if (ReferenceEquals(editContext, _subscribed))
            {
                return;
            }

            if (_subscribed is not null)
            {
                _subscribed.OnFieldChanged -= HandleFieldChanged;
            }

            _subscribed = editContext;
            editContext.OnFieldChanged += HandleFieldChanged;
        }

        private void HandleFieldChanged(object? sender, FieldChangedEventArgs e)
        {
            if (e.FieldIdentifier.FieldName != nameof(EngineOrder.Description))
            {
                return;
            }

            Order.Customer = new EngineCustomer();
            StateHasChanged();
        }
    }

    /// <summary>
    /// A consumer's field primitive: a <see cref="FormidableField{TValue}"/> around a message slot,
    /// every <c>For</c> the same expression instance, as a wrapper that forwards its own parameter
    /// passes it. The wrapper subscribes to nothing; the <see cref="FormidableField{TValue}"/>
    /// re-renders this markup on every publish.
    /// </summary>
    private sealed class KeyedWrapper : ComponentBase
    {
        [Parameter]
        public Expression<Func<string>> For { get; set; } = default!;

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<FormidableField<string>>(0);
            builder.AddComponentParameter(1, nameof(FormidableField<string>.For), For);
            builder.AddComponentParameter(2, nameof(FormidableField<string>.ChildContent), (RenderFragment<FormidableFieldContext>)(_ => content =>
            {
                content.OpenComponent<MessageSlot>(0);
                content.AddComponentParameter(1, nameof(MessageSlot.For), For);
                content.CloseComponent();
            }));
            builder.CloseComponent();
        }
    }

    /// <summary>Layout around a <see cref="FormidableFieldMessage{TValue}"/>, forwarding its accessor unchanged.</summary>
    private sealed class MessageSlot : ComponentBase
    {
        [Parameter]
        public Expression<Func<string>> For { get; set; } = default!;

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<FormidableFieldMessage<string>>(0);
            builder.AddComponentParameter(1, nameof(FormidableFieldMessage<string>.For), For);
            builder.CloseComponent();
        }
    }
}
