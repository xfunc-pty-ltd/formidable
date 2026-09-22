using Bunit;
using Formidable.Blazor.Tests.Fixtures;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;

namespace Formidable.Blazor.Tests;

/// <summary>
/// Pins how often a kit input asks the engine about its field while rendering: the class,
/// <c>aria-invalid</c> and <c>aria-describedby</c> answer from one read of the field's state and
/// one read of its issues, not from one read each, and <c>aria-required</c> costs one read of the
/// field's requirement on top of them rather than one per attribute. A counting engine forwards to
/// a real one, so what the input renders is unchanged and the assertion is the call count itself —
/// the markup is pinned by the input tests instead.
/// </summary>
public class FormidableInputRenderTests : BunitContext
{
    public FormidableInputRenderTests()
    {
        Services.AddFormidable();
        Services.AddSingleton<FluentValidation.IValidator<EngineOrder>, EngineOrderValidator>();
    }

    [Fact]
    public void Input_reads_the_field_once_per_render()
    {
        var order = new EngineOrder();
        var engine = new CountingEngine(CreateEngine(order));
        var cut = RenderCascaded(order, engine);
        var input = cut.FindComponent<FormidableInputText>();

        Assert.Equal(input.RenderCount, engine.FieldStateReads);
        Assert.Equal(input.RenderCount, engine.IssueReads);
        Assert.Equal(input.RenderCount, engine.RequirementReads);
    }

    [Fact]
    public void Each_render_reads_the_field_again_rather_than_reusing_the_last_one()
    {
        var order = new EngineOrder();
        var engine = new CountingEngine(CreateEngine(order));
        var cut = RenderCascaded(order, engine);
        var input = cut.FindComponent<FormidableInputText>();
        var field = new FieldIdentifier(order, nameof(EngineOrder.Description));

        // Three conditional attributes now enter the render tree under one shared sequence
        // number, and only two of them follow the field's state — so what the rounds below check
        // is that the third is unmoved by the other two arriving and leaving: aria-required is
        // there before the field has ever failed, through the round that adds two attributes
        // beside it, and through the round that takes them away again. Mutation that must break
        // this: folding the aria-required branch in with the issue-gated ones, the tidy-looking
        // edit that would make a demand of the rules follow a verdict about the values instead.
        // Sharing the number is NOT what is at risk here and a test cannot pin that it is:
        // moving aria-required onto the class's sequence number breaks nothing, because
        // attribute frames diff by name rather than by sequence — which is the assumption the
        // shared-number idiom rests on in the first place.
        Assert.Equal("true", cut.Find("input").GetAttribute("aria-required"));

        order.Description = new string('x', 11); // past the draft rule's maximum length
        cut.InvokeAsync(() => engine.EditContext.NotifyFieldChanged(field));

        cut.WaitForAssertion(() =>
        {
            var element = cut.Find("input");
            Assert.Contains("formidable-invalid", element.GetAttribute("class"));
            Assert.Equal("true", element.GetAttribute("aria-invalid"));
            Assert.Equal("true", element.GetAttribute("aria-required"));
        });

        order.Description = "ok";
        cut.InvokeAsync(() => engine.EditContext.NotifyFieldChanged(field));

        cut.WaitForAssertion(() =>
        {
            var element = cut.Find("input");
            Assert.Null(element.GetAttribute("aria-invalid"));
            Assert.Equal("true", element.GetAttribute("aria-required"));
        });

        Assert.True(input.RenderCount > 1, $"expected a re-render, saw {input.RenderCount}");
        Assert.Equal(input.RenderCount, engine.FieldStateReads);
        Assert.Equal(input.RenderCount, engine.IssueReads);
        Assert.Equal(input.RenderCount, engine.RequirementReads);
    }

