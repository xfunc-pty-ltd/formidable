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
/// which is what proves the engine's key set complete. Under a row-key check a wrapper also
/// answers the publishes after which a row may have moved (a form-level change, or its field name
/// on another owner), because its render re-sets the components inside it, where the check reads;
/// a leaf input keeps the field filter.
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

    /// <summary>Which field a touched flip names, against the description wrapper's own.</summary>
    public enum TouchedField
    {
        /// <summary>The wrapper's own field.</summary>
        Own,

        /// <summary>A field of the wrapper's name on another owner, as a replaced owner or a shifted row names.</summary>
        SameNameOnAnotherOwner,

        /// <summary>A field of another name.</summary>
        AnotherName,
    }

    // Editing the other field starts a check, and its begin and end are form-level changes, which
    // a wrapper under a row-key check answers: a component kept inside it is re-parameterised,
    // which is where the check re-reads the accessor. The defaults host above is pinned not to.
    [Fact]
    public async Task Under_VerifyRowKeys_a_wrapper_re_renders_when_a_check_elsewhere_begins_and_ends()
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

    // A touched flip publishes one field and nothing form-level, so each case isolates one clause
    // of the wrapper rule. Mutations that must break it: drop the name clause (the second case
    // stops re-rendering), or answer every publish under a check (the third case starts).
    [Theory]
    [InlineData(TouchedField.Own, true)]
    [InlineData(TouchedField.SameNameOnAnotherOwner, true)]
    [InlineData(TouchedField.AnotherName, false)]
    public async Task Under_VerifyRowKeys_a_wrapper_answers_a_single_field_publish_only_where_a_row_may_have_moved(TouchedField touched, bool reRenders)
    {
        var order = new EngineOrder { Customer = new EngineCustomer() };
        var cut = Render<TwoWrapperHost>(p => p
            .Add(x => x.Order, order)
            .Add(x => x.Options, new FormidableOptions { RefreshDebounce = Timeout.InfiniteTimeSpan, VerifyRowKeys = true }));
        var descriptionChild = cut.FindComponents<ParameterSetCounter>().Single(c => c.Instance.Name == "description").Instance;
        var engine = cut.FindComponent<FormidableForm<EngineOrder>>().Instance.Engine!;
        var field = touched switch
        {
            TouchedField.Own => new FieldIdentifier(order, nameof(EngineOrder.Description)),
            TouchedField.SameNameOnAnotherOwner => new FieldIdentifier(new EngineOrder(), nameof(EngineOrder.Description)),
            _ => new FieldIdentifier(order.Customer!, nameof(EngineCustomer.Name)),
        };
        var publishes = new List<FormidableStateChangedEventArgs>();
        engine.StateChanged += (_, e) => publishes.Add(e);

        var before = descriptionChild.ParameterSets;
        await cut.InvokeAsync(() => engine.MarkTouched(field));
        await cut.InvokeAsync(() => { });

        var publish = Assert.Single(publishes);
        Assert.False(publish.FormChanged);
        Assert.Equal([field], publish.ChangedFields!);
        Assert.Equal(reRenders, descriptionChild.ParameterSets > before);

        await Services.DisposeAsync();
    }

    // A leaf's own re-render re-sets nothing, so a row-key check gains nothing from it: a kit
    // input outside every wrapper keeps the field filter under a check, through the other field's
    // touched flip and its check's form-level begin and end alike.
    [Fact]
    public async Task A_leaf_input_under_a_check_does_not_re_render_on_an_unrelated_publish()
    {
        var order = new EngineOrder { Customer = new EngineCustomer() };
        var cut = Render<TwoInputHost>(p => p.Add(x => x.Order, order));
        var engine = cut.FindComponent<FormidableForm<EngineOrder>>().Instance.Engine!;
        var description = cut.FindComponents<FormidableInputText>().Single(c => c.Markup.Contains("-description\"", StringComparison.Ordinal));
        var descriptionField = new FieldIdentifier(order, nameof(EngineOrder.Description));
        var publishes = new List<FormidableStateChangedEventArgs>();
        engine.StateChanged += (_, e) => publishes.Add(e);

        var before = description.RenderCount;
        await cut.InvokeAsync(() => cut.Find("input[id$='-name']").Change("Ada"));
        cut.WaitForState(() => !engine.IsValidating, TimeSpan.FromSeconds(5));
        await cut.InvokeAsync(() => { });

        Assert.Contains(publishes, e => e.FormChanged);
        Assert.DoesNotContain(publishes, e => e.Includes(descriptionField));
        Assert.Equal(before, description.RenderCount);

        await Services.DisposeAsync();
    }

    // Rows bound by their index through the stable model, unkeyed: inserting at the front leaves
    // each row's components in place with an accessor that now reaches the row before. The page
    // reports the edit on the list, as FormidableFieldContext.Edit does, before it renders. A
    // warming check has already given every row its settled state, so the neighbours' states are
    // identical across the shift and no publish names a row's own field: only the check's
    // form-level begin and end can re-render the wrappers. Mutation that must break it: a wrapper
    // that re-renders only for its own field, or a rule without the form-level clause.
    [Fact]
    public async Task A_shifted_row_with_identical_neighbour_states_still_throws_under_VerifyRowKeys()
    {
        var order = new EngineOrder
        {
            Customer = new EngineCustomer(),
            Items = [new EngineItem { Sku = "a" }, new EngineItem { Sku = "b" }],
        };
        var cut = Render<IndexBoundRowsHost>(p => p.Add(x => x.Order, order));
        var engine = cut.FindComponent<FormidableForm<EngineOrder>>().Instance.Engine!;
        var items = new FieldIdentifier(order, nameof(EngineOrder.Items));

        // The form's first check turns every passing row green at once, a move each row's own
        // wrapper answers; run it before the shift so that move is behind it.
        await cut.InvokeAsync(() => engine.EditContext.NotifyFieldChanged(items));
        cut.WaitForState(() => !engine.IsValidating, TimeSpan.FromSeconds(5));
        await cut.InvokeAsync(() => { });
        Assert.False(Renderer.UnhandledException.IsCompleted);

        // The throw reaches the renderer's unhandled path, which completes Renderer.UnhandledException.
        await cut.InvokeAsync(() =>
        {
            order.Items.Insert(0, new EngineItem { Sku = "n" });
            engine.EditContext.NotifyFieldChanged(items);
        });
        await cut.InvokeAsync(() => { });

        Assert.True(Renderer.UnhandledException.IsCompleted, "no re-render reached the shifted rows");
        var invalid = Assert.IsType<InvalidOperationException>(await Renderer.UnhandledException);
        Assert.Contains("same field on a different EngineItem", invalid.Message, StringComparison.Ordinal);
        Assert.Contains(
            $"{nameof(FormidableOptions)}.{nameof(FormidableOptions.VerifyRowKeys)}",
            invalid.Message,
            StringComparison.Ordinal);

        await Services.DisposeAsync();
    }

    // The page replaces the object a wrapper's accessor reaches and marks the new owner's field
    // touched before it renders. That publish names the field on the new owner and nothing
    // form-level, so only the wrapper's name clause can re-render it. Mutation that must break it:
    // a wrapper that re-renders only for its own field, or a rule without the name clause.
    [Fact]
    public async Task A_replaced_owner_still_throws_under_VerifyRowKeys()
    {
        var order = new EngineOrder { Customer = new EngineCustomer { Name = "original" } };
        var cut = Render<ReplacedOwnerHost>(p => p.Add(x => x.Order, order));
        var engine = cut.FindComponent<FormidableForm<EngineOrder>>().Instance.Engine!;

        await cut.InvokeAsync(() =>
        {
            order.Customer = new EngineCustomer();
            engine.MarkTouched(new FieldIdentifier(order.Customer, nameof(EngineCustomer.Name)));
        });
        await cut.InvokeAsync(() => { });

        Assert.True(Renderer.UnhandledException.IsCompleted, "no re-render reached the replaced owner's wrapper");
        var invalid = Assert.IsType<InvalidOperationException>(await Renderer.UnhandledException);
        Assert.Contains("same field on a different EngineCustomer", invalid.Message, StringComparison.Ordinal);
        Assert.Contains(
            $"{nameof(FormidableOptions)}.{nameof(FormidableOptions.VerifyRowKeys)}",
            invalid.Message,
            StringComparison.Ordinal);

        await Services.DisposeAsync();
    }

    /// <summary>Which templated message the Theory below reads.</summary>
    public enum TemplatedMessage
    {
        /// <summary>A FormidableFieldMessage with an ItemTemplate.</summary>
        Field,

        /// <summary>A FormidableModelMessage with an ItemTemplate.</summary>
        ModelLevel,
    }

    // A message with an ItemTemplate runs the consumer's template again at each render, which
    // re-sets the components inside it, so under a row-key check it answers a form-level change
    // as a wrapper does. Editing the other field moves neither message's view, and its check's
    // begin and end are form-level changes. Mutation that must break it: force the message base's
    // override false (the field case) or the model message's (the model-level case).
    [Theory]
    [InlineData(TemplatedMessage.Field)]
    [InlineData(TemplatedMessage.ModelLevel)]
    public async Task Under_VerifyRowKeys_a_templated_message_re_sets_its_template_when_another_field_is_edited(TemplatedMessage message)
    {
        var order = new EngineOrder { Customer = new EngineCustomer() };
        var cut = Render<TemplatedMessageHost>(p => p.Add(x => x.Order, order));
        var form = cut.FindComponent<FormidableForm<EngineOrder>>().Instance;
        var engine = form.Engine!;

        // A too-short description gives the field message an issue to template, and a blocked
        // submit gives the model message the form's own.
        await cut.InvokeAsync(() => cut.Find("input[id$='-description']").Change("abc"));
        cut.WaitForState(() => !engine.IsValidating, TimeSpan.FromSeconds(5));
        await cut.InvokeAsync(() => form.SubmitAsync());
        cut.WaitForState(() => !engine.IsValidating, TimeSpan.FromSeconds(5));
        await cut.InvokeAsync(() => { });

        var observed = message == TemplatedMessage.Field
            ? new FieldIdentifier(order, nameof(EngineOrder.Description))
            : new FieldIdentifier(order, string.Empty);
        var counterName = message == TemplatedMessage.Field ? "field-template" : "model-template";
        var counter = cut.FindComponents<ParameterSetCounter>().Single(c => c.Instance.Name == counterName).Instance;
        var publishes = new List<FormidableStateChangedEventArgs>();
        engine.StateChanged += (_, e) => publishes.Add(e);

        var before = counter.ParameterSets;
        await cut.InvokeAsync(() => cut.Find("input[id$='-name']").Change("Ada"));
        cut.WaitForState(() => !engine.IsValidating, TimeSpan.FromSeconds(5));
        await cut.InvokeAsync(() => { });

        Assert.Contains(publishes, e => e.FormChanged);
        Assert.DoesNotContain(publishes, e => e.Includes(observed));
        Assert.True(counter.ParameterSets > before, "the message did not re-run its template");

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

    /// <summary>Two kit inputs, Description and the customer's name, under a FormidableForm with VerifyRowKeys on and no wrapper; ValueChanged goes to a sink, so the page does not re-render per edit.</summary>
    private sealed class TwoInputHost : ComponentBase
    {
        private static readonly FormidableOptions Options = new() { RefreshDebounce = Timeout.InfiniteTimeSpan, VerifyRowKeys = true };

        [Parameter]
        public EngineOrder Order { get; set; } = default!;

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            var sink = new Sink(Order);
            builder.OpenComponent<FormidableForm<EngineOrder>>(0);
            builder.AddComponentParameter(1, nameof(FormidableForm<EngineOrder>.Model), Order);
            builder.AddComponentParameter(2, nameof(FormidableForm<EngineOrder>.Options), Options);
            builder.AddComponentParameter(3, nameof(FormidableForm<EngineOrder>.ChildContent), (RenderFragment<FormidableFormContext>)(_ => inner =>
            {
                inner.OpenComponent<FormidableInputText>(0);
                inner.AddComponentParameter(1, nameof(FormidableInputText.For), (Expression<Func<string?>>)(() => Order.Description));
                inner.AddComponentParameter(2, "Value", Order.Description);
                inner.AddComponentParameter(3, "ValueChanged", EventCallback.Factory.Create<string?>(sink, sink.SetDescription));
                inner.CloseComponent();
                inner.OpenComponent<FormidableInputText>(4);
                inner.AddComponentParameter(5, nameof(FormidableInputText.For), (Expression<Func<string?>>)(() => Order.Customer!.Name));
                inner.AddComponentParameter(6, "Value", Order.Customer!.Name);
                inner.AddComponentParameter(7, "ValueChanged", EventCallback.Factory.Create<string?>(sink, sink.SetName));
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        }

        private sealed class Sink(EngineOrder order)
        {
            public void SetDescription(string? value) => order.Description = value ?? string.Empty;

            public void SetName(string? value) => order.Customer!.Name = value ?? string.Empty;
        }
    }

    /// <summary>Two kit inputs, Description and the customer's name, a message for Description and a model message, each message with an ItemTemplate holding a counter, under VerifyRowKeys; ValueChanged goes to a sink, so the page does not re-render per edit.</summary>
    private sealed class TemplatedMessageHost : ComponentBase
    {
        private static readonly FormidableOptions Options = new() { RefreshDebounce = Timeout.InfiniteTimeSpan, VerifyRowKeys = true };

        [Parameter]
        public EngineOrder Order { get; set; } = default!;

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            var sink = new Sink(Order);
            builder.OpenComponent<FormidableForm<EngineOrder>>(0);
            builder.AddComponentParameter(1, nameof(FormidableForm<EngineOrder>.Model), Order);
            builder.AddComponentParameter(2, nameof(FormidableForm<EngineOrder>.Options), Options);
            builder.AddComponentParameter(3, nameof(FormidableForm<EngineOrder>.ChildContent), (RenderFragment<FormidableFormContext>)(_ => inner =>
            {
                inner.OpenComponent<FormidableInputText>(0);
                inner.AddComponentParameter(1, nameof(FormidableInputText.For), (Expression<Func<string?>>)(() => Order.Description));
                inner.AddComponentParameter(2, "Value", Order.Description);
                inner.AddComponentParameter(3, "ValueChanged", EventCallback.Factory.Create<string?>(sink, sink.SetDescription));
                inner.CloseComponent();
                inner.OpenComponent<FormidableInputText>(4);
                inner.AddComponentParameter(5, nameof(FormidableInputText.For), (Expression<Func<string?>>)(() => Order.Customer!.Name));
                inner.AddComponentParameter(6, "Value", Order.Customer!.Name);
                inner.AddComponentParameter(7, "ValueChanged", EventCallback.Factory.Create<string?>(sink, sink.SetName));
                inner.CloseComponent();
                inner.OpenComponent<FormidableFieldMessage<string>>(8);
                inner.AddComponentParameter(9, nameof(FormidableFieldMessage<string>.For), (Expression<Func<string>>)(() => Order.Description));
                inner.AddComponentParameter(10, nameof(FormidableFieldMessage<string>.ItemTemplate), Template("field-template"));
                inner.CloseComponent();
                inner.OpenComponent<FormidableModelMessage>(11);
                inner.AddComponentParameter(12, nameof(FormidableModelMessage.ItemTemplate), Template("model-template"));
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        }

        private static RenderFragment<VisibleIssue> Template(string name) => issue => c =>
        {
            c.OpenComponent<ParameterSetCounter>(0);
            c.AddComponentParameter(1, nameof(ParameterSetCounter.Name), name);
            c.AddComponentParameter(2, nameof(ParameterSetCounter.Token), issue);
            c.CloseComponent();
        };

        private sealed class Sink(EngineOrder order)
        {
            public void SetDescription(string? value) => order.Description = value ?? string.Empty;

            public void SetName(string? value) => order.Customer!.Name = value ?? string.Empty;
        }
    }

    /// <summary>The order's items as rows bound by their index through the stable model (<c>() => Order.Items[index].Sku</c>) and rendered without a key: each row a FormidableField around a message, under VerifyRowKeys.</summary>
    private sealed class IndexBoundRowsHost : ComponentBase
    {
        private static readonly FormidableOptions Options = new() { RefreshDebounce = Timeout.InfiniteTimeSpan, VerifyRowKeys = true };

        [Parameter]
        public EngineOrder Order { get; set; } = default!;

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<FormidableForm<EngineOrder>>(0);
            builder.AddComponentParameter(1, nameof(FormidableForm<EngineOrder>.Model), Order);
            builder.AddComponentParameter(2, nameof(FormidableForm<EngineOrder>.Options), Options);
            builder.AddComponentParameter(3, nameof(FormidableForm<EngineOrder>.ChildContent), (RenderFragment<FormidableFormContext>)(_ => inner =>
            {
                for (var i = 0; i < Order.Items.Count; i++)
                {
                    var index = i;
                    Expression<Func<string>> accessor = () => Order.Items[index].Sku;
                    inner.OpenRegion(i);
                    inner.OpenComponent<FormidableField<string>>(0);
                    inner.AddComponentParameter(1, nameof(FormidableField<string>.For), accessor);
                    inner.AddComponentParameter(2, nameof(FormidableField<string>.ChildContent), (RenderFragment<FormidableFieldContext>)(_ => content =>
                    {
                        content.OpenComponent<FormidableFieldMessage<string>>(0);
                        content.AddComponentParameter(1, nameof(FormidableFieldMessage<string>.For), accessor);
                        content.CloseComponent();
                    }));
                    inner.CloseComponent();
                    inner.CloseRegion();
                }
            }));
            builder.CloseComponent();
        }
    }

    /// <summary>A FormidableField keyed by the customer around a message, both reaching the customer through the stable model (<c>() => Order.Customer!.Name</c>), under VerifyRowKeys.</summary>
    private sealed class ReplacedOwnerHost : ComponentBase
    {
        private static readonly FormidableOptions Options = new() { RefreshDebounce = Timeout.InfiniteTimeSpan, VerifyRowKeys = true };

        [Parameter]
        public EngineOrder Order { get; set; } = default!;

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<FormidableForm<EngineOrder>>(0);
            builder.AddComponentParameter(1, nameof(FormidableForm<EngineOrder>.Model), Order);
            builder.AddComponentParameter(2, nameof(FormidableForm<EngineOrder>.Options), Options);
            builder.AddComponentParameter(3, nameof(FormidableForm<EngineOrder>.ChildContent), (RenderFragment<FormidableFormContext>)(_ => inner =>
            {
                inner.OpenComponent<FormidableField<string>>(0);
                inner.SetKey(Order.Customer);
                inner.AddComponentParameter(1, nameof(FormidableField<string>.For), (Expression<Func<string>>)(() => Order.Customer!.Name));
                inner.AddComponentParameter(2, nameof(FormidableField<string>.ChildContent), (RenderFragment<FormidableFieldContext>)(_ => content =>
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
