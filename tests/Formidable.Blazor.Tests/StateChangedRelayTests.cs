using Bunit;
using FluentValidation;
using Formidable.Blazor.Tests.Fixtures;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using System.Linq.Expressions;

namespace Formidable.Blazor.Tests;

/// <summary>
/// A publish the engine raises outside a render batch (a check ending, here released through a
/// gate) must cost one render batch, not one per subscriber. Batches are counted from the
/// renderer's own contract: every render queued in a batch runs before any <c>OnAfterRender</c>
/// of that batch. Two sentinels bracket the kit subscribers, the first to subscribe and the last,
/// and each marks its render and its after-render; an after-render mark followed by a render
/// mark is a batch boundary. (bUnit's <c>RenderCount</c> on the host cannot serve: measured, it
/// climbs once per updated descendant inside a single batch, 15 for one event dispatch here.)
/// Inside an event dispatch Blazor already coalesces, which the guard pins. Delivery stays
/// synchronous within the publish and a handler's throw still leaves the call that raised the
/// event, which the throw pin holds.
/// </summary>
public class StateChangedRelayTests : BunitContext
{
    public StateChangedRelayTests()
    {
        Services.AddSingleton<IFormidableFocusService>(new RecordingFocusService());
        Services.AddSingleton<IFormidableFieldOrderService>(new RecordingFieldOrderService());
        Services.AddSingleton<IFormidableDomValueSync>(new RecordingDomValueSync());
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    // Fails today: the gate's release publishes from a continuation, and every subscriber opens a
    // batch of its own, so the first sentinel's after-render lands before the last one renders.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_publish_from_a_check_ending_is_one_render_batch(bool attach)
    {
        var gated = new GatedValidator { ShouldPass = true };
        Services.AddSingleton<IValidator<EngineOrder>>(gated);
        Services.AddFormidableBlazor();
        var order = new EngineOrder { Customer = new EngineCustomer() };
        var cut = Render<RelayHost>(p => p.Add(x => x.Order, order).Add(x => x.Attach, attach));
        var engine = RelayHost.EngineOf(cut, attach);

        await cut.InvokeAsync(() => cut.Find("input").Change("ok"));
        cut.WaitForState(() => gated.Started == 1, TimeSpan.FromSeconds(5));

        var publishes = 0;
        engine.StateChanged += (_, _) => publishes++;
        cut.Instance.Marks.Clear();

        await cut.InvokeAsync(() => gated.Gate.SetResult());
        cut.WaitForState(() => !engine.IsValidating, TimeSpan.FromSeconds(5));
        await cut.InvokeAsync(() => { });

        Assert.True(publishes >= 1, "the gate's release published nothing");
        Assert.Equal(publishes, Batches(cut.Instance.Marks));

        await Services.DisposeAsync();
    }

    // Guard: inside an event dispatch the publishes already share the event's batch.
    [Fact]
    public async Task Publishes_inside_an_event_dispatch_share_its_batch()
    {
        var gated = new GatedValidator { ShouldPass = true };
        Services.AddSingleton<IValidator<EngineOrder>>(gated);
        Services.AddFormidableBlazor();
        var order = new EngineOrder { Customer = new EngineCustomer() };
        var cut = Render<RelayHost>(p => p.Add(x => x.Order, order));
        var engine = RelayHost.EngineOf(cut, attach: false);

        var publishes = 0;
        engine.StateChanged += (_, _) => publishes++;
        cut.Instance.Marks.Clear();
        await cut.InvokeAsync(() => cut.Find("input").Change("ok"));

        Assert.True(publishes >= 1, "the change published nothing");
        Assert.Equal(1, Batches(cut.Instance.Marks));

        await Services.DisposeAsync();
    }

    // Guard for the throw contract: a call of yours that raises the event still gets the throw.
    [Fact]
    public async Task A_handler_throw_during_ApplyServerIssues_leaves_the_call()
    {
        Services.AddSingleton<IValidator<EngineOrder>, EngineOrderValidator>();
        Services.AddFormidableBlazor();
        var order = new EngineOrder { Customer = new EngineCustomer() };
        var cut = Render<RelayHost>(p => p.Add(x => x.Order, order));
        var form = cut.FindComponent<FormidableForm<EngineOrder>>().Instance;
        form.Engine!.StateChanged += (_, _) => throw new InvalidOperationException("handler");

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cut.InvokeAsync(() => form.ApplyServerIssues(
                [new ValidationIssue(nameof(EngineOrder.Description), "server says no")])));

        Assert.Equal("handler", thrown.Message);

        await Services.DisposeAsync();
    }

