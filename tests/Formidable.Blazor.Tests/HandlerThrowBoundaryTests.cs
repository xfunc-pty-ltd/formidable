using System.Linq.Expressions;
using Bunit;
using Formidable.Blazor.Tests.Fixtures;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Formidable.Blazor.Tests;

/// <summary>
/// A consumer's <c>EditContext.OnValidationStateChanged</c> or <see cref="IFormidableEngine.StateChanged"/>
/// handler throws while a root answers a field leaving the page, or a field starting to wait for
/// submit, in a render of a component inside the root alone. Both roots hand the throw to the
/// nearest <see cref="ErrorBoundary"/> above them, or, with none, to the renderer's own unhandled
/// path, as a throw from any component's lifecycle would go. A root removed before it can hand the
/// throw on logs it as a warning and hands on nothing.
/// </summary>
public class HandlerThrowBoundaryTests : BunitContext
{
    private const string HandlerMessage = "a consumer handler throws";
    private static readonly string TooLong = new('x', 11);

    public HandlerThrowBoundaryTests()
    {
        // Attach mode resolves the three JS-backed services; these doubles go in ahead of
        // AddFormidableBlazor, whose registrations are TryAdds.
        Services.AddSingleton<IFormidableFocusService>(new RecordingFocusService());
        Services.AddSingleton<IFormidableFieldOrderService>(new RecordingFieldOrderService());
        Services.AddSingleton<IFormidableDomValueSync>(new RecordingDomValueSync());
        Services.AddFormidableBlazor();
        Services.AddSingleton<FluentValidation.IValidator<EngineOrder>, EngineOrderValidator>();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public enum Trigger
    {
        // The section stops rendering the field's input.
        FieldLeaves,

        // The section renders the field's input with WaitForSubmit.
        FieldWaits,
    }

    // What the root's own content renders.
    public enum RootContent
    {
        // A DescriptionSection, which renders the description's input itself.
        Section,

        // The description's input, in the root's own content.
        Input,

        // Nothing but the native message.
        Nothing,
    }

    public enum Surface
    {
        // EditContext.OnValidationStateChanged.
        EditContext,

        // IFormidableEngine.StateChanged.
        Engine,
    }

    public static TheoryData<bool, Trigger, Surface> EveryRootTriggerAndSurface()
    {
        var data = new TheoryData<bool, Trigger, Surface>();
        foreach (var attach in new[] { false, true })
        {
            foreach (var trigger in new[] { Trigger.FieldLeaves, Trigger.FieldWaits })
            {
                foreach (var surface in new[] { Surface.EditContext, Surface.Engine })
                {
                    data.Add(attach, trigger, surface);
                }
            }
        }

        return data;
    }

    public static TheoryData<bool, Trigger> EveryRootAndTrigger()
    {
        var data = new TheoryData<bool, Trigger>();
        foreach (var attach in new[] { false, true })
        {
            foreach (var trigger in new[] { Trigger.FieldLeaves, Trigger.FieldWaits })
            {
                data.Add(attach, trigger);
            }
        }

        return data;
    }

    // The boundary around the root shows the handler's exception, and nothing reaches the
    // renderer's unhandled path. Mutation: remove the catch around the reconcile a root posts for
    // a nested render (the FieldLeaves rows fail), or around the engine's posted notification for
    // a change of hold (the FieldWaits rows fail), or stop handing the root's fault route to the
    // engine (the FieldWaits rows fail): the throw then goes to the dispatcher, which in bUnit
    // drops it, so the boundary stays empty.
    [Theory]
    [MemberData(nameof(EveryRootTriggerAndSurface))]
    public async Task A_throwing_handler_reaches_the_boundary_around_the_root(bool attach, Trigger trigger, Surface surface)
    {
        var (cut, engine) = await RenderWithLiveErrorAsync(attach, boundary: true);
        var armed = ArmOnce(engine, surface);

        await cut.InvokeAsync(() => Fire(cut, trigger));
        await Settle(cut);

        Assert.False(armed());
        Assert.Equal(HandlerMessage, cut.Find("p.boundary-error").TextContent);
        Assert.False(Renderer.UnhandledException.IsCompleted);

        await Services.DisposeAsync();
    }

    // With no boundary above the root, the root's hand-off reaches the renderer's own unhandled
    // path: bUnit completes Renderer.UnhandledException with the handler's exception, where Blazor
    // Server ends the circuit and WebAssembly shows its error UI. Mutation: remove either catch,
    // as above, and the dispatcher drops the throw, so the renderer never hears of it.
    [Theory]
    [MemberData(nameof(EveryRootAndTrigger))]
    public async Task With_no_boundary_a_throwing_handler_takes_the_renderer_s_unhandled_path(bool attach, Trigger trigger)
    {
        var (cut, engine) = await RenderWithLiveErrorAsync(attach, boundary: false);
        var armed = ArmOnce(engine, Surface.EditContext);

        await cut.InvokeAsync(() => Fire(cut, trigger));
        await Settle(cut);

        Assert.False(armed());
        Assert.True(Renderer.UnhandledException.IsCompleted);
        Assert.Equal(HandlerMessage, (await Renderer.UnhandledException).Message);

        await Services.DisposeAsync();
    }

    // The handler removes the root from the page and then throws, inside a post the root made
    // while it stood. The root is disposed by the time its hand-off runs, so it hands on nothing:
    // no exception reaches the renderer or the code running the post. It writes one warning
    // instead, to Trace and to the host's logger, naming the root and carrying the exception. The
    // test captures the post and runs it on the renderer's own context, so anything the post
    // throws reaches this test rather than the dispatcher, which in bUnit drops it. Mutation: drop
    // the root's disposed test before it hands the throw on, and the renderer rejects the removed
    // component with an ArgumentException out of the post; drop the warning, and neither the
    // Trace line nor the logged entry appears; log it without the exception, and the entry carries
    // none.
    [Theory]
    [MemberData(nameof(EveryRootAndTrigger))]
    public async Task A_root_removed_by_the_throwing_handler_logs_the_throw_and_hands_on_nothing(bool attach, Trigger trigger)
    {
        var logs = new CapturingLoggerProvider();
        Services.AddSingleton<ILoggerFactory>(LoggerFactory.Create(builder => builder.AddProvider(logs)));
        var (cut, engine) = await RenderWithLiveErrorAsync(attach, boundary: true);
        var order = cut.Instance.Order;
        var armed = ArmOnce(engine, Surface.EditContext, before: cut.Instance.RemoveRoot);
        var posts = new CapturingContext();

        await cut.InvokeAsync(() =>
        {
            // A render needs the renderer's own context current, so the captured posts come from
            // registry changes made directly.
            if (trigger == Trigger.FieldLeaves)
            {
                // The root posts its reconcile. The section's render below then finds that post
                // waiting and makes none, and the captured one answers the field leaving.
                WithContext(posts, () => engine.Registry.Register(new FieldIdentifier(order, "Phantom")).Dispose());
                Fire(cut, Trigger.FieldLeaves);
            }
            else
            {
                // A holding registration over the engaged, failing field: the engine posts its
                // notification, and the root its reconcile, which finds nothing left to reach.
                WithContext(posts, () => engine.Registry.RegisterWithHold(
                    new FieldIdentifier(order, nameof(EngineOrder.Description)), keepRegistered: false, holdsLiveMessages: true));
            }
        });
        Assert.Equal(trigger == Trigger.FieldLeaves ? 1 : 2, posts.Count);

        Exception? thrown = null;
        var traced = await TraceCapture.RunAsync(
            async () => thrown = await Record.ExceptionAsync(() => cut.InvokeAsync(posts.RunPosted)),
            line => line.Contains("left the page", StringComparison.Ordinal));

        Assert.Null(thrown);
        Assert.False(armed());
        Assert.Empty(cut.FindComponents<DescriptionSection>());
        Assert.Empty(cut.FindAll("p.boundary-error"));
        Assert.False(Renderer.UnhandledException.IsCompleted);

        var root = attach ? "FormidableValidator" : "FormidableForm";
        var line = Assert.Single(traced);
        Assert.StartsWith($"Formidable: a handler threw after the {root} for EngineOrder left the page", line);
        Assert.Contains($"(System.InvalidOperationException: {HandlerMessage})", line);
        var warnings = logs.Entries
            .Select((entry, index) => (entry.Level, entry.Message, Exception: logs.Exceptions[index]))
            .Where(entry => entry.Message.Contains("left the page", StringComparison.Ordinal))
            .ToList();
        var warning = Assert.Single(warnings);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Equal(line, warning.Message);
        Assert.Equal(HandlerMessage, Assert.IsType<InvalidOperationException>(warning.Exception).Message);

        await Services.DisposeAsync();
    }

    // A pin: a handler that throws on every call. The boundary around the root shows the first
    // throw and removes the root, and the root's disposal raises OnValidationStateChanged once
    // more, but not StateChanged. So an OnValidationStateChanged handler throws a second time,
    // from a component's Dispose, and that throw takes the renderer's unhandled path (on Blazor
    // Server it ends the circuit). A StateChanged handler is called once, and the boundary alone
    // shows its throw. Mutation: raise StateChanged in the engine's Dispose too, and the Engine
    // rows meet a second throw; drop the Dispose's OnValidationStateChanged, and the EditContext
    // rows meet none.
    [Theory]
    [InlineData(false, Surface.EditContext, 2)]
    [InlineData(false, Surface.Engine, 1)]
    [InlineData(true, Surface.EditContext, 2)]
    [InlineData(true, Surface.Engine, 1)]
    public async Task A_handler_that_throws_on_every_call_meets_the_root_s_removal_only_through_the_edit_context(
        bool attach,
        Surface surface,
        int calls)
    {
        var (cut, engine) = await RenderWithLiveErrorAsync(attach, boundary: true);
        var called = ArmAlways(engine, surface);

        await cut.InvokeAsync(() => Fire(cut, Trigger.FieldLeaves));
        await Settle(cut);

        Assert.Equal(HandlerMessage, cut.Find("p.boundary-error").TextContent);
        Assert.Equal(calls, called());
        Assert.Equal(calls == 2, Renderer.UnhandledException.IsCompleted);

        await Services.DisposeAsync();
    }

    // A pin: when the form's own render removes the field, the form answers the departure from
    // that render's OnAfterRenderAsync, and a throw there reaches the boundary around the form as
    // any lifecycle throw does. Mutation: catch and drop what the reconcile in OnAfterRenderAsync
    // throws, and the boundary stays empty.
    [Fact]
    public async Task A_throwing_handler_during_the_form_s_own_render_reaches_the_boundary_around_it()
    {
        var (cut, engine) = await RenderWithLiveErrorAsync(attach: false, boundary: true, RootContent.Input);
        var armed = ArmOnce(engine, Surface.EditContext);

        await cut.InvokeAsync(() => cut.Render(parameters => parameters.Add(p => p.Content, RootContent.Nothing)));
        await Settle(cut);

        // The renderer hands the boundary the faulted task's AggregateException.
        Assert.False(armed());
        Assert.Contains(HandlerMessage, cut.Find("p.boundary-error").TextContent);
        Assert.False(Renderer.UnhandledException.IsCompleted);

        await Services.DisposeAsync();
    }

    // A pin: NotifyFieldSetChanged reconciles on its caller's stack, so a throw from a handler
    // reaches that caller and no boundary. Mutation: hand every reconcile's throw to the root,
    // not only a posted one's, and the call returns quietly while the boundary shows instead.
    [Fact]
    public async Task NotifyFieldSetChanged_throws_a_handler_s_throw_to_its_caller()
    {
        var (cut, engine) = await RenderWithLiveErrorAsync(attach: true, boundary: true);
        var validator = cut.FindComponent<FormidableValidator<EngineOrder>>().Instance;
        var armed = ArmOnce(engine, Surface.EditContext);

        Exception? thrown = null;
        await cut.InvokeAsync(() =>
        {
            Fire(cut, Trigger.FieldLeaves);
            thrown = Record.Exception(validator.NotifyFieldSetChanged);
        });
        await Settle(cut);

        Assert.False(armed());
        Assert.Equal(HandlerMessage, Assert.IsType<InvalidOperationException>(thrown).Message);
        Assert.Empty(cut.FindAll("p.boundary-error"));
        Assert.False(Renderer.UnhandledException.IsCompleted);

        await Services.DisposeAsync();
    }

    // Renders the host and gives the description a live error, through a committed change, so a
    // departure or a change of hold has something to take off the page.
    private async Task<(IRenderedComponent<ThrowHost> Cut, IFormidableEngine Engine)> RenderWithLiveErrorAsync(
        bool attach,
        bool boundary,
        RootContent content = RootContent.Section)
    {
        var order = new EngineOrder { Customer = new EngineCustomer() };
        var cut = Render<ThrowHost>(parameters => parameters
            .Add(p => p.Order, order)
            .Add(p => p.Attach, attach)
            .Add(p => p.Boundary, boundary)
            .Add(p => p.Content, content));
        var engine = attach
            ? cut.FindComponent<FormidableValidator<EngineOrder>>().Instance.Engine!
            : cut.FindComponent<FormidableForm<EngineOrder>>().Instance.Engine!;
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));

