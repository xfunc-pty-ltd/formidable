using System.Linq.Expressions;
using Bunit;
using Formidable.Blazor.Tests.Fixtures;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace Formidable.Blazor.Tests;

/// <summary>
/// The browser's layout report asks the form where its fields now sit. The answer is a measuring
/// round trip, never a render of the form: the page the report describes is already drawn, and a
/// render of the form runs the consumer's whole fragment again, which re-sets the parameters of
/// every component in it that Blazor cannot prove unchanged. A component that does work on its
/// parameters (a list it reseeds, a node it toggles) would then do that work for a change that was
/// never its own, and a render that changes the DOM makes the observer report again.
/// </summary>
public class LayoutReportTests : BunitContext
{
    // The report must provoke a resolve, and nothing inside the form may learn of it. The counter
    // stands where a consumer's own component stands; its one parameter is an object, which is
    // enough for any render of the fragment above it to re-set its parameters. Mutation that must
    // break this: answer the report with StateHasChanged, and the count moves by one.
    [Fact]
    public async Task A_layout_report_re_resolves_the_order_without_re_rendering_the_forms_content()
    {
        var order = new EngineOrder { Customer = new EngineCustomer() };
        var fieldOrder = new RecordingFieldOrderService();
        Services.AddSingleton<IFormidableFieldOrderService>(fieldOrder);
        Services.AddFormidableBlazor();
        var module = SetUpJsModule();

        var cut = RenderForm(order);
        var counter = cut.FindComponent<ParameterSetCounter>().Instance;
        cut.WaitForAssertion(() => Assert.NotEmpty(fieldOrder.Requests));
        var resolvesBefore = fieldOrder.Requests.Count;
        var parameterSetsBefore = counter.ParameterSets;

        await ReportLayoutMoveAsync(cut, module);

        cut.WaitForAssertion(() => Assert.True(
            fieldOrder.Requests.Count > resolvesBefore,
            "the layout report provoked no fresh order resolve"));
        Assert.Equal(parameterSetsBefore, counter.ParameterSets);

        await Services.DisposeAsync();
    }

    // A second report while the first resolve is still being answered is a second question, asked
    // at once: the stamps already decide which answer stands, so nothing has to wait for a render.
    [Fact]
    public async Task A_report_during_a_resolve_asks_again_at_once()
    {
        var order = new EngineOrder { Customer = new EngineCustomer() };
        var fieldOrder = new HeldFieldOrderService();
        Services.AddSingleton<IFormidableFieldOrderService>(fieldOrder);
        Services.AddFormidableBlazor();
        var module = SetUpJsModule();

        var cut = RenderForm(order);
        cut.WaitForAssertion(() => Assert.Single(fieldOrder.Calls));
        await cut.InvokeAsync(() => fieldOrder.Calls[0].SetResult([]));

        var first = ReportLayoutMoveAsync(cut, module);
        cut.WaitForAssertion(() => Assert.Equal(2, fieldOrder.Calls.Count));
        var second = ReportLayoutMoveAsync(cut, module);
        cut.WaitForAssertion(() => Assert.Equal(3, fieldOrder.Calls.Count));

        await cut.InvokeAsync(() => fieldOrder.Calls[1].SetResult([]));
        await cut.InvokeAsync(() => fieldOrder.Calls[2].SetResult([]));
        await first;
        await second;

        await Services.DisposeAsync();
    }

    // The service is a seam a consumer can implement, so a throw of its own is a bug of theirs to
    // see. On the report path there is no lifecycle method for it to escape from, so the form hands
    // it to the renderer itself, where a throw from the render path would have landed. Mutation
    // that must break this: let the throw go back to the script, whose callback drops it.
    [Fact]
    public async Task A_consumer_order_service_that_throws_on_a_report_reaches_the_renderer()
    {
        var order = new EngineOrder { Customer = new EngineCustomer() };
        var fieldOrder = new RecordingFieldOrderService();
        Services.AddSingleton<IFormidableFieldOrderService>(fieldOrder);
        Services.AddFormidableBlazor();
        var module = SetUpJsModule();

        var cut = RenderForm(order);
        cut.WaitForAssertion(() => Assert.NotEmpty(fieldOrder.Requests));

        fieldOrder.Fault = new InvalidOperationException("the consumer's own bug");
        await ReportLayoutMoveAsync(cut, module);

        var thrown = await Renderer.UnhandledException.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("the consumer's own bug", thrown.GetBaseException().Message);

        await Services.DisposeAsync();
    }

