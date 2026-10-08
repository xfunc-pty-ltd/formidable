using System.Linq.Expressions;
using Bunit;
using Formidable.Blazor.Tests.Fixtures;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

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

    // Three inputs leave in one nested render, and the batch reconciles once. Mutation: drop both
    // BatchPost's pending flag and the reconciler's version gate, and the count climbs by three.
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

        // A registration and its end each raise Changed in one call stack, and the form posts one
        // reconcile for the pair: a root posts once per batch of registry changes, not once per
        // change.
        var before = new RecordingContext();
        await cut.InvokeAsync(() => WithContext(
            before, () => engine.Registry.Register(new FieldIdentifier(order, "Phantom")).Dispose()));
        await cut.InvokeAsync(before.RunPosted);
        Assert.Equal(1, before.Posts);

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

    // A pin: a reconcile posted just before disposal runs after it and reaches nothing. The
    // removed row shows an error, so a reconcile reaching the engine would drop it and republish
    // the store. Mutation: drop the disposed test from the form's registry reader, and the posted
    // reconcile is counted and puts the description's error back into the store Dispose had
    // cleared.
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

    // A nested render removes a field with a live error, and a consumer's OnValidationStateChanged
    // handler throws once from inside the reconcile that removal posted. No ErrorBoundary surrounds
    // the form, so the form hands the throw to the renderer's own unhandled path, as a throw from
    // its lifecycle would go: bUnit completes Renderer.UnhandledException with it and rethrows it
    // from the next render call. The form stays on the page, and that render reconciles again:
    // both attempts count, the departed field's message stays out of the EditContext, and the
    // whole-form re-check the reconcile owes runs once the clock reaches RefreshDebounce. A pin:
    // the form records the registry version only once the reconcile has returned. Mutation: record
    // it before the reconcile runs, and the render finds nothing to do, so one attempt counts and
    // the re-check never runs.
    [Fact]
    public async Task A_form_reconcile_whose_handler_threw_is_retried()
    {
        var clock = new FakeTimeProvider();
        Services.AddSingleton<TimeProvider>(clock);
        var rules = new RuleRunCountingValidator();
        var order = new EngineOrder();
        var cut = Render<RetryHost>(parameters => parameters
            .Add(p => p.Order, order)
            .Add(p => p.Options, new FormidableOptions { RefreshDebounce = RetryRefreshDebounce })
            .Add(p => p.Validator, new FluentValidationModelValidator<EngineOrder>(rules)));
        var form = cut.FindComponent<FormidableForm<EngineOrder>>().Instance;
        var engine = form.Engine!;
        var section = cut.FindComponent<DescriptionSection>().Instance;
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));

        // The re-check the first render's reconcile owes runs first, so the only one left to run
        // is the one the reconcile under test owes.
        await Settle(cut);
        clock.Advance(RetryRefreshDebounce);
        await WaitUntil(cut, () => rules.SubmitRuleRuns == 1);

        await cut.InvokeAsync(() => engine.EditContext.NotifyFieldChanged(description));
        await WaitUntil(cut, () => engine.EditContext.GetValidationMessages(description).Any());
        var runs = rules.SubmitRuleRuns;
        var baseline = form.ReconcileCount;

        var armed = true;
        engine.EditContext.OnValidationStateChanged += (_, _) =>
        {
            if (armed)
            {
                armed = false;
                throw new InvalidOperationException("a consumer handler throws");
            }
        };

        await cut.InvokeAsync(section.Hide);
        await Settle(cut);
        Assert.False(armed);
        Assert.Equal(baseline + 1, form.ReconcileCount);
        Assert.True(Renderer.UnhandledException.IsCompleted);
        Assert.Equal("a consumer handler throws", (await Renderer.UnhandledException).Message);

        var rethrown = await Record.ExceptionAsync(
            () => cut.InvokeAsync(() => cut.Render(parameters => parameters.Add(p => p.Renders, 1))));
        await Settle(cut);

        Assert.Equal("a consumer handler throws", Assert.IsType<InvalidOperationException>(rethrown).Message);
        Assert.Equal(baseline + 2, form.ReconcileCount);
        Assert.Empty(engine.EditContext.GetValidationMessages(description));

        clock.Advance(RetryRefreshDebounce);
        await WaitUntil(cut, () => rules.SubmitRuleRuns > runs);
        Assert.Equal(runs + 1, rules.SubmitRuleRuns);

        await Services.DisposeAsync();
    }

    // A nested component's own render adds four rows while no render of the form is pending, so
    // four registrations raise Changed in one batch. The root posts one reconcile for them, and it
    // runs once. Mutation: drop BatchPost's pending flag, and the batch posts four.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_nested_batch_of_registry_changes_posts_one_reconcile(bool attach)
    {
        var order = new EngineOrder
        {
            Customer = new EngineCustomer(),
            Items = [new EngineItem(), new EngineItem(), new EngineItem(), new EngineItem()],
        };
        var cut = Render<BatchHost>(parameters => parameters
            .Add(p => p.Order, order)
            .Add(p => p.Attach, attach));
        var nested = cut.FindComponent<NestedInputs>();
        Func<int> reconciles;
        Func<int> posts;
        if (attach)
        {
            var validator = cut.FindComponent<FormidableValidator<EngineOrder>>().Instance;
            reconciles = () => validator.ReconcileCount;
            posts = () => validator.ReconcilePostCount;
        }
        else
        {
            var form = cut.FindComponent<FormidableForm<EngineOrder>>().Instance;
            reconciles = () => form.ReconcileCount;
            posts = () => form.ReconcilePostCount;
        }

        await Settle(cut);
        var reconciled = reconciles();
        var posted = posts();

        await cut.InvokeAsync(() => nested.Render(parameters => parameters.Add(p => p.Rows, true)));
        await Settle(cut);

        Assert.Equal(4, cut.FindAll("input[data-name=row]").Count);
        Assert.Equal(posted + 1, posts());
        Assert.Equal(reconciled + 1, reconciles());

        await Services.DisposeAsync();
    }

    // A pin: a nested render adds rows and posts a reconcile, and in the same dispatcher turn the
    // root reconciles the move itself (the form through its own render, attach mode through
    // NotifyFieldSetChanged). The post then finds the version already recorded and does nothing.
    // Mutation: drop the reconciler's version gate, and the post reconciles the same move again.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_move_the_root_already_reconciled_is_not_reconciled_again_by_its_post(bool attach)
    {
        var order = new EngineOrder
        {
            Customer = new EngineCustomer(),
            Items = [new EngineItem(), new EngineItem()],
        };
        var cut = Render<BatchHost>(parameters => parameters
            .Add(p => p.Order, order)
            .Add(p => p.Attach, attach));
        var nested = cut.FindComponent<NestedInputs>();
        Func<int> reconciles;
        Func<int> posts;
        Action reconcileNow;
        if (attach)
        {
            var validator = cut.FindComponent<FormidableValidator<EngineOrder>>().Instance;
            reconciles = () => validator.ReconcileCount;
            posts = () => validator.ReconcilePostCount;
            reconcileNow = validator.NotifyFieldSetChanged;
        }
        else
        {
            var form = cut.FindComponent<FormidableForm<EngineOrder>>().Instance;
            reconciles = () => form.ReconcileCount;
            posts = () => form.ReconcilePostCount;
            reconcileNow = () => cut.Render(parameters => parameters.Add(p => p.Renders, 1));
        }

        await Settle(cut);
        var reconciled = reconciles();
        var posted = posts();

        await cut.InvokeAsync(() =>
        {
            nested.Render(parameters => parameters.Add(p => p.Rows, true));
            reconcileNow();
        });
        await Settle(cut);

        Assert.Equal(2, cut.FindAll("input[data-name=row]").Count);
        Assert.Equal(posted + 1, posts());
        Assert.Equal(reconciled + 1, reconciles());

        await Services.DisposeAsync();
    }

    private static readonly TimeSpan RetryRefreshDebounce = TimeSpan.FromSeconds(1);

    // Polls on real time, settling the dispatcher between reads, because a check the fake clock
    // starts runs on the dispatcher and renders nothing a WaitForAssertion could wait on.
    private static async Task WaitUntil<TComponent>(IRenderedComponent<TComponent> cut, Func<bool> condition)
        where TComponent : IComponent
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
            await Settle(cut);
        }
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

    /// <summary>Test-only host: a form over <see cref="Order"/> with the <see cref="Options"/> and <see cref="Validator"/> given, around a <see cref="DescriptionSection"/>; a new <see cref="Renders"/> value renders the form again.</summary>
    private sealed class RetryHost : ComponentBase
    {
        [Parameter]
        public EngineOrder Order { get; set; } = default!;

        [Parameter]
        public FormidableOptions Options { get; set; } = default!;

        [Parameter]
        public IModelValidator<EngineOrder> Validator { get; set; } = default!;

        [Parameter]
        public int Renders { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<FormidableForm<EngineOrder>>(0);
            builder.AddComponentParameter(1, nameof(FormidableForm<EngineOrder>.Model), Order);
            builder.AddComponentParameter(2, nameof(FormidableForm<EngineOrder>.Options), Options);
            builder.AddComponentParameter(3, nameof(FormidableForm<EngineOrder>.Validator), Validator);
            builder.AddComponentParameter(4, nameof(FormidableForm<EngineOrder>.ChildContent), (RenderFragment<FormidableFormContext>)(_ => inner =>
            {
                inner.OpenComponent<DescriptionSection>(0);
                inner.AddComponentParameter(1, nameof(DescriptionSection.Order), Order);
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        }
    }

    /// <summary>Test-only component inside the form: the description's input until <see cref="Hide"/> removes it in a render of this component alone, which a later render of the form does not undo.</summary>
    private sealed class DescriptionSection : ComponentBase
    {
        private bool _showing = true;

        [Parameter]
        public EngineOrder Order { get; set; } = default!;

        public void Hide()
        {
            _showing = false;
            StateHasChanged();
        }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            if (_showing)
            {
                AddDescriptionInput(builder, 0, this, Order, "section");
            }
        }
    }

    /// <summary>Test-only host: a <see cref="NestedInputs"/> over <see cref="Order"/> inside a form, or inside an <c>EditForm</c> with a validator when <see cref="Attach"/> is set; a new <see cref="Renders"/> value renders the root again.</summary>
    private sealed class BatchHost : ComponentBase
    {
        private readonly FormidableOptions _options = new() { RefreshDebounce = Timeout.InfiniteTimeSpan };

        [Parameter]
        public EngineOrder Order { get; set; } = default!;

        [Parameter]
        public bool Attach { get; set; }

        [Parameter]
        public int Renders { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            RenderFragment<FormidableFormContext> content = _ => inner =>
            {
                inner.OpenComponent<NestedInputs>(0);
                inner.AddComponentParameter(1, nameof(NestedInputs.Order), Order);
                inner.CloseComponent();
            };

            if (!Attach)
            {
                builder.OpenComponent<FormidableForm<EngineOrder>>(0);
                builder.AddComponentParameter(1, nameof(FormidableForm<EngineOrder>.Model), Order);
                builder.AddComponentParameter(2, nameof(FormidableForm<EngineOrder>.Options), _options);
                builder.AddComponentParameter(3, nameof(FormidableForm<EngineOrder>.ChildContent), content);
                builder.CloseComponent();
                return;
            }

            builder.OpenComponent<EditForm>(4);
            builder.AddComponentParameter(5, nameof(EditForm.Model), Order);
            builder.AddComponentParameter(6, nameof(EditForm.ChildContent), (RenderFragment<EditContext>)(_ => inner =>
            {
                inner.OpenComponent<FormidableValidator<EngineOrder>>(0);
                inner.AddComponentParameter(1, nameof(FormidableValidator<EngineOrder>.Options), _options);
                inner.AddComponentParameter(2, nameof(FormidableValidator<EngineOrder>.ChildContent), content);
                inner.CloseComponent();
            }));
            builder.CloseComponent();
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