        cut.Find("input").Change(TooLong);
        await Settle(cut);
        await Settle(cut);
        Assert.NotEmpty(engine.EditContext.GetValidationMessages(description));

        return (cut, engine);
    }

    // Subscribes a handler that throws on its first call and does nothing after; the returned
    // reader answers whether it is still armed. The handler runs before, when given, ahead of the
    // throw.
    private static Func<bool> ArmOnce(IFormidableEngine engine, Surface surface, Action? before = null)
    {
        var armed = true;
        void Handler()
        {
            if (!armed)
            {
                return;
            }

            armed = false;
            before?.Invoke();
            throw new InvalidOperationException(HandlerMessage);
        }

        if (surface == Surface.EditContext)
        {
            engine.EditContext.OnValidationStateChanged += (_, _) => Handler();
        }
        else
        {
            engine.StateChanged += (_, _) => Handler();
        }

        return () => armed;
    }

    // Subscribes a handler that throws on every call; the returned reader answers how many calls
    // it has had.
    private static Func<int> ArmAlways(IFormidableEngine engine, Surface surface)
    {
        var calls = 0;
        void Handler()
        {
            calls++;
            throw new InvalidOperationException(HandlerMessage);
        }

        if (surface == Surface.EditContext)
        {
            engine.EditContext.OnValidationStateChanged += (_, _) => Handler();
        }
        else
        {
            engine.StateChanged += (_, _) => Handler();
        }

        return () => calls;
    }

    // Re-renders the section alone, so no render of the root follows the change.
    private static void Fire(IRenderedComponent<ThrowHost> cut, Trigger trigger)
    {
        var section = cut.FindComponent<DescriptionSection>().Instance;
        if (trigger == Trigger.FieldLeaves)
        {
            section.Hide();
        }
        else
        {
            section.Wait();
        }
    }

    private static void WithContext(SynchronizationContext context, Action act)
    {
        var ambient = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            act();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(ambient);
        }
    }

    // The dispatcher runs one piece of work at a time, so a no-op queued behind a post completes
    // only once that post has.
    private static Task Settle<TComponent>(IRenderedComponent<TComponent> cut)
        where TComponent : IComponent =>
        cut.InvokeAsync(() => { });

    /// <summary>Records every post, and runs them only when asked, on whatever context is current then.</summary>
    private sealed class CapturingContext : SynchronizationContext
    {
        private readonly List<(SendOrPostCallback Callback, object? State)> _posted = [];

        public int Count => _posted.Count;

        public override void Post(SendOrPostCallback d, object? state) => _posted.Add((d, state));

        public void RunPosted()
        {
            foreach (var (callback, state) in _posted.ToList())
            {
                callback(state);
            }
        }
    }

    /// <summary>Test-only host: an optional <see cref="ErrorBoundary"/> around a root (a form, or an <c>EditForm</c> with a validator when <see cref="Attach"/> is set) holding what <see cref="Content"/> names.</summary>
    public sealed class ThrowHost : ComponentBase
    {
        private readonly FormidableOptions _options = new() { RefreshDebounce = Timeout.InfiniteTimeSpan };
        private bool _showRoot = true;

        [Parameter]
        public EngineOrder Order { get; set; } = default!;

        [Parameter]
        public bool Attach { get; set; }

        [Parameter]
        public bool Boundary { get; set; }

        [Parameter]
        public RootContent Content { get; set; }

        // Takes the root, and the boundary around it, off the page in a render of this component.
        public void RemoveRoot()
        {
            _showRoot = false;
            StateHasChanged();
        }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            if (!_showRoot)
            {
                return;
            }

            if (!Boundary)
            {
                BuildRoot(builder);
                return;
            }

            builder.OpenComponent<ErrorBoundary>(0);
            builder.AddComponentParameter(1, nameof(ErrorBoundary.ChildContent), (RenderFragment)BuildRoot);
            builder.AddComponentParameter(2, nameof(ErrorBoundary.ErrorContent), (RenderFragment<Exception>)(exception => error =>
            {
                error.OpenElement(0, "p");
                error.AddAttribute(1, "class", "boundary-error");
                error.AddContent(2, exception.Message);
                error.CloseElement();
            }));
            builder.CloseComponent();
        }

        private void BuildRoot(RenderTreeBuilder builder)
        {
            RenderFragment<FormidableFormContext> content = _ => inner =>
            {
                inner.OpenComponent<ValidationMessage<string>>(0);
                inner.AddComponentParameter(1, "For", (Expression<Func<string>>)(() => Order.Description));
                inner.CloseComponent();

                if (Content == RootContent.Input)
                {
                    AddDescriptionInput(inner, 2, this, Order, waitForSubmit: false);
                }

                if (Content != RootContent.Section)
                {
                    return;
                }

                inner.OpenComponent<DescriptionSection>(10);
                inner.AddComponentParameter(11, nameof(DescriptionSection.Order), Order);
                inner.CloseComponent();
            };

            if (!Attach)
            {
                builder.OpenComponent<FormidableForm<EngineOrder>>(10);
                builder.AddComponentParameter(11, nameof(FormidableForm<EngineOrder>.Model), Order);
                builder.AddComponentParameter(12, nameof(FormidableForm<EngineOrder>.Options), _options);
                builder.AddComponentParameter(13, nameof(FormidableForm<EngineOrder>.ChildContent), content);
                builder.CloseComponent();
                return;
            }

            builder.OpenComponent<EditForm>(20);
            builder.AddComponentParameter(21, nameof(EditForm.Model), Order);
            builder.AddComponentParameter(22, nameof(EditForm.ChildContent), (RenderFragment<EditContext>)(_ => inner =>
            {
                inner.OpenComponent<FormidableValidator<EngineOrder>>(0);
                inner.AddComponentParameter(1, nameof(FormidableValidator<EngineOrder>.Options), _options);
                inner.AddComponentParameter(2, nameof(FormidableValidator<EngineOrder>.ChildContent), content);
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        }
    }

    /// <summary>Test-only component inside the root: the description's input, until <see cref="Hide"/> takes it away or <see cref="Wait"/> renders it waiting for submit, each in a render of this component alone.</summary>
    public sealed class DescriptionSection : ComponentBase
    {
        private bool _showing = true;
        private bool _waiting;

        [Parameter]
        public EngineOrder Order { get; set; } = default!;

        public void Hide()
        {
            _showing = false;
            StateHasChanged();
        }

        public void Wait()
        {
            _waiting = true;
            StateHasChanged();
        }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            if (_showing)
            {
                AddDescriptionInput(builder, 0, this, Order, _waiting);
            }
        }
    }

    // The description's input, bound as @bind-Value binds it. It takes six sequence numbers.
    private static void AddDescriptionInput(RenderTreeBuilder builder, int sequence, object receiver, EngineOrder order, bool waitForSubmit)
    {
        builder.OpenComponent<FormidableInputText>(sequence);
        builder.AddComponentParameter(sequence + 1, "For", (Expression<Func<string?>>)(() => order.Description));
        builder.AddComponentParameter(sequence + 2, "Value", order.Description);
        builder.AddComponentParameter(sequence + 3, "ValueChanged",
            EventCallback.Factory.Create<string?>(receiver, v => order.Description = v ?? string.Empty));
        builder.AddComponentParameter(sequence + 4, "WaitForSubmit", waitForSubmit);
        builder.CloseComponent();
    }
}