    // A handler that publishes again during the relay's render (here by touching a field) runs a
    // nested publish inside the outer one; a throw from the same raise must still leave the call
    // that raised the event, not slip to the renderer once the nested publish has returned.
    [Fact]
    public async Task A_handler_throw_still_leaves_the_call_when_another_handler_publishes()
    {
        Services.AddSingleton<IValidator<EngineOrder>, EngineOrderValidator>();
        Services.AddFormidableBlazor();
        var order = new EngineOrder { Customer = new EngineCustomer() };
        var cut = Render<RelayHost>(p => p.Add(x => x.Order, order));
        var form = cut.FindComponent<FormidableForm<EngineOrder>>().Instance;
        var engine = form.Engine!;
        var nested = 0;
        engine.StateChanged += (_, _) =>
        {
            if (nested++ == 0)
            {
                engine.MarkTouched(new FieldIdentifier(order.Customer!, nameof(EngineCustomer.Name)));
            }
        };
        engine.StateChanged += (_, _) => throw new InvalidOperationException("handler");

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cut.InvokeAsync(() => form.ApplyServerIssues(
                [new ValidationIssue(nameof(EngineOrder.Description), "server says no")])));

        Assert.Equal("handler", thrown.Message);
        Assert.False(Renderer.UnhandledException.IsCompleted);

