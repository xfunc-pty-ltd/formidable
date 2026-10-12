using Bunit;
using FluentValidation;
using Formidable.Blazor.Tests.Fixtures;
using Formidable.Introspection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Formidable.Blazor.Tests;

/// <summary>
/// A consumer's <see cref="IFormidableEngine.StateChanged"/> or <c>EditContext.OnValidationStateChanged</c>
/// handler throws while the form checks a change, runs a validity check or fires a timer: work
/// that no caller awaits. Both roots hand the throw to the nearest <see cref="ErrorBoundary"/>
/// above them, or, with none, to the renderer's own unhandled path, as they hand on a posted
/// callback's throw. An engine built without a root has nowhere to hand it, so the throw stays on
/// the task, unobserved, and reaches <see cref="TaskScheduler.UnobservedTaskException"/>.
/// </summary>
public class FormidableEngineHandlerThrowTests : BunitContext
{
    private static readonly string TooLong = new('x', 11);
    private readonly FakeTimeProvider _clock = new();

    public FormidableEngineHandlerThrowTests()
    {
        // Attach mode resolves the three JS-backed services; these doubles go in ahead of
        // AddFormidableBlazor, whose registrations are TryAdds.
        Services.AddSingleton<TimeProvider>(_clock);
        Services.AddSingleton<IFormidableFocusService>(new RecordingFocusService());
        Services.AddSingleton<IFormidableFieldOrderService>(new RecordingFieldOrderService());
        Services.AddSingleton<IFormidableDomValueSync>(new RecordingDomValueSync());
        Services.AddFormidableBlazor();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    /// <summary>Where the engine calls the throwing handler from.</summary>
    public enum Site
    {
        // The live check a committed change starts, with no LiveDebounce.
        LivePass,

        // The TrackFormValidity check a committed change starts beside that live check.
        EditValidityCheck,

        // The LiveDebounce timer's fire, which runs the window's live check.
        LiveDebounceFire,

        // The RefreshDebounce timer's fire after a submit, which runs the whole-form re-check.
        RefreshFire,

        // The TrackFormValidity check the engine starts as it is built, held on an async rule
        // until the handler is subscribed.
        FirstValidityCheck,

        // The TrackFormValidity check a submit's end starts when an edit during the submit made its
        // answer out of date, under a RefreshDebounce that never passes.
        CheckBehindAnOutdatedAnswer,

        // The validity timer's check, armed by a change under a LiveDebounce that never closes.
        ValidityTimerFire,

        // The TrackFormValidity check the LiveDebounce timer's fire starts beside its live check.
        LiveDebounceFireCheck,

        // Control: the notification the engine posts when a field starts waiting for submit, which
        // reaches the root through a catch of its own rather than through a task.
        HeldPublish,
    }

    public enum Surface
    {
        // IFormidableEngine.StateChanged.
        Engine,

        // EditContext.OnValidationStateChanged.
        EditContext,
    }

    // Mutation: discard the task of the live check HandleFieldChanged starts, and the boundary
    // stays empty while the throw reaches UnobservedTaskException instead.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_throw_during_a_live_pass_reaches_the_boundary(bool attach)
    {
        var outcome = await RunAsync(Site.LivePass, attach: attach);

        outcome.AssertReachedTheBoundary();
    }

    // With no boundary above the root, the root's hand-off reaches the renderer's own unhandled
    // path, where Blazor Server ends the circuit and WebAssembly shows its error UI: bUnit
    // completes Renderer.UnhandledException with the handler's exception. The hand-off lands once
    // the change event has returned, so the code that fired the change sees no exception (bUnit
    // rethrows one that reaches the renderer while the event runs). Mutation: as above, and the
    // renderer never hears of the throw; hand it on inline instead, and the trigger meets it.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_throw_during_a_live_pass_with_no_boundary_reaches_the_renderer(bool attach)
    {
        var outcome = await RunAsync(Site.LivePass, boundary: false, attach: attach);

        outcome.AssertReached(boundary: null, renderer: outcome.Marker, trigger: null);
    }

    // Mutation: discard the task of the validity check HandleFieldChanged starts.
    [Fact]
    public async Task A_throw_during_the_validity_probe_reaches_the_boundary()
    {
        var outcome = await RunAsync(Site.EditValidityCheck);

        outcome.AssertReachedTheBoundary();
    }

    // Mutation: discard the task ArmTimer's callback dispatches.
    [Fact]
    public async Task A_throw_during_a_live_debounce_fire_reaches_the_boundary()
    {
        var outcome = await RunAsync(Site.LiveDebounceFire);

        outcome.AssertReachedTheBoundary();
    }

    // Mutation: as above; the refresh is the other timer behind the same callback.
    [Fact]
    public async Task A_throw_during_a_refresh_fire_reaches_the_boundary()
    {
        var outcome = await RunAsync(Site.RefreshFire);

        outcome.AssertReachedTheBoundary();
    }

    // The engine raises OnValidationStateChanged beside StateChanged as the live check begins.
    // Mutation: as for the live pass above.
    [Fact]
    public async Task A_throw_from_OnValidationStateChanged_reaches_the_boundary()
    {
        var outcome = await RunAsync(Site.LivePass, surface: Surface.EditContext);

        outcome.AssertReachedTheBoundary();
    }

    // The remaining validity checks the engine starts without awaiting. Mutation: discard the task
    // of any one of them (the constructor's, RecheckFormValidity's, RunValidityCheckAsync's or
    // RunDebouncedLivePassAsync's), and its row alone fails.
    [Theory]
    [InlineData(Site.FirstValidityCheck)]
    [InlineData(Site.CheckBehindAnOutdatedAnswer)]
    [InlineData(Site.ValidityTimerFire)]
    [InlineData(Site.LiveDebounceFireCheck)]
    public async Task A_throw_during_any_other_validity_check_reaches_the_boundary(Site site)
    {
        var outcome = await RunAsync(site);

        outcome.AssertReachedTheBoundary();
    }

    // A pin: the notification the engine posts as a field starts waiting for submit reaches the
    // boundary through its own catch, beside the checks and timers that go through a task.
    [Fact]
    public async Task A_throw_during_a_posted_hold_notification_reaches_the_boundary()
    {
        var outcome = await RunAsync(Site.HeldPublish);

        outcome.AssertReachedTheBoundary();
    }

    /// <summary>Engine work that goes on after the work it started has thrown.</summary>
    public enum WorkAfterTheThrow
    {
        // LiveDebounce and TrackFormValidity: the validity check the window's fire starts throws,
        // and the fire goes on to start its live check.
        LiveDebounceFire,

        // TrackFormValidity: page code outside any DOM event commits a change, the live check it
        // starts throws, and the change goes on to start its validity check.
        ChangeFromPageCode,
    }

    // The boundary takes the throw only once the engine call that started the throwing work has
    // returned, so the boundary's removal of the form (and the engine's disposal with it) never
    // lands in the middle of that call. Nothing the engine raises comes after the boundary took the
    // throw, and no check runs on a disposed engine (which would raise ValidationFaulted with an
    // ObjectDisposedException). Mutation: hand the throw to the root inline, inside the
    // continuation, and the LiveDebounceFire row sees StateChanged from a live check run after the
    // boundary, the ChangeFromPageCode row a ValidationFaulted from a check on the disposed engine.
    [Theory]
    [InlineData(WorkAfterTheThrow.LiveDebounceFire)]
    [InlineData(WorkAfterTheThrow.ChangeFromPageCode)]
    public async Task Nothing_reaches_the_page_after_the_boundary_takes_the_throw(WorkAfterTheThrow work)
    {
        var options = work == WorkAfterTheThrow.LiveDebounceFire
            ? new FormidableOptions { TrackFormValidity = true, LiveDebounce = TimeSpan.FromMilliseconds(50), RefreshDebounce = Timeout.InfiniteTimeSpan }
            : new FormidableOptions { TrackFormValidity = true, RefreshDebounce = Timeout.InfiniteTimeSpan };
        var order = new EngineOrder { Customer = new EngineCustomer() };
        var cut = Render<ThrowSiteHost>(p => p
            .Add(x => x.Order, order)
            .Add(x => x.Options, options)
            .Add(x => x.Validator, new FluentValidationModelValidator<EngineOrder>(new EngineOrderValidator()))
            .Add(x => x.Boundary, true));
        var engine = cut.FindComponent<FormidableForm<EngineOrder>>().Instance.Engine!;
        var boundary = cut.FindComponent<RecordingBoundary>().Instance;
        var afterTheBoundary = new List<string>();
        var faults = new List<string>();
        engine.StateChanged += (_, _) =>
        {
            if (boundary.Took)
            {
                afterTheBoundary.Add(nameof(IFormidableEngine.StateChanged));
            }
        };
        engine.ValidationFaulted += (_, e) => faults.Add(e.Exception.GetType().Name);
        var marker = $"{work}-{Guid.NewGuid():N}";
        var armed = ArmOnce(
            engine,
            Surface.Engine,
            work == WorkAfterTheThrow.LiveDebounceFire ? "ProbeFormValidityAsync" : "RunLivePassAsync",
            marker);

        if (work == WorkAfterTheThrow.LiveDebounceFire)
        {
            await cut.InvokeAsync(() => cut.Find("input").Change("ok"));
            await cut.InvokeAsync(() => _clock.Advance(TimeSpan.FromMilliseconds(51)));
        }
        else
        {
            await cut.InvokeAsync(() =>
            {
                order.Description = TooLong;
                engine.EditContext.NotifyFieldChanged(new FieldIdentifier(order, nameof(EngineOrder.Description)));
            });
        }

        await UntilAsync(() => !armed());
        for (var i = 0; i < 5; i++)
        {
            await Settle(cut);
        }

        var boundaryText = cut.FindAll("p.boundary-error").Select(p => p.TextContent).SingleOrDefault();
        Assert.Equal(
            (marker, string.Empty, string.Empty),
            (boundaryText, string.Join(",", afterTheBoundary), string.Join(",", faults)));

        await Services.DisposeAsync();
    }

    // Attach mode builds its engine over the page's own EditContext, which can already carry the
    // page's handler. With a synchronous validator the engine's first validity check lands inside
    // its constructor, so the root's route has to be in place before that check starts. Mutation:
    // assign the route as the constructor's last statement, after the check has started, and the
    // boundary stays empty while the throw reaches UnobservedTaskException instead.
    [Fact]
    public async Task A_throw_while_attach_mode_builds_the_engine_reaches_the_boundary()
    {
        var order = new EngineOrder { Customer = new EngineCustomer() };
        var editContext = new EditContext(order);
        var marker = $"construction-{Guid.NewGuid():N}";
        var stackAtThrow = string.Empty;
        editContext.OnValidationStateChanged += (_, _) =>
        {
            var stack = Environment.StackTrace;
            if (stackAtThrow.Length > 0 || !stack.Contains("ProbeFormValidityAsync", StringComparison.Ordinal))
            {
                return;
            }

            stackAtThrow = stack;
            throw new InvalidOperationException(marker);
        };

        using var unobserved = new UnobservedCapture(marker);
        IRenderedComponent<PageContextAttachHost>? cut = null;
        var thrown = Record.Exception(() => cut = Render<PageContextAttachHost>(p => p.Add(x => x.Context, editContext)));
        Assert.Null(thrown);
        await Settle(cut!);
        var boundaryText = cut!.FindAll("p.boundary-error").Select(p => p.TextContent).SingleOrDefault();
        var unobservedCount = await unobserved.CountAfterAsync(collections: 2);

        // The throw came from the check the constructor started, not from a later one. Then one
        // comparison: what the boundary shows, whether the renderer's unhandled path took the
        // throw, and the unobserved count.
        Assert.Contains("FormidableEngine`1..ctor", stackAtThrow, StringComparison.Ordinal);
        Assert.Equal((marker, false, 0), (boundaryText, Renderer.UnhandledException.IsCompleted, unobservedCount));

        await Services.DisposeAsync();
    }

    // An engine a component builds itself, under a boundary, has no root to hand a throw to. The
    // throw stays on the live check's task: the change notification returns quietly, neither the
    // boundary nor the renderer hears of it, and it surfaces as an unobserved task exception once
    // the task is collected. Mutation: hand the throw on, or observe the task, when no root set a
    // route, and no unobserved exception carries it.
    [Fact]
    public async Task An_engine_without_a_root_keeps_the_throw_off_any_boundary()
    {
        var order = new EngineOrder { Customer = new EngineCustomer() };
        var cut = Render<DirectEngineHost>(p => p.Add(x => x.Order, order));
        var engine = cut.FindComponent<DirectEngineComponent>().Instance.Engine!;
        var marker = $"no-root-{Guid.NewGuid():N}";
        var armed = ArmOnce(engine, Surface.Engine, "RunLivePassAsync", marker);

        using var unobserved = new UnobservedCapture(marker);
        var thrown = await Record.ExceptionAsync(() => cut.InvokeAsync(() =>
        {
            order.Description = TooLong;
            engine.EditContext.NotifyFieldChanged(new FieldIdentifier(order, nameof(EngineOrder.Description)));
        }));
        await Settle(cut);

        Assert.Null(thrown);
        Assert.False(armed());
        Assert.Empty(cut.FindAll("p.boundary-error"));
        Assert.False(Renderer.UnhandledException.IsCompleted);
        Assert.True(await unobserved.SeenWithinAsync(collections: 10));

        await Services.DisposeAsync();
    }

    private static FormidableOptions OptionsFor(Site site) => site switch
    {
        Site.EditValidityCheck => new() { TrackFormValidity = true, RefreshDebounce = Timeout.InfiniteTimeSpan },
        Site.LiveDebounceFire => new() { LiveDebounce = TimeSpan.FromMilliseconds(50), RefreshDebounce = Timeout.InfiniteTimeSpan },
        Site.RefreshFire => new() { RefreshDebounce = TimeSpan.FromMilliseconds(300) },
        Site.FirstValidityCheck => new() { TrackFormValidity = true, RefreshDebounce = Timeout.InfiniteTimeSpan },
        Site.CheckBehindAnOutdatedAnswer => new() { TrackFormValidity = true, RefreshDebounce = Timeout.InfiniteTimeSpan },
        Site.ValidityTimerFire => new() { TrackFormValidity = true, LiveDebounce = Timeout.InfiniteTimeSpan, RefreshDebounce = TimeSpan.FromMilliseconds(300) },
        Site.LiveDebounceFireCheck => new() { TrackFormValidity = true, LiveDebounce = TimeSpan.FromMilliseconds(50), RefreshDebounce = Timeout.InfiniteTimeSpan },
        _ => new() { RefreshDebounce = Timeout.InfiniteTimeSpan },
    };

    // A frame only the site's own work puts on the stack when the handler runs: a method of that
    // work, or a lambda the compiler names after it. The handler throws on its first call from
    // such a stack, so a notification any other work raises first passes it by.
    private static string StackMarkerFor(Site site) => site switch
    {
        Site.LivePass => "RunLivePassAsync",
        Site.LiveDebounceFire => "ArmTimer",
        Site.RefreshFire => "RunRefreshPassAsync",
        Site.HeldPublish => "RunHeldStatePublish",
        _ => "ProbeFormValidityAsync",
    };

    // Renders the root under an optional boundary, brings the form to where the site's work can
    // start, arms the handler, starts the work, and reads where the throw went.
    private async Task<Outcome> RunAsync(Site site, bool boundary = true, Surface surface = Surface.Engine, bool attach = false)
    {
        var gated = site is Site.FirstValidityCheck or Site.CheckBehindAnOutdatedAnswer
            ? new GatedValidator { ShouldPass = true }
            : null;
        var order = new EngineOrder { Customer = new EngineCustomer() };
        var cut = Render<ThrowSiteHost>(p => p
            .Add(x => x.Order, order)
            .Add(x => x.Options, OptionsFor(site))
            .Add(x => x.Validator, new FluentValidationModelValidator<EngineOrder>(gated ?? (IValidator<EngineOrder>)new EngineOrderValidator()))
            .Add(x => x.Boundary, boundary)
            .Add(x => x.Attach, attach));
        var engine = attach
            ? cut.FindComponent<FormidableValidator<EngineOrder>>().Instance.Engine!
            : cut.FindComponent<FormidableForm<EngineOrder>>().Instance.Engine!;
        Task<SubmitOutcome>? submit = null;

        switch (site)
        {
            case Site.HeldPublish:
                // A live message for the hold to take off the page.
                await cut.InvokeAsync(() => cut.Find("input").Change(TooLong));
                await Settle(cut);
                await Settle(cut);
                Assert.NotEmpty(engine.EditContext.GetValidationMessages(new FieldIdentifier(order, nameof(EngineOrder.Description))));
                break;
            case Site.RefreshFire:
                // The first render's re-check, then a submit, so a later change arms the refresh.
                await cut.InvokeAsync(() => _clock.Advance(TimeSpan.FromMilliseconds(301)));
                await Settle(cut);
                await cut.InvokeAsync(() => cut.Find("form").Submit());
                await UntilAsync(() => engine.HasSubmitted && !engine.IsValidating);
                break;
            case Site.ValidityTimerFire:
                // The first render's re-check, so the timer that fires below is the validity
                // check's.
                await cut.InvokeAsync(() => _clock.Advance(TimeSpan.FromMilliseconds(301)));
                await Settle(cut);
                break;
            case Site.CheckBehindAnOutdatedAnswer:
                // The first check lands, then a submit's rule waits on a fresh gate, and the next
                // answer, the check's, comes back changed so it has something to tell.
                await cut.InvokeAsync(() => gated!.Gate.SetResult());
                await UntilAsync(() => engine.IsFormValid);
                gated!.Reset();
                gated.ShouldPass = false;
                await cut.InvokeAsync(() => { submit = engine.ValidateForSubmitAsync(); });
                await UntilAsync(() => gated.Started == 2);
                break;
        }

        var marker = $"{site}-{Guid.NewGuid():N}";
        var armed = ArmOnce(engine, surface, StackMarkerFor(site), marker);
        using var unobserved = new UnobservedCapture(marker);

        // bUnit rethrows, to the code that fired an event, an exception that reached the
        // renderer's unhandled path while the event ran, so the trigger's own exception is part
        // of the outcome: it shows whether a hand-off landed inside the event.
        async Task TriggerAsync()
        {
            switch (site)
            {
                case Site.LivePass:
                    await cut.InvokeAsync(() => cut.Find("input").Change(TooLong));
                    break;
                case Site.EditValidityCheck:
                    await cut.InvokeAsync(() => cut.Find("input").Change("ok"));
                    break;
                case Site.LiveDebounceFire:
                    await cut.InvokeAsync(() => cut.Find("input").Change(TooLong));
                    await cut.InvokeAsync(() => _clock.Advance(TimeSpan.FromMilliseconds(51)));
                    break;
                case Site.RefreshFire:
                    await cut.InvokeAsync(() => cut.Find("input").Change("ok"));
                    await cut.InvokeAsync(() => _clock.Advance(TimeSpan.FromMilliseconds(301)));
                    break;
                case Site.FirstValidityCheck:
                    await cut.InvokeAsync(() => gated!.Gate.SetResult());
                    break;
                case Site.CheckBehindAnOutdatedAnswer:
                    // The change's own live check and validity check stand down for the
                    // submit, and the re-check it arms never fires, so the submit's end
                    // starts the check.
                    await cut.InvokeAsync(() => cut.Find("input").Change("ok"));
                    await cut.InvokeAsync(() => gated!.Gate.SetResult());
                    break;
                case Site.ValidityTimerFire:
                    await cut.InvokeAsync(() => cut.Find("input").Change("ok"));
                    await cut.InvokeAsync(() => _clock.Advance(TimeSpan.FromMilliseconds(301)));
                    break;
                case Site.LiveDebounceFireCheck:
                    await cut.InvokeAsync(() => cut.Find("input").Change("ok"));
                    await cut.InvokeAsync(() => _clock.Advance(TimeSpan.FromMilliseconds(51)));
                    break;
                default:
                    await cut.InvokeAsync(() => cut.FindComponent<HandlerThrowBoundaryTests.DescriptionSection>().Instance.Wait());
                    break;
            }
        }

        var triggerThrow = await Record.ExceptionAsync(TriggerAsync);
        await UntilAsync(() => !armed());
        for (var i = 0; i < 5; i++)
        {
            await Settle(cut);
        }

        if (submit is not null)
        {
            await submit;
        }

        var outcome = new Outcome(
            marker,
            armed(),
            cut.FindAll("p.boundary-error").Select(p => p.TextContent).SingleOrDefault(),
            Renderer.UnhandledException.IsCompleted ? await Renderer.UnhandledException : null,
            triggerThrow,
            await unobserved.CountAfterAsync(collections: 2));

        await Services.DisposeAsync();
        return outcome;
    }

    // Subscribes a handler that throws, carrying the marker, on its first call from a stack that
    // holds the stack marker, and does nothing after; the returned reader answers whether it is
    // still armed.
    private static Func<bool> ArmOnce(IFormidableEngine engine, Surface surface, string stackMarker, string marker)
    {
        var armed = true;
        void Handler()
        {
            if (!armed || !Environment.StackTrace.Contains(stackMarker, StringComparison.Ordinal))
            {
                return;
            }

            armed = false;
            throw new InvalidOperationException(marker);
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

    // Polls, because the state these waits read moves on work that need not render anything.
    private static async Task UntilAsync(Func<bool> condition)
    {
        for (var waited = 0; waited < 500 && !condition(); waited++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    // The dispatcher runs one piece of work at a time, so a no-op queued behind other work
    // completes only once that work has.
    private static Task Settle<TComponent>(IRenderedComponent<TComponent> cut)
        where TComponent : IComponent =>
        cut.InvokeAsync(() => { });

    /// <summary>Where one handler throw went.</summary>
    private sealed record Outcome(string Marker, bool Armed, string? BoundaryText, Exception? RendererUnhandled, Exception? TriggerThrow, int Unobserved)
    {
        // The handler threw, the boundary shows its exception, the renderer's unhandled path never
        // took it, and no unobserved task exception carried it after two forced collections.
        public void AssertReachedTheBoundary() => AssertReached(boundary: Marker, renderer: null, trigger: null);

        // One comparison, so a failure prints all five facts: whether the handler threw, what the
        // boundary shows, what reached the renderer's unhandled path, what the trigger threw, and
        // the unobserved count.
        public void AssertReached(string? boundary, string? renderer, string? trigger) =>
            Assert.Equal(
                (true, boundary, renderer, trigger, 0),
                (!Armed, BoundaryText, RendererUnhandled?.Message, TriggerThrow?.Message, Unobserved));
    }

    /// <summary>Counts the unobserved task exceptions that carry a marker, from construction until disposal.</summary>
    private sealed class UnobservedCapture : IDisposable
    {
        private readonly string _marker;
        private int _count;

        public UnobservedCapture(string marker)
        {
            _marker = marker;
            TaskScheduler.UnobservedTaskException += OnUnobserved;
        }

        // Forces the given number of collections, so a faulted task nothing references raises its
        // event, and answers how many carried the marker.
        public async Task<int> CountAfterAsync(int collections)
        {
            for (var i = 0; i < collections; i++)
            {
                await CollectAsync();
            }

            return Volatile.Read(ref _count);
        }

        // Forces collections until one carrying the marker is seen, up to the given number.
        public async Task<bool> SeenWithinAsync(int collections)
        {
            for (var i = 0; i < collections && Volatile.Read(ref _count) == 0; i++)
            {
                await CollectAsync();
            }

            return Volatile.Read(ref _count) > 0;
        }

        public void Dispose() => TaskScheduler.UnobservedTaskException -= OnUnobserved;

        private static async Task CollectAsync()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            await Task.Delay(50);
        }

        private static bool Carries(Exception? exception, string marker) =>
            exception is not null
            && (exception.Message.Contains(marker, StringComparison.Ordinal)
                || (exception is AggregateException aggregate && aggregate.InnerExceptions.Any(inner => Carries(inner, marker)))
                || Carries(exception.InnerException, marker));

        private void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            if (Carries(e.Exception, _marker))
            {
                Interlocked.Increment(ref _count);
                e.SetObserved();
            }
        }
    }

    /// <summary>Test-only <see cref="ErrorBoundary"/> that records the moment it takes an exception, before it renders its error content in its child content's place.</summary>
    public sealed class RecordingBoundary : ErrorBoundary
    {
        public bool Took { get; private set; }

        protected override Task OnErrorAsync(Exception exception)
        {
            Took = true;
            return base.OnErrorAsync(exception);
        }
    }

    /// <summary>Test-only host: an optional <see cref="ErrorBoundary"/> around a root (a form, or an <c>EditForm</c> with a validator when <see cref="Attach"/> is set) holding a <see cref="HandlerThrowBoundaryTests.DescriptionSection"/>.</summary>
    public sealed class ThrowSiteHost : ComponentBase
    {
        [Parameter]
        public EngineOrder Order { get; set; } = default!;

        [Parameter]
        public FormidableOptions Options { get; set; } = default!;

        [Parameter]
        public IModelValidator<EngineOrder> Validator { get; set; } = default!;

        [Parameter]
        public bool Boundary { get; set; }

        [Parameter]
        public bool Attach { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            if (!Boundary)
            {
                BuildRoot(builder);
                return;
            }

            builder.OpenComponent<RecordingBoundary>(0);
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
                inner.OpenComponent<HandlerThrowBoundaryTests.DescriptionSection>(0);
                inner.AddComponentParameter(1, nameof(HandlerThrowBoundaryTests.DescriptionSection.Order), Order);
                inner.CloseComponent();
                inner.AddMarkupContent(2, "<button type=\"submit\">Save</button>");
            };

            if (!Attach)
            {
                builder.OpenComponent<FormidableForm<EngineOrder>>(10);
                builder.AddComponentParameter(11, nameof(FormidableForm<EngineOrder>.Model), Order);
                builder.AddComponentParameter(12, nameof(FormidableForm<EngineOrder>.Options), Options);
                builder.AddComponentParameter(13, nameof(FormidableForm<EngineOrder>.Validator), Validator);
                builder.AddComponentParameter(14, nameof(FormidableForm<EngineOrder>.ChildContent), content);
                builder.CloseComponent();
                return;
            }

            builder.OpenComponent<EditForm>(20);
            builder.AddComponentParameter(21, nameof(EditForm.Model), Order);
            builder.AddComponentParameter(22, nameof(EditForm.ChildContent), (RenderFragment<EditContext>)(_ => inner =>
            {
                inner.OpenComponent<FormidableValidator<EngineOrder>>(0);
                inner.AddComponentParameter(1, nameof(FormidableValidator<EngineOrder>.Options), Options);
                inner.AddComponentParameter(2, nameof(FormidableValidator<EngineOrder>.Validator), Validator);
                inner.AddComponentParameter(3, nameof(FormidableValidator<EngineOrder>.ChildContent), content);
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        }
    }

    /// <summary>Test-only host: an <see cref="ErrorBoundary"/> around an <c>EditForm</c> over the page's own <see cref="EditContext"/>, holding a validator that tracks form validity over a synchronous validator.</summary>
    public sealed class PageContextAttachHost : ComponentBase
    {
        private readonly FormidableOptions _options = new() { TrackFormValidity = true, RefreshDebounce = Timeout.InfiniteTimeSpan };

        [Parameter]
        public EditContext Context { get; set; } = default!;

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<ErrorBoundary>(0);
            builder.AddComponentParameter(1, nameof(ErrorBoundary.ChildContent), (RenderFragment)(form =>
            {
                form.OpenComponent<EditForm>(0);
                form.AddComponentParameter(1, nameof(EditForm.EditContext), Context);
                form.AddComponentParameter(2, nameof(EditForm.ChildContent), (RenderFragment<EditContext>)(_ => inner =>
                {
                    inner.OpenComponent<FormidableValidator<EngineOrder>>(0);
                    inner.AddComponentParameter(1, nameof(FormidableValidator<EngineOrder>.Options), _options);
                    inner.AddComponentParameter(2, nameof(FormidableValidator<EngineOrder>.Validator),
                        (IModelValidator<EngineOrder>)new FluentValidationModelValidator<EngineOrder>(new EngineOrderValidator()));
                    inner.AddComponentParameter(3, nameof(FormidableValidator<EngineOrder>.ChildContent),
                        (RenderFragment<FormidableFormContext>)(_ => _ => { }));
                    inner.CloseComponent();
                }));
                form.CloseComponent();
            }));
            builder.AddComponentParameter(2, nameof(ErrorBoundary.ErrorContent), (RenderFragment<Exception>)(exception => error =>
            {
                error.OpenElement(0, "p");
                error.AddAttribute(1, "class", "boundary-error");
                error.AddContent(2, exception.Message);
                error.CloseElement();
            }));
            builder.CloseComponent();
        }
    }

    /// <summary>Test-only host: an <see cref="ErrorBoundary"/> around a <see cref="DirectEngineComponent"/>.</summary>
    public sealed class DirectEngineHost : ComponentBase
    {
        [Parameter]
        public EngineOrder Order { get; set; } = default!;

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<ErrorBoundary>(0);
            builder.AddComponentParameter(1, nameof(ErrorBoundary.ChildContent), (RenderFragment)(inner =>
            {
                inner.OpenComponent<DirectEngineComponent>(0);
                inner.AddComponentParameter(1, nameof(DirectEngineComponent.Order), Order);
                inner.CloseComponent();
            }));
            builder.AddComponentParameter(2, nameof(ErrorBoundary.ErrorContent), (RenderFragment<Exception>)(exception => error =>
            {
                error.OpenElement(0, "p");
                error.AddAttribute(1, "class", "boundary-error");
                error.AddContent(2, exception.Message);
                error.CloseElement();
            }));
            builder.CloseComponent();
        }
    }

    /// <summary>Test-only component that builds an engine through the public constructor, dispatching on its own renderer, as a page with no Formidable root could.</summary>
    public sealed class DirectEngineComponent : ComponentBase, IDisposable
    {
        [Parameter]
        public EngineOrder Order { get; set; } = default!;

        [Inject]
        private TimeProvider Clock { get; set; } = default!;

        public FormidableEngine<EngineOrder>? Engine { get; private set; }

        public void Dispose() => Engine?.Dispose();

        protected override void OnInitialized() =>
            Engine = new FormidableEngine<EngineOrder>(
                Order,
                new EditContext(Order),
                new FluentValidationModelValidator<EngineOrder>(new EngineOrderValidator()),
                new ReflectionModelIntrospector(),
                new FormidableOptions { RefreshDebounce = Timeout.InfiniteTimeSpan },
                Clock,
                work => InvokeAsync(work));
    }
}
