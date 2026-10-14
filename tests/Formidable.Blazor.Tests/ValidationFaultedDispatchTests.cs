using System.Linq.Expressions;
using Bunit;
using FluentValidation;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Formidable.Blazor.Tests;

/// <summary>
/// <see cref="IFormidableEngine.ValidationFaulted"/> runs on the renderer's dispatcher on both
/// routes that raise it: a check that files the form's fault message (a live check here), and the
/// validity check, which files none. The rule throws after an await the test releases from its own
/// thread, so the check resumes off the dispatcher before it reaches the event. A handler that
/// renders then works, and a handler that throws reaches the boundary around the form. Every case
/// runs under <see cref="FormidableForm{TModel}"/> and, in attach mode, under
/// <see cref="FormidableValidator{TModel}"/> in a page's <c>EditForm</c>: both hand the engine the
/// same dispatch.
/// </summary>
public class ValidationFaultedDispatchTests : BunitContext
{
    private const string RuleThrew = "The rule threw.";

    private readonly FakeTimeProvider _clock = new();

    public ValidationFaultedDispatchTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<TimeProvider>(_clock);
        Services.AddFormidableBlazor();
    }

    /// <summary>Which check throws.</summary>
    public enum Route
    {
        // The live check an edit starts, which files the form's fault message and then raises the
        // event.
        LiveCheck,

        // The TrackFormValidity check the engine starts as it is built, which files no message and
        // raises the event as it exits.
        ValidityCheck,
    }

    public sealed class Account
    {
        public string? Name { get; set; } = "Ann";
    }

    /// <summary>One async rule on Name that waits on a gate and then throws.</summary>
    public sealed class ThrowingValidator : AbstractValidator<Account>
    {
        private int _started;

        public ThrowingValidator() =>
            RuleFor(m => m.Name).MustAsync(async (_, _) =>
            {
                Interlocked.Increment(ref _started);
                await Gate.Task;
                throw new InvalidOperationException(RuleThrew);
            });

        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Started => Volatile.Read(ref _started);
    }

    // The handler reads where it runs and renders a banner through StateHasChanged, as a page's
    // handler would. Mutation: raise the event after the dispatch on either route (ReportFaultAsync's
    // or the validity check's fault exit), and that route's rows (both roots) run the handler off
    // the dispatcher: it reads false, StateHasChanged throws, the banner stays empty and the throw
    // reaches the renderer.
    [Theory]
    [InlineData(Route.LiveCheck, false)]
    [InlineData(Route.ValidityCheck, false)]
    [InlineData(Route.LiveCheck, true)]
    [InlineData(Route.ValidityCheck, true)]
    public async Task A_handler_runs_on_the_dispatcher_and_can_render(Route route, bool attach)
    {
        var (cut, validator) = await StartAsync(route, boundary: false, attach);
        var host = cut.Instance;
        var engine = EngineOf(cut, attach);
        bool? onDispatcher = null;
        engine.ValidationFaulted += (_, e) =>
        {
            onDispatcher = Renderer.Dispatcher.CheckAccess();
            host.ShowFault(e.Exception.Message);
        };

        validator.Gate.TrySetResult(); // from this thread, not the dispatcher
        await UntilAsync(() => onDispatcher is not null);
        await SettleAsync(cut);

        var (banner, visible) = await cut.InvokeAsync(() => (cut.Find("p.fault").TextContent, engine.GetVisibleIssues().Count));
        Assert.Equal(
            (true, RuleThrew, (string?)null, route == Route.LiveCheck ? 1 : 0),
            (onDispatcher, banner, Renderer.UnhandledException.IsCompleted ? (await Renderer.UnhandledException).Message : null, visible));

        await Services.DisposeAsync();
    }

    // A handler that throws reaches the boundary around the form on either route, under either root,
    // and never the renderer's own unhandled path. Mutation: catch the dispatch's exception in the
    // engine, and the boundary stays empty.
    [Theory]
    [InlineData(Route.LiveCheck, false)]
    [InlineData(Route.ValidityCheck, false)]
    [InlineData(Route.LiveCheck, true)]
    [InlineData(Route.ValidityCheck, true)]
    public async Task A_throwing_handler_reaches_the_boundary(Route route, bool attach)
    {
        var (cut, validator) = await StartAsync(route, boundary: true, attach);
        var engine = EngineOf(cut, attach);
        var marker = $"{route}-{attach}-{Guid.NewGuid():N}";
        var calls = 0;
        engine.ValidationFaulted += (_, _) =>
        {
            Interlocked.Increment(ref calls);
            throw new InvalidOperationException(marker);
        };

        validator.Gate.TrySetResult();
        await UntilAsync(() => Volatile.Read(ref calls) > 0);

        // The throw reaches the boundary through a hand-off posted past the call stack, which can
        // run from a pool thread, so wait for where it lands rather than for a count of settles.
        cut.WaitForState(
            () => cut.FindAll("p.boundary-error").Count > 0 || Renderer.UnhandledException.IsCompleted,
            TimeSpan.FromSeconds(5));
        await SettleAsync(cut);

        var boundaryText = await cut.InvokeAsync(() => cut.FindAll("p.boundary-error").Select(p => p.TextContent).SingleOrDefault());
        Assert.Equal(
            (1, marker, false),
            (Volatile.Read(ref calls), boundaryText, Renderer.UnhandledException.IsCompleted));

        await Services.DisposeAsync();
    }

    private static IFormidableEngine EngineOf(IRenderedComponent<FaultHost> cut, bool attach) =>
        attach
            ? cut.FindComponent<FormidableValidator<Account>>().Instance.Engine!
            : cut.FindComponent<FormidableForm<Account>>().Instance.Engine!;

    // Renders the host and brings the route's check to the rule's gate.
    private async Task<(IRenderedComponent<FaultHost> Cut, ThrowingValidator Validator)> StartAsync(Route route, bool boundary, bool attach)
    {
        var validator = new ThrowingValidator();
        var options = new FormidableOptions
        {
            RefreshDebounce = Timeout.InfiniteTimeSpan,
            TrackFormValidity = route == Route.ValidityCheck,
        };
        var cut = Render<FaultHost>(p => p
            .Add(x => x.Model, new Account())
            .Add(x => x.Options, options)
            .Add(x => x.Validator, new FluentValidationModelValidator<Account>(validator))
            .Add(x => x.Boundary, boundary)
            .Add(x => x.Attach, attach));

        if (route == Route.LiveCheck)
        {
            Assert.Equal(0, validator.Started);
            await cut.InvokeAsync(() => cut.Find("input").Change("Bea"));
        }

        await UntilAsync(() => validator.Started == 1);
        return (cut, validator);
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
    private static Task SettleAsync<TComponent>(IRenderedComponent<TComponent> cut)
        where TComponent : IComponent =>
        cut.InvokeAsync(() => { });

    /// <summary>Test-only page: a form with one input (a <see cref="FormidableForm{TModel}"/>, or an <c>EditForm</c> with a validator when <see cref="Attach"/> is set), beside a banner the page's fault handler fills, under an optional <see cref="ErrorBoundary"/>.</summary>
    public sealed class FaultHost : ComponentBase
    {
        private string? _fault;

        [Parameter]
        public Account Model { get; set; } = default!;

        [Parameter]
        public FormidableOptions Options { get; set; } = default!;

        [Parameter]
        public IModelValidator<Account> Validator { get; set; } = default!;

        [Parameter]
        public bool Boundary { get; set; }

        [Parameter]
        public bool Attach { get; set; }

        /// <summary>Shows the message in the banner, as a page's handler would.</summary>
        public void ShowFault(string message)
        {
            _fault = message;
            StateHasChanged();
        }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenElement(0, "p");
            builder.AddAttribute(1, "class", "fault");
            builder.AddContent(2, _fault);
            builder.CloseElement();

            if (!Boundary)
            {
                BuildForm(builder);
                return;
            }

            builder.OpenComponent<ErrorBoundary>(10);
            builder.AddComponentParameter(11, nameof(ErrorBoundary.ChildContent), (RenderFragment)BuildForm);
            builder.AddComponentParameter(12, nameof(ErrorBoundary.ErrorContent), (RenderFragment<Exception>)(exception => error =>
            {
                error.OpenElement(0, "p");
                error.AddAttribute(1, "class", "boundary-error");
                error.AddContent(2, exception.Message);
                error.CloseElement();
            }));
            builder.CloseComponent();
        }

        private void BuildForm(RenderTreeBuilder builder)
        {
            RenderFragment<FormidableFormContext> content = _ => inner =>
            {
                inner.OpenComponent<FormidableInputText>(0);
                inner.AddComponentParameter(1, "For", (Expression<Func<string?>>)(() => Model.Name));
                inner.AddComponentParameter(2, "Value", Model.Name);
                inner.AddComponentParameter(3, "ValueChanged", EventCallback.Factory.Create<string?>(this, v => Model.Name = v));
                inner.CloseComponent();
            };

            if (!Attach)
            {
                builder.OpenComponent<FormidableForm<Account>>(20);
                builder.AddComponentParameter(21, nameof(FormidableForm<Account>.Model), Model);
                builder.AddComponentParameter(22, nameof(FormidableForm<Account>.Options), Options);
                builder.AddComponentParameter(23, nameof(FormidableForm<Account>.Validator), Validator);
                builder.AddComponentParameter(24, nameof(FormidableForm<Account>.ChildContent), content);
                builder.CloseComponent();
                return;
            }

            builder.OpenComponent<EditForm>(30);
            builder.AddComponentParameter(31, nameof(EditForm.Model), Model);
            builder.AddComponentParameter(32, nameof(EditForm.ChildContent), (RenderFragment<EditContext>)(_ => inner =>
            {
                inner.OpenComponent<FormidableValidator<Account>>(0);
                inner.AddComponentParameter(1, nameof(FormidableValidator<Account>.Options), Options);
                inner.AddComponentParameter(2, nameof(FormidableValidator<Account>.Validator), Validator);
                inner.AddComponentParameter(3, nameof(FormidableValidator<Account>.ChildContent), content);
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        }
    }
}
