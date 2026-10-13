using System.Diagnostics.CodeAnalysis;
using Bunit;
using Formidable.Blazor.Tests.Fixtures;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace Formidable.Blazor.Tests;

/// <summary>
/// A mark a page keeps after its form is gone keeps nothing of the form alive. A page takes a mark
/// after its first render and puts it in an outbox that outlives the page, as a retry queue would.
/// Then the page is removed, or the form is reset, and collections are forced while the outbox
/// still holds the mark: the engine the mark was taken from, and what that engine holds, are
/// collected.
/// </summary>
/// <remarks>
/// Every script-backed service is replaced, and so is the JS runtime, because bUnit's own runtime
/// records each call's arguments and the form hands its layout observer a reference to itself.
/// </remarks>
// Mutation that must break it: have the mark hold the engine itself rather than its token, and
// every row finds the engine still alive.
public class ServerRequestMarkRetentionTests : BunitContext
{
    public ServerRequestMarkRetentionTests()
    {
        Services.AddSingleton<IFormidableFocusService>(new NoFocus());
        Services.AddSingleton<IFormidableFieldOrderService>(new NoOrder());
        Services.AddSingleton<IFormidableDomValueSync>(new NoSync());
        Services.AddFormidableBlazor();
        Services.AddSingleton<IJSRuntime>(new NoJs());
    }

    public enum RootKind
    {
        Form,
        Validator,
    }

    [Theory]
    [InlineData(RootKind.Form, false)]
    [InlineData(RootKind.Validator, false)]
    [InlineData(RootKind.Form, true)]
    public async Task A_mark_held_after_its_form_is_gone_keeps_nothing_of_it_alive(RootKind root, bool reset)
    {
        Held.Clear(reset);
        var cut = Render<MarkHost>(p => p.Add(x => x.Show, true).Add(x => x.Root, root));
        for (var i = 0; i < 5; i++)
        {
            await cut.InvokeAsync(() => { });
        }

        // A reset keeps the page; removing it takes the page away. Each tick renders again, which
        // turns over the render-tree buffers that still hold what was replaced.
        for (var tick = 1; tick <= 3; tick++)
        {
            var t = tick;
            await cut.InvokeAsync(() => cut.Render(p => p.Add(x => x.Show, reset).Add(x => x.Root, root).Add(x => x.Tick, t)));
        }

        await ForceCollectionsAsync();

        Assert.Single(Held.Outbox);
        Assert.False(Held.Engine!.IsAlive);
        Assert.False(Held.EditContext!.IsAlive);
        if (!reset)
        {
            Assert.False(Held.RootComponent!.IsAlive);
            Assert.False(Held.Page!.IsAlive);
        }

        await Services.DisposeAsync();
    }

    private static async Task ForceCollectionsAsync()
    {
        for (var i = 0; i < 4; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Delay(20);
        }
    }

    /// <summary>The outbox that outlives the page, and weak references to what the page built.</summary>
    private static class Held
    {
        public static List<ServerRequestMark> Outbox { get; } = [];

        public static bool Reset { get; private set; }

        public static WeakReference? Page { get; set; }

        public static WeakReference? RootComponent { get; set; }

        public static WeakReference? Engine { get; set; }

        public static WeakReference? EditContext { get; set; }

        public static void Clear(bool reset)
        {
            Reset = reset;
            Outbox.Clear();
            Page = RootComponent = Engine = EditContext = null;
        }
    }

    /// <summary>The app: shows the page while <see cref="Show"/> is set.</summary>
    public sealed class MarkHost : ComponentBase
    {
        [Parameter]
        public bool Show { get; set; }

        [Parameter]
        public RootKind Root { get; set; }

        [Parameter]
        public int Tick { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.AddContent(0, $"tick {Tick}");
            if (Show)
            {
                builder.OpenComponent<MarkPage>(1);
                builder.AddComponentParameter(2, nameof(MarkPage.Root), Root);
                builder.AddComponentParameter(3, nameof(MarkPage.Tick), Tick);
                builder.CloseComponent();
            }
        }
    }

