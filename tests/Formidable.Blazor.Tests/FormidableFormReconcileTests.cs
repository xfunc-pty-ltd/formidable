using System.Linq.Expressions;
using Bunit;
using Formidable.Blazor.Tests.Fixtures;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;

namespace Formidable.Blazor.Tests;

/// <summary>
/// <see cref="FormidableForm{TModel}"/> reconciles the rendered field set when a nested component's
/// render changes it, without the form re-rendering: a removed field's message leaves every
/// surface, the reconcile runs once per batch, and nothing reaches the engine once the form is
/// disposed. A form render that removes a field still reconciles before the render returns.
/// </summary>
public class FormidableFormReconcileTests : BunitContext
{
    private static readonly string TooLong = new('x', 11);

    public FormidableFormReconcileTests()
    {
        Services.AddFormidableBlazor();
        Services.AddSingleton<FluentValidation.IValidator<EngineOrder>, EngineOrderValidator>();
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.SetupModule("./_content/Formidable.Blazor/formidable.js")
            .Setup<IReadOnlyList<string>>("orderFields", _ => true).SetResult([]);
    }

    // Mutation: drop FormidableForm's Registry.Changed subscription, and the error stays in the
    // store, the summary and the native message until the form next renders.
    [Fact]
    public async Task A_plain_input_s_error_leaves_every_surface_when_a_nested_render_removes_it()
    {
        var order = new EngineOrder { Customer = new EngineCustomer() };
        var cut = RenderHost(order, nestedDescription: true);
        var nested = cut.FindComponent<NestedInputs>();
        var editContext = cut.FindComponent<FormidableForm<EngineOrder>>().Instance.Engine!.EditContext;
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));

        cut.Find("input").Change(TooLong);
        await Settle(cut);
        AssertShowing(cut, editContext, description);

        var formRenders = cut.Instance.ContentRenders;
        nested.Render(parameters => parameters.Add(p => p.Description, false));
        await Settle(cut);

        Assert.Equal(formRenders, cut.Instance.ContentRenders);
        Assert.Empty(cut.FindAll("input"));
        AssertGone(cut, editContext, description);

        await Services.DisposeAsync();
    }

    // The window the posted reconcile leaves: a nested render removes the field and, in the same
    // dispatcher turn, an edit to another field starts a check, which still answers the removed
    // field. The reconcile then runs once and takes the removed field's message off every surface.
    // Mutation: drop FormidableForm's Registry.Changed subscription, and the message the check put
    // back stays on the store, the summary and the native message.
    [Fact]
    public async Task A_check_started_before_the_reconcile_leaves_nothing_for_the_removed_field()
    {
        var order = new EngineOrder { Customer = new EngineCustomer() };
        var cut = RenderHost(order, nestedDescription: true);
        var nested = cut.FindComponent<NestedInputs>();
        var form = cut.FindComponent<FormidableForm<EngineOrder>>().Instance;
        var engine = form.Engine!;
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));

        cut.Find("input").Change(TooLong);
        await Settle(cut);
        AssertShowing(cut, engine.EditContext, description);
        var baseline = form.ReconcileCount;

        var answeredInTheWindow = false;
        await cut.InvokeAsync(() =>
        {
            nested.Render(parameters => parameters.Add(p => p.Description, false));
            engine.EditContext.NotifyFieldChanged(new FieldIdentifier(order, nameof(EngineOrder.Customer)));

            // Still inside the turn that removed the field, so the reconcile has not run: the
            // check just answered the removed field along with the edited one.
            answeredInTheWindow = engine.EditContext.GetValidationMessages(description).Any();
        });
        await Settle(cut);

        Assert.True(answeredInTheWindow);
        Assert.Equal(baseline + 1, form.ReconcileCount);
        AssertGone(cut, engine.EditContext, description);

        await Services.DisposeAsync();
    }

    // A pin of the ordering the render-time reconcile keeps: when the form's own render removes a
    // field, the reconcile runs in that render's OnAfterRenderAsync, which bUnit runs before the
    // render call returns, and runs once. Mutation: drop the reconcile from OnAfterRenderAsync, so
    // only a posted one could run, and the message is still in the store when the render returns.
    [Fact]
    public async Task A_form_render_that_removes_a_field_reconciles_before_the_render_returns()
    {
        var order = new EngineOrder { Customer = new EngineCustomer() };
        var cut = RenderHost(order, outerDescription: true);
        var form = cut.FindComponent<FormidableForm<EngineOrder>>().Instance;
        var editContext = form.Engine!.EditContext;
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));

        cut.Find("input").Change(TooLong);
        await Settle(cut);
        AssertShowing(cut, editContext, description);
        var baseline = form.ReconcileCount;

        var leftInsideTheRender = false;
        await cut.InvokeAsync(() =>
        {
            cut.Render(parameters => parameters.Add(p => p.OuterDescription, false));
            leftInsideTheRender = !editContext.GetValidationMessages(description).Any();
        });
        await Settle(cut);

        Assert.True(leftInsideTheRender);
        Assert.Equal(baseline + 1, form.ReconcileCount);
        AssertGone(cut, editContext, description);

        await Services.DisposeAsync();
    }

    // A field that registers while the form renders is left to the form's own OnAfterRenderAsync,
    // so nothing is posted; the same registration from a nested component's own render posts one
    // reconcile. Mutation: post whether or not a render of the form is still to reach
    // OnAfterRenderAsync, and the form's render posts too.
    [Fact]
    public async Task A_form_render_posts_no_reconcile_and_a_nested_render_posts_one()
    {
        var order = new EngineOrder { Customer = new EngineCustomer() };
        var cut = RenderHost(order);
        await Settle(cut);

        var duringFormRender = new RecordingContext();
        await cut.InvokeAsync(() => cut.Render(parameters => parameters.Add(p => p.Recorder, duringFormRender)));
        Assert.Equal(1, duringFormRender.Registrations);
        Assert.Equal(0, duringFormRender.Posts);

        var duringNestedRender = new RecordingContext();
        var probe = cut.FindComponent<RegisteringProbe>();
        await cut.InvokeAsync(() => probe.Render(parameters => parameters.Add(p => p.Recorder, duringNestedRender)));
        Assert.Equal(1, duringNestedRender.Registrations);
        Assert.Equal(1, duringNestedRender.Posts);

        await Services.DisposeAsync();
    }

    // Three inputs leave in one nested render, and each departure posts a reconcile. Mutation:
    // drop the version gate in ReconcileIfChanged, and the count climbs by three.
    [Fact]
    public async Task A_batch_of_nested_removals_reconciles_once()
    {
        var order = new EngineOrder
        {
            Customer = new EngineCustomer(),
            Items = [new EngineItem(), new EngineItem(), new EngineItem()],
        };
        var cut = RenderHost(order, nestedRows: true);
        var nested = cut.FindComponent<NestedInputs>();
        var form = cut.FindComponent<FormidableForm<EngineOrder>>().Instance;
        Assert.Equal(3, cut.FindAll("input").Count);
        await Settle(cut);
        var baseline = form.ReconcileCount;

        nested.Render(parameters => parameters.Add(p => p.Rows, false));
        await Settle(cut);

        Assert.Empty(cut.FindAll("input"));
        Assert.Equal(baseline + 1, form.ReconcileCount);

        await Services.DisposeAsync();
    }

    // Before disposal a registry change posts one reconcile; after it, none, and nothing reaches
    // the disposed engine. Mutation: drop the unsubscription from Dispose, and the change after
    // disposal posts.
    [Fact]
    public async Task After_Dispose_a_registry_change_posts_nothing()
    {
        var order = new EngineOrder { Customer = new EngineCustomer() };
        var cut = RenderHost(order);
        var form = cut.FindComponent<FormidableForm<EngineOrder>>().Instance;
        var engine = form.Engine!;
        var faults = 0;
        engine.ValidationFaulted += (_, _) => faults++;

        // A registration and its end each raise Changed, so each posts one reconcile.
        var before = new RecordingContext();
        await cut.InvokeAsync(() => WithContext(
            before, () => engine.Registry.Register(new FieldIdentifier(order, "Phantom")).Dispose()));
        await cut.InvokeAsync(before.RunPosted);
        Assert.Equal(2, before.Posts);

        await DisposeComponentsAsync();
        var reconciled = form.ReconcileCount;

        var after = new RecordingContext();
        WithContext(after, () => engine.Registry.Register(new FieldIdentifier(order, "Phantom")));
        after.RunPosted();

        Assert.Equal(0, after.Posts);
        Assert.Equal(reconciled, form.ReconcileCount);
        Assert.Equal(0, faults);
        Assert.Empty(engine.EditContext.GetValidationMessages());

        await Services.DisposeAsync();
    }

    // ResetAsync builds a new engine; the old engine's registry posts nothing from then on, and the
    // new one's does. Mutation: drop the unsubscription from RebuildEngine, and a change in the old
    // registry posts a reconcile.
    [Fact]
    public async Task After_a_rebuild_only_the_new_engine_s_registry_posts()
    {
        var order = new EngineOrder { Customer = new EngineCustomer() };
        var cut = RenderHost(order);
        var form = cut.FindComponent<FormidableForm<EngineOrder>>().Instance;
        var old = form.Engine!;

        await cut.InvokeAsync(() => form.ResetAsync());
        var rebuilt = form.Engine!;
        Assert.NotSame(old, rebuilt);

        var oldRegistry = new RecordingContext();
        await cut.InvokeAsync(() => WithContext(
            oldRegistry, () => old.Registry.Register(new FieldIdentifier(order, "Phantom"))));
        var newRegistry = new RecordingContext();
        await cut.InvokeAsync(() => WithContext(
            newRegistry, () => rebuilt.Registry.Register(new FieldIdentifier(order, "Phantom"))));

        Assert.Equal(0, oldRegistry.Posts);
        Assert.Equal(1, newRegistry.Posts);

        await Services.DisposeAsync();
    }

    // A pin, passing before the form posted anything because nothing was posted then. A reconcile
    // posted just before disposal runs after it. The removed row shows an error, so a reconcile
    // reaching the engine would drop it and republish the store. Mutation: drop the
    // disposed test from ReconcileIfChanged, and the posted reconcile is counted and puts the
    // description's error back into the store Dispose had cleared.
    [Fact]
    public async Task A_reconcile_posted_before_Dispose_reaches_nothing_after_it()
    {
        var item = new EngineItem { Sku = "sku" };
        var order = new EngineOrder { Customer = new EngineCustomer(), Items = [item] };
        var cut = RenderHost(order, outerDescription: true, nestedRows: true);
        var nested = cut.FindComponent<NestedInputs>();
        var form = cut.FindComponent<FormidableForm<EngineOrder>>().Instance;
        var engine = form.Engine!;
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));
        var sku = new FieldIdentifier(item, nameof(EngineItem.Sku));

        cut.Find("input[data-name=outer]").Change(TooLong);
        cut.Find("input[data-name=row]").Change(string.Empty);
        await Settle(cut);
        Assert.NotEmpty(engine.EditContext.GetValidationMessages(description));
        Assert.NotEmpty(engine.EditContext.GetValidationMessages(sku));
        var baseline = form.ReconcileCount;

        // One turn: the nested render posts the reconcile, and the form is disposed before the
        // dispatcher reaches it.
        await cut.InvokeAsync(() =>
        {
            nested.Render(parameters => parameters.Add(p => p.Rows, false));
            form.Dispose();
        });
        await Settle(cut);

        Assert.Equal(baseline, form.ReconcileCount);
        Assert.Empty(engine.EditContext.GetValidationMessages());

        await Services.DisposeAsync();
    }

    // The dispatcher runs one piece of work at a time, so a no-op queued behind a posted reconcile
    // completes only once that reconcile has.
    private static Task Settle<TComponent>(IRenderedComponent<TComponent> cut)
        where TComponent : IComponent =>
        cut.InvokeAsync(() => { });

    private static void AssertShowing<TComponent>(IRenderedComponent<TComponent> cut, EditContext editContext, FieldIdentifier field)
        where TComponent : IComponent
    {
        Assert.Contains(editContext.GetValidationMessages(field), m => m.Contains("10"));
        Assert.Contains(cut.FindAll("ul.formidable-summary__group--error li"), li => li.TextContent.Contains("10"));
        Assert.Contains(cut.FindAll("div.validation-message"), div => div.TextContent.Contains("10"));
    }

    private static void AssertGone<TComponent>(IRenderedComponent<TComponent> cut, EditContext editContext, FieldIdentifier field)
        where TComponent : IComponent
    {
        Assert.Empty(editContext.GetValidationMessages(field));
        Assert.DoesNotContain(cut.FindAll("ul.formidable-summary__group--error li"), li => li.TextContent.Contains("10"));
        Assert.DoesNotContain(cut.FindAll("div.validation-message"), div => div.TextContent.Contains("10"));
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

    private IRenderedComponent<ReconcileHost> RenderHost(
        EngineOrder order,
        bool outerDescription = false,
        bool nestedDescription = false,
        bool nestedRows = false) =>
        Render<ReconcileHost>(parameters => parameters
            .Add(p => p.Order, order)
            .Add(p => p.OuterDescription, outerDescription)
            .Add(p => p.NestedDescription, nestedDescription)
            .Add(p => p.NestedRows, nestedRows));

    /// <summary>Records every post, and runs them only when asked, so a test can count what a registry change posted.</summary>
    private sealed class RecordingContext : SynchronizationContext
    {
        private readonly List<(SendOrPostCallback Callback, object? State)> _posted = [];

        public int Posts => _posted.Count;

        // How many registrations a RegisteringProbe made with this context current.
        public int Registrations { get; set; }

        public override void Post(SendOrPostCallback d, object? state) => _posted.Add((d, state));

        public void RunPosted()
        {
            foreach (var (callback, state) in _posted.ToList())
            {
                callback(state);
            }
        }
    }

    /// <summary>Test-only host: a form over <see cref="Order"/> with the summary and a native message for the description, the description's input in the form's own content while <see cref="OuterDescription"/> is set, and a <see cref="NestedInputs"/>.</summary>
    private sealed class ReconcileHost : ComponentBase
    {
        private readonly FormidableOptions _options = new() { RefreshDebounce = Timeout.InfiniteTimeSpan };

        [Parameter]
        public EngineOrder Order { get; set; } = default!;

        [Parameter]
        public bool OuterDescription { get; set; }

        [Parameter]
        public bool NestedDescription { get; set; }

        [Parameter]
        public bool NestedRows { get; set; }

        [Parameter]
        public RecordingContext? Recorder { get; set; }

        // How many times the form has rendered its content, which a nested component's own render
        // never does.
        public int ContentRenders { get; private set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<FormidableForm<EngineOrder>>(0);
            builder.AddComponentParameter(1, nameof(FormidableForm<EngineOrder>.Model), Order);
            builder.AddComponentParameter(2, nameof(FormidableForm<EngineOrder>.Options), _options);
            builder.AddComponentParameter(3, nameof(FormidableForm<EngineOrder>.ChildContent), (RenderFragment<FormidableFormContext>)(_ => inner =>
            {
                ContentRenders++;
                if (OuterDescription)
                {
                    AddDescriptionInput(inner, 0, this, Order, "outer");
                }

                inner.OpenComponent<NestedInputs>(10);
                inner.AddComponentParameter(11, nameof(NestedInputs.Order), Order);
                inner.AddComponentParameter(12, nameof(NestedInputs.Description), NestedDescription);
                inner.AddComponentParameter(13, nameof(NestedInputs.Rows), NestedRows);
                inner.CloseComponent();

                inner.OpenComponent<FormidableSummary>(20);
                inner.CloseComponent();

                inner.OpenComponent<ValidationMessage<string>>(21);
                inner.AddComponentParameter(22, "For", (Expression<Func<string>>)(() => Order.Description));
                inner.CloseComponent();

                inner.OpenComponent<RegisteringProbe>(23);
                inner.AddComponentParameter(24, nameof(RegisteringProbe.Order), Order);
                inner.AddComponentParameter(25, nameof(RegisteringProbe.Recorder), Recorder);
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        }
    }

    /// <summary>Test-only component inside the form: the description's input while <see cref="Description"/> is set, and an input per row's SKU while <see cref="Rows"/> is.</summary>
    private sealed class NestedInputs : ComponentBase
    {
        [Parameter]
        public EngineOrder Order { get; set; } = default!;

        [Parameter]
        public bool Description { get; set; }

        [Parameter]
        public bool Rows { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            if (Description)
            {
                AddDescriptionInput(builder, 0, this, Order, "nested");
            }

            if (!Rows)
            {
                return;
            }

            foreach (var item in Order.Items)
            {
                builder.OpenComponent<FormidableInputText>(10);
                builder.SetKey(item);
                builder.AddComponentParameter(11, "For", (Expression<Func<string?>>)(() => item.Sku));
                builder.AddComponentParameter(12, "Value", item.Sku);
                builder.AddComponentParameter(13, "ValueChanged",
                    EventCallback.Factory.Create<string?>(this, v => item.Sku = v ?? string.Empty));
                builder.AddComponentParameter(14, "data-name", "row");
                builder.CloseComponent();
            }
        }
    }

    /// <summary>Test-only component inside the form: each time it is handed a <see cref="Recorder"/>, it registers a new field with that context current, from inside whichever render handed it over.</summary>
    private sealed class RegisteringProbe : ComponentBase
    {
        private RecordingContext? _seen;

        [CascadingParameter]
        private FormidableFormContext Context { get; set; } = default!;

        [Parameter]
        public EngineOrder Order { get; set; } = default!;

        [Parameter]
        public RecordingContext? Recorder { get; set; }

        protected override void OnParametersSet()
        {
            if (Recorder is null || ReferenceEquals(Recorder, _seen))
            {
                return;
            }

            _seen = Recorder;
            Recorder.Registrations++;
            WithContext(Recorder, () => Context.Registry.Register(new FieldIdentifier(Order, $"Probe{Recorder.GetHashCode()}")));
        }
    }

    // The description's input, bound as @bind-Value binds it. It takes five sequence numbers.
    private static void AddDescriptionInput(RenderTreeBuilder builder, int sequence, object receiver, EngineOrder order, string name)
    {
        builder.OpenComponent<FormidableInputText>(sequence);
        builder.AddComponentParameter(sequence + 1, "For", (Expression<Func<string?>>)(() => order.Description));
        builder.AddComponentParameter(sequence + 2, "Value", order.Description);
        builder.AddComponentParameter(sequence + 3, "ValueChanged",
            EventCallback.Factory.Create<string?>(receiver, v => order.Description = v ?? string.Empty));
        builder.AddComponentParameter(sequence + 4, "data-name", name);
        builder.CloseComponent();
    }
}
