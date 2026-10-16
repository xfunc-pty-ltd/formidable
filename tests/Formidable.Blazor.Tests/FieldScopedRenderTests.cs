using System.Linq.Expressions;
using Bunit;
using FluentValidation;
using Formidable.Blazor.Tests.Fixtures;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;

namespace Formidable.Blazor.Tests;

/// <summary>
/// A kit component re-renders for a publish only when the publish names its field. The wrapper's
/// ChildContent therefore refreshes on its own field's changes, on the form's renders and on the
/// page's, not on every check elsewhere; the form's own renders at a submit's start and end still
/// reach it. A message for a field nothing registers still re-renders when its issue moves,
/// which is what proves the engine's key set complete.
/// </summary>
public class FieldScopedRenderTests : BunitContext
{
    public FieldScopedRenderTests()
    {
        Services.AddSingleton<IFormidableFocusService>(new RecordingFocusService());
        Services.AddSingleton<IFormidableFieldOrderService>(new RecordingFieldOrderService());
        Services.AddSingleton<IFormidableDomValueSync>(new RecordingDomValueSync());
        Services.AddSingleton<IValidator<EngineOrder>, DeclarationOrderValidator>();
        Services.AddFormidableBlazor();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    // A publish that names only the other field must leave this wrapper, and so the consumer's
    // control inside it, unrendered, under either root. The check this edit starts is the form's
    // first, which turns every passing field green at once; Description fails empty under this
    // validator, so that check leaves its view where it was.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_wrapper_does_not_re_render_when_another_field_moves(bool attach)
    {
        var order = new EngineOrder { Customer = new EngineCustomer() };
        var cut = Render<TwoWrapperHost>(p => p.Add(x => x.Order, order).Add(x => x.Attach, attach));
        var descriptionChild = cut.FindComponents<ParameterSetCounter>().Single(c => c.Instance.Name == "description").Instance;
        var nameChild = cut.FindComponents<ParameterSetCounter>().Single(c => c.Instance.Name == "name").Instance;
        var engine = TwoWrapperHost.EngineOf(cut, attach);

        var descriptionBefore = descriptionChild.ParameterSets;
        var nameBefore = nameChild.ParameterSets;
        await cut.InvokeAsync(() => cut.Find("input[id$='-name']").Change("Ada"));
        cut.WaitForState(() => !engine.IsValidating, TimeSpan.FromSeconds(5));
        await cut.InvokeAsync(() => { });

        Assert.Equal(descriptionBefore, descriptionChild.ParameterSets);
        Assert.True(nameChild.ParameterSets > nameBefore, "the edited field's wrapper did not re-render");

        await Services.DisposeAsync();
    }

    // Under a row-key check every publish re-renders every wrapper, so a component kept inside
    // one is re-parameterised after each notify, which is where the check re-reads the accessor;
    // the defaults host above is pinned not to.
    [Fact]
    public async Task Under_VerifyRowKeys_a_wrapper_re_renders_when_another_field_moves()
    {
        var order = new EngineOrder { Customer = new EngineCustomer() };
        var cut = Render<TwoWrapperHost>(p => p
            .Add(x => x.Order, order)
            .Add(x => x.Options, new FormidableOptions { RefreshDebounce = Timeout.InfiniteTimeSpan, VerifyRowKeys = true }));
        var descriptionChild = cut.FindComponents<ParameterSetCounter>().Single(c => c.Instance.Name == "description").Instance;
        var engine = cut.FindComponent<FormidableForm<EngineOrder>>().Instance.Engine!;

        var before = descriptionChild.ParameterSets;
        await cut.InvokeAsync(() => cut.Find("input[id$='-name']").Change("Ada"));
        cut.WaitForState(() => !engine.IsValidating, TimeSpan.FromSeconds(5));
        await cut.InvokeAsync(() => { });

        Assert.True(descriptionChild.ParameterSets > before, "the check's re-render did not reach the other field's wrapper");

        await Services.DisposeAsync();
    }

    [Fact]
    public async Task A_wrapper_re_renders_when_its_own_field_moves()
    {
        var order = new EngineOrder { Customer = new EngineCustomer() };
        var cut = Render<TwoWrapperHost>(p => p.Add(x => x.Order, order));
        var descriptionChild = cut.FindComponents<ParameterSetCounter>().Single(c => c.Instance.Name == "description").Instance;
        var engine = cut.FindComponent<FormidableForm<EngineOrder>>().Instance.Engine!;

        var before = descriptionChild.ParameterSets;
        await cut.InvokeAsync(() => cut.Find("input[id$='-description']").Change("x"));
        cut.WaitForState(() => !engine.IsValidating, TimeSpan.FromSeconds(5));

        Assert.True(descriptionChild.ParameterSets > before);

        await Services.DisposeAsync();
    }

    // A wrapper's fragment re-runs across a submit, through the form's own renders at its start
    // and end and through the submit's publishes, which name every field; the ceiling class tells
    // those two routes apart. What this pins is that an inline read of IsSubmitting does not go
    // stale across a submit: one of those re-runs reads it true. Mutation: hand the form's context
    // a flag that always reads false, as the validator root's does; no re-run then sees true.
    [Fact]
    public async Task An_inline_IsSubmitting_read_does_not_go_stale_across_a_submit()
    {
        var order = new EngineOrder { Customer = new EngineCustomer() };
        var cut = Render<TwoWrapperHost>(p => p.Add(x => x.Order, order));
        var form = cut.FindComponent<FormidableForm<EngineOrder>>().Instance;
        var descriptionChild = cut.FindComponents<ParameterSetCounter>().Single(c => c.Instance.Name == "description").Instance;

        var before = descriptionChild.ParameterSets;
        await cut.InvokeAsync(() => form.SubmitAsync());

        Assert.True(descriptionChild.ParameterSets >= before + 2, "the wrapper's fragment did not re-run across the submit");
        Assert.Contains(descriptionChild.Readings, reading => reading.EndsWith("/True", StringComparison.Ordinal));

        await Services.DisposeAsync();
    }

    // The engine's key set must reach a field nothing registers, or a message for it would never
    // re-render under the filter.
    [Fact]
    public async Task A_message_for_an_unregistered_field_still_renders_when_its_issue_moves()
    {
        var order = new EngineOrder { Customer = new EngineCustomer() };
        var cut = Render<MessageOnlyHost>(p => p.Add(x => x.Order, order));
        var engine = cut.FindComponent<FormidableForm<EngineOrder>>().Instance.Engine!;
        var customerName = new FieldIdentifier(order.Customer!, nameof(EngineCustomer.Name));
        Assert.False(engine.Registry.IsRegistered(customerName));

        await cut.InvokeAsync(() => engine.EditContext.NotifyFieldChanged(customerName));
        cut.WaitForAssertion(() => Assert.Contains("Customer name is required", cut.Markup, StringComparison.Ordinal));

        order.Customer!.Name = "Ada";
        await cut.InvokeAsync(() => engine.EditContext.NotifyFieldChanged(customerName));
        cut.WaitForAssertion(() => Assert.DoesNotContain("Customer name is required", cut.Markup, StringComparison.Ordinal));

        await Services.DisposeAsync();
    }

    /// <summary>Counts its parameter sets and keeps what the fragment read inline at each; its object parameter makes every render of the fragment above re-set it.</summary>
    private sealed class ParameterSetCounter : ComponentBase
    {
        [Parameter]
        public string Name { get; set; } = string.Empty;

        [Parameter]
        public object? Token { get; set; }

        /// <summary>What the fragment read inline as it rendered this time.</summary>
        [Parameter]
        public string Reading { get; set; } = string.Empty;

        public int ParameterSets { get; private set; }

        public List<string> Readings { get; } = [];

        protected override void OnParametersSet()
        {
            ParameterSets++;
            Readings.Add(Reading);
        }

        protected override void BuildRenderTree(RenderTreeBuilder builder) =>
            builder.AddMarkupContent(0, "<span></span>");
    }

    /// <summary>
    /// Two FormidableFields, each wrapping a kit input and a counting child, under a FormidableForm
    /// or, with Attach, under a FormidableValidator inside a consumer-owned EditForm. The
    /// description wrapper's fragment also reads the other field's state and the context's
    /// IsSubmitting inline, the two reads the contract says refresh only with the wrapper's own
    /// field, the form or the page. The inputs' ValueChanged goes to a sink, not to this page, so
    /// the page does not re-render per edit.
    /// </summary>
    private sealed class TwoWrapperHost : ComponentBase
    {
        private static readonly FormidableOptions Defaults = new() { RefreshDebounce = Timeout.InfiniteTimeSpan };
        private EditContext? _editContext;

        [Parameter]
        public EngineOrder Order { get; set; } = default!;

        /// <summary>The form's options; the default holds the post-submit re-check back, so a submit's publishes are the submit's own.</summary>
        [Parameter]
        public FormidableOptions Options { get; set; } = Defaults;

        /// <summary>Whether the wrappers sit under the validator root rather than the form root.</summary>
        [Parameter]
        public bool Attach { get; set; }

        public static IFormidableEngine EngineOf(IRenderedComponent<TwoWrapperHost> cut, bool attach) => attach
            ? cut.FindComponent<FormidableValidator<EngineOrder>>().Instance.Engine!
            : cut.FindComponent<FormidableForm<EngineOrder>>().Instance.Engine!;

        protected override void OnParametersSet()
        {
            if (_editContext is null || !ReferenceEquals(_editContext.Model, Order))
            {
                _editContext = new EditContext(Order);
            }
        }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            var sink = new Sink(Order);
            if (Attach)
            {
                builder.OpenComponent<EditForm>(0);
                builder.AddComponentParameter(1, nameof(EditForm.EditContext), _editContext);
                builder.AddComponentParameter(2, nameof(EditForm.ChildContent), (RenderFragment<EditContext>)(_ => inner =>
                {
                    inner.OpenComponent<FormidableValidator<EngineOrder>>(0);
                    inner.AddComponentParameter(1, nameof(FormidableValidator<EngineOrder>.Options), Options);
                    inner.AddComponentParameter(2, nameof(FormidableValidator<EngineOrder>.ChildContent), (RenderFragment<FormidableFormContext>)(form => fields => Fields(fields, form, sink)));
                    inner.CloseComponent();
                }));
                builder.CloseComponent();
                return;
            }

            builder.OpenComponent<FormidableForm<EngineOrder>>(0);
            builder.AddComponentParameter(1, nameof(FormidableForm<EngineOrder>.Model), Order);
            builder.AddComponentParameter(2, nameof(FormidableForm<EngineOrder>.Options), Options);
            builder.AddComponentParameter(3, nameof(FormidableForm<EngineOrder>.ChildContent), (RenderFragment<FormidableFormContext>)(form => inner => Fields(inner, form, sink)));
            builder.CloseComponent();
        }

        private void Fields(RenderTreeBuilder inner, FormidableFormContext form, Sink sink)
        {
            Wrapper(inner, 0, "description", () => Order.Description, sink.SetDescription, field =>
                $"{form.Engine.GetFieldState(new FieldIdentifier(Order.Customer!, nameof(EngineCustomer.Name))).IsTouched}/{form.IsSubmitting}");
            Wrapper(inner, 10, "name", () => Order.Customer!.Name, sink.SetName, _ => string.Empty);
        }

        private static void Wrapper(
            RenderTreeBuilder b,
            int seq,
            string name,
            Expression<Func<string>> accessor,
            Action<string?> setter,
            Func<FormidableFieldContext, string> inlineRead)
        {
            b.OpenComponent<FormidableField<string>>(seq);
            b.AddComponentParameter(seq + 1, nameof(FormidableField<string>.For), accessor);
            b.AddComponentParameter(seq + 2, nameof(FormidableField<string>.ChildContent), (RenderFragment<FormidableFieldContext>)(field => c =>
            {
                var reading = inlineRead(field);
                c.OpenComponent<ParameterSetCounter>(0);
                c.AddComponentParameter(1, nameof(ParameterSetCounter.Name), name);
                c.AddComponentParameter(2, nameof(ParameterSetCounter.Token), field);
                c.AddComponentParameter(3, nameof(ParameterSetCounter.Reading), reading);
                c.CloseComponent();
                c.AddMarkupContent(4, $"<span>{reading}</span>");
                c.OpenComponent<FormidableInputText>(5);
                c.AddComponentParameter(6, nameof(FormidableInputText.For), Expression.Lambda<Func<string?>>(accessor.Body));
                c.AddComponentParameter(7, "Value", accessor.Compile()());
                c.AddComponentParameter(8, "ValueChanged", EventCallback.Factory.Create(setter.Target!, setter));
                c.CloseComponent();
            }));
            b.CloseComponent();
        }

        private sealed class Sink(EngineOrder order)
        {
            public void SetDescription(string? value) => order.Description = value ?? string.Empty;

            public void SetName(string? value) => order.Customer!.Name = value ?? string.Empty;
        }
    }

    /// <summary>A form whose only component for the customer's name is a message: nothing registers the field.</summary>
    private sealed class MessageOnlyHost : ComponentBase
    {
        private static readonly FormidableOptions Options = new() { RefreshDebounce = Timeout.InfiniteTimeSpan };

        [Parameter]
        public EngineOrder Order { get; set; } = default!;

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<FormidableForm<EngineOrder>>(0);
            builder.AddComponentParameter(1, nameof(FormidableForm<EngineOrder>.Model), Order);
            builder.AddComponentParameter(2, nameof(FormidableForm<EngineOrder>.Options), Options);
            builder.AddComponentParameter(3, nameof(FormidableForm<EngineOrder>.ChildContent), (RenderFragment<FormidableFormContext>)(_ => inner =>
            {
                inner.OpenComponent<FormidableFieldMessage<string>>(0);
                inner.AddComponentParameter(1, nameof(FormidableFieldMessage<string>.For), (Expression<Func<string>>)(() => Order.Customer!.Name));
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        }
    }
}