    /// <summary>The page: a root over its own model, which marks after its first render and keeps the mark in the outbox.</summary>
    public sealed class MarkPage : ComponentBase
    {
        private readonly EngineOrder _order = new() { Customer = new EngineCustomer(), Description = "page state" };
        private readonly FormidableOptions _options = new() { RefreshDebounce = Timeout.InfiniteTimeSpan };
        private readonly IModelValidator<EngineOrder> _validator =
            new FluentValidationModelValidator<EngineOrder>(new EngineOrderValidator());

        private FormidableForm<EngineOrder>? _form;
        private FormidableValidator<EngineOrder>? _attach;

        [Parameter]
        public RootKind Root { get; set; }

        [Parameter]
        public int Tick { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            if (Root == RootKind.Form)
            {
                builder.OpenComponent<FormidableForm<EngineOrder>>(0);
                builder.AddComponentParameter(1, nameof(FormidableForm<EngineOrder>.Model), _order);
                builder.AddComponentParameter(2, nameof(FormidableForm<EngineOrder>.Options), _options);
                builder.AddComponentParameter(3, nameof(FormidableForm<EngineOrder>.Validator), _validator);
                builder.AddComponentParameter(4, nameof(FormidableForm<EngineOrder>.ChildContent),
                    (RenderFragment<FormidableFormContext>)(_ => inner => inner.AddContent(0, _order.Description)));
                builder.AddComponentReferenceCapture(5, reference => _form = (FormidableForm<EngineOrder>)reference);
                builder.CloseComponent();
                return;
            }

            builder.OpenComponent<EditForm>(10);
            builder.AddComponentParameter(11, nameof(EditForm.Model), _order);
            builder.AddComponentParameter(12, nameof(EditForm.ChildContent), (RenderFragment<EditContext>)(_ => inner =>
            {
                inner.OpenComponent<FormidableValidator<EngineOrder>>(0);
                inner.AddComponentParameter(1, nameof(FormidableValidator<EngineOrder>.Options), _options);
                inner.AddComponentParameter(2, nameof(FormidableValidator<EngineOrder>.Validator), _validator);
                inner.AddComponentParameter(3, nameof(FormidableValidator<EngineOrder>.ChildContent),
                    (RenderFragment<FormidableFormContext>)(_ => content => content.AddContent(0, _order.Description)));
                inner.AddComponentReferenceCapture(4, reference => _attach = (FormidableValidator<EngineOrder>)reference);
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        }

        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if (!firstRender)
            {
                return;
            }

            Record();
            Held.Outbox.Add(Root == RootKind.Form ? _form!.MarkServerRequest() : _attach!.MarkServerRequest());
            if (Held.Reset)
            {
                await _form!.ResetAsync();
            }
        }

        // In a method of its own, so no strong reference outlives it in the async state machine.
        private void Record()
        {
            IFormidableEngine engine = Root == RootKind.Form ? _form!.Engine! : _attach!.Engine!;
            Held.Page = new WeakReference(this);
            Held.RootComponent = new WeakReference(Root == RootKind.Form ? _form! : _attach!);
            Held.Engine = new WeakReference(engine);
            Held.EditContext = new WeakReference(engine.EditContext);
        }
    }

    private sealed class NoFocus : IFormidableFocusService
    {
        public ValueTask<bool> FocusAsync(FieldIdentifier field) => ValueTask.FromResult(false);
    }

    private sealed class NoOrder : IFormidableFieldOrderService
    {
        public ValueTask<IReadOnlyList<FieldIdentifier>?> OrderAsync(IReadOnlyList<FieldIdentifier> fields) =>
            ValueTask.FromResult<IReadOnlyList<FieldIdentifier>?>(null);
    }

    private sealed class NoSync : IFormidableDomValueSync
    {
        public ValueTask SyncValueAsync(string elementId, string? value) => ValueTask.CompletedTask;
    }

    private sealed class NoJs : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties)] TValue>(
            string identifier, object?[]? args) =>
            ValueTask.FromException<TValue>(new JSException("no script on this host"));

        public ValueTask<TValue> InvokeAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties)] TValue>(
            string identifier, CancellationToken cancellationToken, object?[]? args) =>
            ValueTask.FromException<TValue>(new JSException("no script on this host"));
    }
}
