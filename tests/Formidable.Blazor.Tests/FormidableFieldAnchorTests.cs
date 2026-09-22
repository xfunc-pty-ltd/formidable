using Bunit;
using Formidable.Blazor.Tests.Fixtures;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;

namespace Formidable.Blazor.Tests;

public class FormidableFieldAnchorTests : BunitContext
{
    public FormidableFieldAnchorTests()
    {
        Services.AddFormidable();
        Services.AddSingleton<FluentValidation.IValidator<EngineOrder>, EngineOrderValidator>();
    }

    [Fact]
    public void Field_id_is_deterministic_and_instance_scoped()
    {
        var a = new EngineItem();
        var b = new EngineItem();

        Assert.Equal(FormidableFieldId.For(new FieldIdentifier(a, "Sku")), FormidableFieldId.For(new FieldIdentifier(a, "Sku")));
        Assert.NotEqual(FormidableFieldId.For(new FieldIdentifier(a, "Sku")), FormidableFieldId.For(new FieldIdentifier(b, "Sku")));
        Assert.EndsWith("-form", FormidableFieldId.For(new FieldIdentifier(a, string.Empty)));
        Assert.DoesNotContain(FormidableFieldId.For(new FieldIdentifier(a, "Location.X")), ".");
    }

    [Fact]
    public void Expression_overload_matches_the_string_path()
    {
        var order = new EngineOrder();

        Assert.Equal(
            FormidableFieldId.For(new FieldIdentifier(order, nameof(EngineOrder.Description))),
            FormidableFieldId.For(order, o => o.Description));
    }

    // Adaptation: bunit 2.9.0 does not dispose the first tree when a second top-level `Render`
    // call replaces it on the same BunitContext, so the anchor is rendered behind a bool flag
    // component parameter and flipped via re-parameterization
    // (`IRenderedComponent<T>.Render(...)`, the v2 equivalent of SetParametersAndRender) instead of
    // replacing the whole tree. The assertions are unchanged: reveal on register, hide on dispose.
    [Fact]
    public void Anchor_registers_and_unregisters_with_the_form_registry()
    {
        var order = new EngineOrder();
        var cut = Render(builder =>
        {
            builder.OpenComponent<AnchorHost>(0);
            builder.AddComponentParameter(1, nameof(AnchorHost.Order), order);
            builder.AddComponentParameter(2, nameof(AnchorHost.ShowAnchor), true);
            builder.CloseComponent();
        });

        var host = cut.FindComponent<AnchorHost>();
        var form = cut.FindComponent<FormidableForm<EngineOrder>>();
        Assert.True(form.Instance.Engine!.Registry.IsRevealed(new FieldIdentifier(order, nameof(EngineOrder.Description))));

        host.Render(parameters => parameters.Add(p => p.ShowAnchor, false)); // re-parameterize -> anchor leaves the tree and disposes
        Assert.False(form.Instance.Engine!.Registry.IsRevealed(new FieldIdentifier(order, nameof(EngineOrder.Description))));
    }

    // This deliberately does NOT route through FormidableForm/EditForm. EditForm tears down and
    // recreates its entire descendant subtree whenever its EditContext instance changes (it opens
    // a render region keyed on `_editContext.GetHashCode()` specifically so its internal
    // `CascadingValue<EditContext> IsFixed="true"` is safe — see EditForm.BuildRenderTree). That
    // means a FormidableFieldAnchor nested inside a FormidableForm/FormidableValidator-wrapped
    // EditForm is ALWAYS disposed and freshly constructed on a model swap, regardless of whether
    // FormidableFieldAnchor itself rebinds on cascading-parameter changes — so a test built on
    // top of FormidableForm cannot distinguish fixed from unfixed FormidableFieldAnchor code.
    // Cascading a FormidableFormContext directly (no EditForm underneath) isolates
    // FormidableFieldAnchor's own contract: it must rebind when
    // the cascaded context instance changes, independent of whatever caused that change.
    [Fact]
    public void Anchor_rebinds_registration_when_the_cascaded_context_is_replaced()
    {
        var order = new EngineOrder();
        var field = new FieldIdentifier(order, nameof(EngineOrder.Description));
        RenderFragment fieldFragment = inner =>
        {
            inner.OpenComponent<FormidableFieldAnchor<string>>(0);
            inner.AddComponentParameter(1, nameof(FormidableFieldAnchor<string>.For), (System.Linq.Expressions.Expression<Func<string>>)(() => order.Description));
            inner.CloseComponent();
        };

        var firstEngine = CreateEngine(order);
        var firstContext = new FormidableFormContext(firstEngine);
        var cut = Render(builder =>
        {
            builder.OpenComponent<CascadedContextHost>(0);
            builder.AddComponentParameter(1, nameof(CascadedContextHost.Context), firstContext);
            builder.AddComponentParameter(2, nameof(CascadedContextHost.ChildContent), fieldFragment);
            builder.CloseComponent();
        });

        Assert.True(firstEngine.Registry.IsRevealed(field));

        var secondEngine = CreateEngine(order);
        var secondContext = new FormidableFormContext(secondEngine);
        var host = cut.FindComponent<CascadedContextHost>();
        host.Render(parameters =>
        {
            parameters.Add(p => p.Context, secondContext);
            parameters.Add(p => p.ChildContent, fieldFragment);
        });

        Assert.True(secondEngine.Registry.IsRevealed(field));
        Assert.False(firstEngine.Registry.IsRevealed(field));
    }

    private static FormValidationEngine<EngineOrder> CreateEngine(EngineOrder order) =>
        new(order, new EditContext(order),
            new FluentValidationModelValidator<EngineOrder>(new EngineOrderValidator()),
            new Formidable.Introspection.ReflectionModelIntrospector(),
            new FormidableOptions());

    [Fact]
    public void Anchor_outside_a_form_throws_clearly()
    {
        var order = new EngineOrder();
        var exception = Assert.ThrowsAny<Exception>(() => Render(builder =>
        {
            builder.OpenComponent<FormidableFieldAnchor<string>>(0);
            builder.AddComponentParameter(1, "For", (System.Linq.Expressions.Expression<Func<string>>)(() => order.Description));
            builder.CloseComponent();
        }));

        Assert.Contains("FormidableForm", exception.Message);
    }

    /// <summary>Test-only host so the anchor's presence can be toggled via a parameter re-render (see adaptation note above).</summary>
    private sealed class AnchorHost : ComponentBase
    {
        [Parameter]
        public EngineOrder Order { get; set; } = default!;

        [Parameter]
        public bool ShowAnchor { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<FormidableForm<EngineOrder>>(0);
            builder.AddComponentParameter(1, nameof(FormidableForm<EngineOrder>.Model), Order);
            builder.AddComponentParameter(2, nameof(FormidableForm<EngineOrder>.ChildContent), (RenderFragment)(inner =>
            {
                if (ShowAnchor)
                {
                    inner.OpenComponent<FormidableFieldAnchor<string>>(0);
                    inner.AddComponentParameter(1, nameof(FormidableFieldAnchor<string>.For), (System.Linq.Expressions.Expression<Func<string>>)(() => Order.Description));
                    inner.CloseComponent();
                }
            }));
            builder.CloseComponent();
        }
    }

    /// <summary>Test-only host that cascades a FormidableFormContext directly, with no EditForm underneath (see the rebind test's remarks).</summary>
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
            builder.AddComponentParameter(2, "ChildContent", ChildContent);
            builder.CloseComponent();
        }
    }
}