    private static async Task ReportLayoutMoveAsync(
        IRenderedComponent<FormidableForm<EngineOrder>> cut, BunitJSModuleInterop module)
    {
        // Driven through the very reference observeLayout was handed, which is all the browser
        // holds.
        var receiver = (DotNetObjectReference<LayoutObserverReceiver>)
            module.Invocations["observeLayout"].Single().Arguments[1]!;
        await cut.InvokeAsync(() => receiver.Value.NotifyLayoutMoved());
    }

    private BunitJSModuleInterop SetUpJsModule()
    {
        var module = JSInterop.SetupModule("./_content/Formidable.Blazor/formidable.js");
        module.Setup<bool>("focusField", _ => true).SetResult(true);
        module.SetupVoid("observeLayout", _ => true).SetVoidResult();
        module.SetupVoid("disconnectLayoutObserver", _ => true).SetVoidResult();
        return module;
    }

    /// <summary>
    /// A form holding a summary, one kit input and a consumer-shaped component, so a render of the
    /// form's fragment is observable from inside it.
    /// </summary>
    private IRenderedComponent<FormidableForm<EngineOrder>> RenderForm(EngineOrder order) =>
        Render(builder =>
        {
            builder.OpenComponent<FormidableForm<EngineOrder>>(0);
            builder.AddComponentParameter(1, nameof(FormidableForm<EngineOrder>.Model), order);
            builder.AddComponentParameter(
                2,
                nameof(FormidableForm<EngineOrder>.Validator),
                new FluentValidationModelValidator<EngineOrder>(new DeclarationOrderValidator()));
            builder.AddComponentParameter(
                3,
                nameof(FormidableForm<EngineOrder>.ChildContent),
                (RenderFragment<FormidableFormContext>)(_ => inner =>
                {
                    inner.OpenComponent<FormidableSummary>(0);
                    inner.CloseComponent();

                    inner.OpenComponent<FormidableInputText>(1);
                    inner.AddComponentParameter(
                        2, nameof(FormidableInputText.For), (Expression<Func<string?>>)(() => order.Description));
                    inner.AddComponentParameter(3, "Value", order.Description);
                    inner.AddComponentParameter(
                        4,
                        "ValueChanged",
                        EventCallback.Factory.Create<string?>(this, v => order.Description = v ?? string.Empty));
                    inner.CloseComponent();

                    inner.OpenComponent<ParameterSetCounter>(5);
                    inner.AddComponentParameter(6, nameof(ParameterSetCounter.Token), order);
                    inner.CloseComponent();
                }));
            builder.CloseComponent();
        }).FindComponent<FormidableForm<EngineOrder>>();

    /// <summary>
    /// Stands in for a consumer's component inside the form that does work when its parameters are
    /// set: it counts those sets. Its parameter is an object, which Blazor cannot prove unchanged,
    /// so every render of the fragment above re-sets it, as it does for a component handed a model,
    /// a list or a callback.
    /// </summary>
    private sealed class ParameterSetCounter : ComponentBase
    {
        [Parameter]
        public object? Token { get; set; }

        public int ParameterSets { get; private set; }

        protected override void OnParametersSet() => ParameterSets++;

        protected override void BuildRenderTree(RenderTreeBuilder builder) =>
            builder.AddMarkupContent(0, "<span></span>");
    }

    /// <summary>
    /// Answers each <see cref="IFormidableFieldOrderService.OrderAsync"/> call with a task the test
    /// completes by hand, one per call, so two resolves can be held open together.
    /// </summary>
    private sealed class HeldFieldOrderService : IFormidableFieldOrderService
    {
        public List<TaskCompletionSource<IReadOnlyList<FieldIdentifier>?>> Calls { get; } = [];

        public ValueTask<IReadOnlyList<FieldIdentifier>?> OrderAsync(IReadOnlyList<FieldIdentifier> fields)
        {
            var call = new TaskCompletionSource<IReadOnlyList<FieldIdentifier>?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Calls.Add(call);
            return new ValueTask<IReadOnlyList<FieldIdentifier>?>(call.Task);
        }
    }
}
