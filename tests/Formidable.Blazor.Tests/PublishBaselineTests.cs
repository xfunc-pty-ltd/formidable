using System.Linq.Expressions;
using Bunit;
using FluentValidation;
using Formidable.Blazor.Tests.Fixtures;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;

namespace Formidable.Blazor.Tests;

/// <summary>
/// The views a publish keeps for the next publish's diff cover the fields the engine still holds
/// something for, so a form whose rows are replaced under one engine (a grid reloading after each
/// save) does not pay a state read per departed row at every later publish. The departed rows'
/// fields read green here, because a blocked submit has made coverage fresh, which is the shape a
/// baseline keeping every non-default view would still grow on.
/// </summary>
public class PublishBaselineTests : BunitContext
{
    private const int Replacements = 5;

    public PublishBaselineTests()
    {
        Services.AddSingleton<IFormidableFocusService>(new RecordingFocusService());
        Services.AddSingleton<IFormidableFieldOrderService>(new RecordingFieldOrderService());
        Services.AddSingleton<IFormidableDomValueSync>(new RecordingDomValueSync());
        Services.AddSingleton<IValidator<EngineOrder>, DeclarationOrderValidator>();
        Services.AddFormidableBlazor();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public async Task Replacing_a_keyed_rows_object_does_not_grow_the_baseline()
    {
        var order = new EngineOrder { Description = "valid desc", Items = [new EngineItem { Sku = "first" }] };
        var cut = Render<RowChurnHost>(p => p.Add(x => x.Order, order));
        var form = cut.FindComponent<FormidableForm<EngineOrder>>().Instance;
        var engine = (FormidableEngine<EngineOrder>)form.Engine!;
        await cut.InvokeAsync(() => form.SubmitAsync());   // blocked by the form-level rule: coverage is fresh, every passing field green

        var counts = new List<int>();
        for (var round = 1; round <= Replacements; round++)
        {
            order.Items[0] = new EngineItem { Sku = $"row {round}" };
            cut.Render(p => p.Add(x => x.Order, order));   // the keyed row rebuilds: the old row's input leaves, the new one registers
            await cut.InvokeAsync(() => cut.Find("input[id$='-description']").Change($"valid desc {round}"));
            cut.WaitForState(() => !engine.IsValidating, TimeSpan.FromSeconds(5));
            await cut.InvokeAsync(() => { });
            counts.Add(engine.PublishedViewCount);
        }

        Assert.True(
            counts[^1] == counts[0],
            $"the baseline grew across {Replacements} row replacements: {string.Join(", ", counts)}");

        await Services.DisposeAsync();
    }

    /// <summary>A form with one stable input and one keyed row per item; the inputs' ValueChanged goes to a sink, so the host does not re-render per edit.</summary>
    private sealed class RowChurnHost : ComponentBase
    {
        private static readonly FormidableOptions Options = new() { RefreshDebounce = Timeout.InfiniteTimeSpan };

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
                Input(inner, 0, () => Order.Description, sink.SetDescription);
                foreach (var item in Order.Items)
                {
                    inner.OpenElement(4, "div");
                    inner.SetKey(item);
                    Input(inner, 5, () => item.Sku, value => item.Sku = value ?? string.Empty);
                    inner.CloseElement();
                }
            }));
            builder.CloseComponent();
        }

        private static void Input(RenderTreeBuilder b, int seq, Expression<Func<string?>> accessor, Action<string?> setter)
        {
            b.OpenComponent<FormidableInputText>(seq);
            b.AddComponentParameter(seq + 1, nameof(FormidableInputText.For), accessor);
            b.AddComponentParameter(seq + 2, "Value", accessor.Compile()());
            b.AddComponentParameter(seq + 3, "ValueChanged", EventCallback.Factory.Create(setter.Target!, setter));
            b.CloseComponent();
        }

        private sealed class Sink(EngineOrder order)
        {
            public void SetDescription(string? value) => order.Description = value ?? string.Empty;
        }
    }
}