        await Services.DisposeAsync();
    }

    // A publish made off the renderer's dispatcher (a test thread calling the engine with no
    // InvokeAsync around it) throws Blazor's dispatcher error from the relay's StateHasChanged, as
    // Blazor's own form components throw. The common test-thread call is a field-changed
    // notification, which reaches MarkTouched and publishes before anything else in the handler.
    [Fact]
    public async Task A_notification_off_the_dispatcher_throws_the_dispatcher_error()
    {
        Services.AddSingleton<IValidator<EngineOrder>, EngineOrderValidator>();
        Services.AddFormidableBlazor();
        var order = new EngineOrder { Customer = new EngineCustomer() };
        var cut = Render<RelayHost>(p => p.Add(x => x.Order, order));
        var engine = RelayHost.EngineOf(cut, attach: false);
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));

        var thrown = Assert.Throws<InvalidOperationException>(() => engine.EditContext.NotifyFieldChanged(description));

        Assert.Contains("Dispatcher", thrown.Message, StringComparison.Ordinal);

        await Services.DisposeAsync();
    }

    // The args of a publish the renderer refused must not wait in the relay's queue for the next
    // publish, which would raise them late, out of order and naming a field that did not move in
    // it. Mutation: drop the clear from Publish's catch; the later publish then raises twice, the
    // refused args first.
    [Fact]
    public async Task A_publish_after_a_refused_one_raises_only_its_own_args()
    {
        Services.AddSingleton<IValidator<EngineOrder>, EngineOrderValidator>();
        Services.AddFormidableBlazor();
        var order = new EngineOrder { Customer = new EngineCustomer() };
        var cut = Render<RelayHost>(p => p.Add(x => x.Order, order));
        var engine = RelayHost.EngineOf(cut, attach: false);
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));
        var customerName = new FieldIdentifier(order.Customer!, nameof(EngineCustomer.Name));
        Assert.Throws<InvalidOperationException>(() => engine.MarkTouched(description));   // off the dispatcher: refused
        var events = new List<FormidableStateChangedEventArgs>();
        engine.StateChanged += (_, e) => events.Add(e);

        await cut.InvokeAsync(() => engine.MarkTouched(customerName));

        var e = Assert.Single(events);
        Assert.True(e.Includes(customerName));
        Assert.False(e.Includes(description), "the refused publish's args were raised late");

        await Services.DisposeAsync();
    }

    // Review focus 1: a handler that unsubscribes itself during the relay's render.
    [Fact]
    public async Task A_handler_that_unsubscribes_during_a_publish_does_not_break_the_relay()
    {
        var gated = new GatedValidator { ShouldPass = true };
        Services.AddSingleton<IValidator<EngineOrder>>(gated);
        Services.AddFormidableBlazor();
        var order = new EngineOrder { Customer = new EngineCustomer() };
        var cut = Render<RelayHost>(p => p.Add(x => x.Order, order));
        var engine = RelayHost.EngineOf(cut, attach: false);

        var seen = 0;
        EventHandler<FormidableStateChangedEventArgs>? once = null;
        once = (_, _) => { seen++; engine.StateChanged -= once; };
        engine.StateChanged += once;
        var later = 0;
        engine.StateChanged += (_, _) => later++;

        await cut.InvokeAsync(() => cut.Find("input").Change("ok"));
        cut.WaitForState(() => gated.Started == 1, TimeSpan.FromSeconds(5));
        await cut.InvokeAsync(() => gated.Gate.SetResult());
        cut.WaitForState(() => !engine.IsValidating, TimeSpan.FromSeconds(5));

        Assert.Equal(1, seen);
        Assert.True(later > 1, "the later subscriber missed publishes after the first unsubscribed");

        await Services.DisposeAsync();
    }

    // Review focus 2: a model swap while a check is in flight. The old engine is disposed and
    // its pass is superseded; the publish that would end it must raise nothing.
    [Fact]
    public async Task A_relay_raises_nothing_for_a_disposed_engine()
    {
        var gated = new GatedValidator { ShouldPass = true };
        Services.AddSingleton<IValidator<EngineOrder>>(gated);
        Services.AddFormidableBlazor();
        var order = new EngineOrder { Customer = new EngineCustomer() };
        var cut = Render<RelayHost>(p => p.Add(x => x.Order, order));
        var oldEngine = RelayHost.EngineOf(cut, attach: false);

        await cut.InvokeAsync(() => cut.Find("input").Change("ok"));
        cut.WaitForState(() => gated.Started == 1, TimeSpan.FromSeconds(5));
        var raisedAfterDispose = 0;
        oldEngine.StateChanged += (_, _) => raisedAfterDispose++;

        cut.Render(p => p.Add(x => x.Order, new EngineOrder { Customer = new EngineCustomer() }));
        await cut.InvokeAsync(() => gated.Gate.SetResult());
        await cut.InvokeAsync(() => { });

        Assert.Equal(0, raisedAfterDispose);
        Assert.False(Renderer.UnhandledException.IsCompleted);

        await Services.DisposeAsync();
    }

    /// <summary>
    /// The number of render batches the sentinel marks span: one, plus one for every after-render
    /// mark that a render mark follows, because a batch runs all of its renders before any of its
    /// after-renders.
    /// </summary>
    private static int Batches(IReadOnlyList<string> marks)
    {
        var batches = marks.Count > 0 ? 1 : 0;
        for (var i = 1; i < marks.Count; i++)
        {
            if (marks[i - 1].StartsWith("after:", StringComparison.Ordinal)
                && marks[i].StartsWith("render:", StringComparison.Ordinal))
            {
                batches++;
            }
        }

        return batches;
    }

    /// <summary>
    /// Six kit subscribers under either root: a summary, two inputs, two messages and the model
    /// message, bracketed by the two sentinels. Options keep the first-render re-check off, so
    /// the only check is the one the test starts. Attach mode wraps the validator in a
    /// consumer-owned EditForm.
    /// </summary>
    private sealed class RelayHost : ComponentBase
    {
        private static readonly FormidableOptions Options = new() { RefreshDebounce = Timeout.InfiniteTimeSpan };
        private EditContext? _editContext;

        public List<string> Marks { get; } = [];

        [Parameter]
        public EngineOrder Order { get; set; } = default!;

        [Parameter]
        public bool Attach { get; set; }

        public static IFormidableEngine EngineOf(IRenderedComponent<RelayHost> cut, bool attach) => attach
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
            if (Attach)
            {
                builder.OpenComponent<EditForm>(0);
                builder.AddComponentParameter(1, nameof(EditForm.EditContext), _editContext);
                builder.AddComponentParameter(2, nameof(EditForm.ChildContent), (RenderFragment<EditContext>)(_ => inner =>
                {
                    inner.OpenComponent<FormidableValidator<EngineOrder>>(0);
                    inner.AddComponentParameter(1, nameof(FormidableValidator<EngineOrder>.Options), Options);
                    inner.AddComponentParameter(2, nameof(FormidableValidator<EngineOrder>.ChildContent), (RenderFragment<FormidableFormContext>)(_ => Fields));
                    inner.CloseComponent();
                }));
                builder.CloseComponent();
                return;
            }

            builder.OpenComponent<FormidableForm<EngineOrder>>(0);
            builder.AddComponentParameter(1, nameof(FormidableForm<EngineOrder>.Model), Order);
            builder.AddComponentParameter(2, nameof(FormidableForm<EngineOrder>.Options), Options);
            builder.AddComponentParameter(3, nameof(FormidableForm<EngineOrder>.ChildContent), (RenderFragment<FormidableFormContext>)(_ => Fields));
            builder.CloseComponent();
        }

        private void Fields(RenderTreeBuilder inner)
        {
            Sentinel(inner, 0, "first");
            inner.OpenComponent<FormidableSummary>(3);
            inner.CloseComponent();
            Input(inner, 4, () => Order.Description, v => Order.Description = v ?? string.Empty);
            Input(inner, 8, () => Order.Customer!.Name, v => Order.Customer!.Name = v ?? string.Empty);
            Message(inner, 12, () => Order.Description);
            Message(inner, 14, () => Order.Customer!.Name);
            inner.OpenComponent<FormidableModelMessage>(16);
            inner.CloseComponent();
            Sentinel(inner, 17, "last");
        }

        private void Sentinel(RenderTreeBuilder b, int seq, string name)
        {
            b.OpenComponent<BatchSentinel>(seq);
            b.AddComponentParameter(seq + 1, nameof(BatchSentinel.Name), name);
            b.AddComponentParameter(seq + 2, nameof(BatchSentinel.Marks), Marks);
            b.CloseComponent();
        }

        private void Input(RenderTreeBuilder b, int seq, Expression<Func<string?>> accessor, Action<string?> setter)
        {
            b.OpenComponent<FormidableInputText>(seq);
            b.AddComponentParameter(seq + 1, nameof(FormidableInputText.For), accessor);
            b.AddComponentParameter(seq + 2, "Value", accessor.Compile()());
            b.AddComponentParameter(seq + 3, "ValueChanged", EventCallback.Factory.Create(this, setter));
            b.CloseComponent();
        }

        private static void Message(RenderTreeBuilder b, int seq, Expression<Func<string>> accessor)
        {
            b.OpenComponent<FormidableFieldMessage<string>>(seq);
            b.AddComponentParameter(seq + 1, nameof(FormidableFieldMessage<string>.For), accessor);
            b.CloseComponent();
        }
    }

    /// <summary>
    /// Subscribes to the engine the way a kit component does and marks each render and each
    /// after-render with its name. Placed first and last among the subscribers, two of these
    /// show whether one publish ran as one batch.
    /// </summary>
    private sealed class BatchSentinel : ComponentBase, IDisposable
    {
        private IFormidableEngine? _engine;

        [CascadingParameter]
        public FormidableFormContext? Context { get; set; }

        [Parameter]
        public string Name { get; set; } = string.Empty;

        [Parameter]
        public List<string> Marks { get; set; } = default!;

        protected override void OnParametersSet()
        {
            if (_engine is null && Context is not null)
            {
                _engine = Context.Engine;
                _engine.StateChanged += OnStateChanged;
            }
        }

        protected override void BuildRenderTree(RenderTreeBuilder builder) => Marks.Add($"render:{Name}");

        protected override void OnAfterRender(bool firstRender) => Marks.Add($"after:{Name}");

        public void Dispose()
        {
            if (_engine is not null)
            {
                _engine.StateChanged -= OnStateChanged;
            }
        }

        private void OnStateChanged(object? sender, FormidableStateChangedEventArgs e) => _ = InvokeAsync(StateHasChanged);
    }
}