    private IRenderedComponent<CascadedContextHost> RenderCascaded(EngineOrder order, IFormValidationEngine engine)
    {
        RenderFragment inputFragment = inner =>
        {
            inner.OpenComponent<FormidableInputText>(0);
            inner.AddComponentParameter(1, "For", (System.Linq.Expressions.Expression<Func<string?>>)(() => order.Description));
            inner.AddComponentParameter(2, "Value", order.Description);
            inner.AddComponentParameter(3, "ValueChanged",
                EventCallback.Factory.Create<string?>(this, v => order.Description = v ?? string.Empty));
            inner.CloseComponent();
        };

        var cut = Render(builder =>
        {
            builder.OpenComponent<CascadedContextHost>(0);
            builder.AddComponentParameter(1, nameof(CascadedContextHost.Context), new FormidableFormContext(engine));
            builder.AddComponentParameter(2, nameof(CascadedContextHost.ChildContent), inputFragment);
            builder.CloseComponent();
        });

        return cut.FindComponent<CascadedContextHost>();
    }

    private static FormValidationEngine<EngineOrder> CreateEngine(EngineOrder order) =>
        new(order, new EditContext(order),
            new FluentValidationModelValidator<EngineOrder>(new EngineOrderValidator()),
            new Formidable.Introspection.ReflectionModelIntrospector(),
            new FormidableOptions());

    /// <summary>Cascades a context with no EditForm underneath, so only the input renders.</summary>
    private sealed class CascadedContextHost : ComponentBase
    {
        [Parameter]
        public FormidableFormContext? Context { get; set; }

        [Parameter]
        public RenderFragment? ChildContent { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<CascadingValue<FormidableFormContext>>(0);
            builder.AddComponentParameter(1, "Value", Context);
            builder.AddComponentParameter(2, "IsFixed", true);
            builder.AddComponentParameter(3, "ChildContent", ChildContent);
            builder.CloseComponent();
        }
    }

    /// <summary>
    /// Forwards every call to a real engine, counting the three a render makes. Everything the input
    /// sees — state, issues, notifications — is the real engine's, so the counts describe the
    /// component's own behaviour and nothing else.
    /// </summary>
    private sealed class CountingEngine(IFormValidationEngine inner) : IFormValidationEngine
    {
        public int FieldStateReads { get; private set; }

        public int IssueReads { get; private set; }

        public int RequirementReads { get; private set; }

        public EditContext EditContext => inner.EditContext;

        public FieldRegistry Registry => inner.Registry;

        public FormidableOptions Options => inner.Options;

        public bool IsValidating => inner.IsValidating;

        public bool HasSubmitted => inner.HasSubmitted;

        public bool IsFormValid => inner.IsFormValid;

        public event EventHandler<FormidableStateChangedEventArgs>? StateChanged
        {
            add => inner.StateChanged += value;
            remove => inner.StateChanged -= value;
        }

        public event EventHandler<FormidableValidationFaultedEventArgs>? ValidationFaulted
        {
            add => inner.ValidationFaulted += value;
            remove => inner.ValidationFaulted -= value;
        }

        public FieldState GetFieldState(FieldIdentifier field)
        {
            FieldStateReads++;
            return inner.GetFieldState(field);
        }

        public IReadOnlyList<ValidationIssue> GetIssues(FieldIdentifier field)
        {
            IssueReads++;
            return inner.GetIssues(field);
        }

        public FieldRequirement GetFieldRequirement(FieldIdentifier field)
        {
            RequirementReads++;
            return inner.GetFieldRequirement(field);
        }

        public IReadOnlyList<VisibleIssue> GetVisibleIssues() => inner.GetVisibleIssues();

        public void MarkTouched(FieldIdentifier field) => inner.MarkTouched(field);

        public Task<SubmitOutcome> ValidateForSubmitAsync(CancellationToken cancellationToken = default) =>
            inner.ValidateForSubmitAsync(cancellationToken);

        public Task DiscloseLoadedValuesAsync(CancellationToken cancellationToken = default) =>
            inner.DiscloseLoadedValuesAsync(cancellationToken);

        public void ApplyServerIssues(IEnumerable<ValidationIssue> issues) => inner.ApplyServerIssues(issues);
    }
}
