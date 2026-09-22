using Bunit;
using Formidable.Blazor.Tests.Fixtures;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;

namespace Formidable.Blazor.Tests;

public class FormidableFieldTests : BunitContext
{
    public FormidableFieldTests()
    {
        Services.AddFormidable();
        Services.AddSingleton<FluentValidation.IValidator<EngineOrder>, EngineOrderValidator>();
    }

    [Fact]
    public void Field_context_exposes_state_issues_and_aria()
    {
        var order = new EngineOrder { Description = new string('x', 11) };
        FormidableFieldContext? seen = null;
        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<EngineOrder>>(0);
            builder.AddComponentParameter(1, "Model", order);
            builder.AddComponentParameter(2, "ChildContent", (RenderFragment<FormidableFormContext>)(_ => inner =>
            {
                inner.OpenComponent<FormidableField<string>>(0);
                inner.AddComponentParameter(1, "For", (System.Linq.Expressions.Expression<Func<string>>)(() => order.Description));
                inner.AddComponentParameter(2, "ChildContent", (RenderFragment<FormidableFieldContext>)(ctx => b => { seen = ctx; }));
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        });

        var form = cut.FindComponent<FormidableForm<EngineOrder>>();
        cut.InvokeAsync(() => form.Instance.Engine!.EditContext.NotifyFieldChanged(
            new FieldIdentifier(order, nameof(EngineOrder.Description))));

        cut.WaitForAssertion(() =>
        {
            Assert.NotNull(seen);
            Assert.True(seen!.State.HasErrors);
            Assert.Contains("formidable-invalid", seen.CssClass);
            Assert.NotEmpty(seen.Issues);
            Assert.True(seen.AriaInvalid);
            Assert.Equal($"{seen.ElementId}-messages", seen.AriaDescribedBy);
        });
    }

    [Fact]
    public void InputAttributes_bundles_id_class_and_every_applicable_aria_key_when_invalid()
    {
        var order = new EngineOrder { Description = new string('x', 11) };
        FormidableFieldContext? seen = null;
        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<EngineOrder>>(0);
            builder.AddComponentParameter(1, "Model", order);
            builder.AddComponentParameter(2, "ChildContent", (RenderFragment<FormidableFormContext>)(_ => inner =>
            {
                inner.OpenComponent<FormidableField<string>>(0);
                inner.AddComponentParameter(1, "For", (System.Linq.Expressions.Expression<Func<string>>)(() => order.Description));
                inner.AddComponentParameter(2, "ChildContent", (RenderFragment<FormidableFieldContext>)(ctx => b => { seen = ctx; }));
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        });

        var form = cut.FindComponent<FormidableForm<EngineOrder>>();
        cut.InvokeAsync(() => form.Instance.Engine!.EditContext.NotifyFieldChanged(
            new FieldIdentifier(order, nameof(EngineOrder.Description))));

        cut.WaitForAssertion(() =>
        {
            Assert.NotNull(seen);
            Assert.True(seen!.State.HasErrors);
            var attributes = seen.InputAttributes;
            Assert.Equal(5, attributes.Count);
            Assert.Equal(seen.ElementId, attributes["id"]);
            Assert.Equal(seen.CssClass, attributes["class"]);
            Assert.Equal("true", attributes["aria-invalid"]);
            Assert.Equal(seen.AriaDescribedBy, attributes["aria-describedby"]);
            // Description carries the submit bucket's NotEmpty(), so the splat says so too — a
            // control splatting this bundle announces the demand without wiring anything itself.
            Assert.Equal("true", attributes["aria-required"]);
            Assert.Equal(FieldRequirement.Required, seen.Requirement);
        });
    }

    [Fact]
    public void InputAttributes_omits_the_issue_driven_aria_keys_when_field_is_valid_and_untouched()
    {
        var order = new EngineOrder();
        FormidableFieldContext? seen = null;
        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<EngineOrder>>(0);
            builder.AddComponentParameter(1, "Model", order);
            builder.AddComponentParameter(2, "ChildContent", (RenderFragment<FormidableFormContext>)(_ => inner =>
            {
                inner.OpenComponent<FormidableField<string>>(0);
                inner.AddComponentParameter(1, "For", (System.Linq.Expressions.Expression<Func<string>>)(() => order.Description));
                inner.AddComponentParameter(2, "ChildContent", (RenderFragment<FormidableFieldContext>)(ctx => b => { seen = ctx; }));
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        });

        Assert.NotNull(seen);
        var attributes = seen!.InputAttributes;
        Assert.Equal(3, attributes.Count);
        Assert.Equal(seen.ElementId, attributes["id"]);
        Assert.Equal(seen.CssClass, attributes["class"]);
        Assert.False(attributes.ContainsKey("aria-invalid"));
        Assert.False(attributes.ContainsKey("aria-describedby"));
        // aria-required is not one of the keys a clean field drops: it reports what the rules
        // demand of the field, which is true of an untouched field and of a passing one alike.
        Assert.Equal("true", attributes["aria-required"]);
    }

    [Fact]
    public void Field_registers_for_disclosure()
    {
        var order = new EngineOrder();
        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<EngineOrder>>(0);
            builder.AddComponentParameter(1, "Model", order);
            builder.AddComponentParameter(2, "ChildContent", (RenderFragment<FormidableFormContext>)(_ => inner =>
            {
                inner.OpenComponent<FormidableField<string>>(0);
                inner.AddComponentParameter(1, "For", (System.Linq.Expressions.Expression<Func<string>>)(() => order.Description));
                inner.AddComponentParameter(2, "ChildContent", (RenderFragment<FormidableFieldContext>)(ctx => b => { }));
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        });

        var form = cut.FindComponent<FormidableForm<EngineOrder>>();
        Assert.True(form.Instance.Engine!.Registry.IsRegistered(new FieldIdentifier(order, nameof(EngineOrder.Description))));
    }

    [Fact]
    public void Native_input_base_gets_provider_class_names()
    {
        var order = new EngineOrder { Description = new string('x', 11) };
        var engine = new FormValidationEngine<EngineOrder>(
            order, new EditContext(order),
            new FluentValidationModelValidator<EngineOrder>(new EngineOrderValidator()),
            new Formidable.Introspection.ReflectionModelIntrospector(),
            new FormidableOptions(), new Microsoft.Extensions.Time.Testing.FakeTimeProvider());

        var field = new FieldIdentifier(order, nameof(EngineOrder.Description));
        engine.EditContext.NotifyFieldChanged(field); // live error lands

        Assert.Equal("formidable-invalid", engine.EditContext.FieldCssClass(field));
    }

    // Regression test for a defect found in review: FormidableField captured its cascaded
    // FormidableFormContext once in OnInitialized and never rebound when the host swapped
    // models (FormidableForm/FormidableValidator rebuild engine + registry on Model change,
    // cascaded via a non-fixed CascadingValue). This deliberately does NOT route through
    // FormidableForm/EditForm: EditForm tears down and recreates its entire descendant subtree
    // whenever its EditContext instance changes (it opens a render region keyed on
    // `_editContext.GetHashCode()` specifically so its internal
    // `CascadingValue<EditContext> IsFixed="true"` is safe — see EditForm.BuildRenderTree). That
    // means a FormidableField nested inside a FormidableForm-wrapped EditForm is ALWAYS disposed
    // and freshly constructed on a model swap regardless of whether FormidableField itself
    // rebinds on cascading-parameter changes, so a test built on top of FormidableForm cannot
    // distinguish fixed from unfixed FormidableField code. Cascading a FormidableFormContext
    // directly (no EditForm underneath) isolates FormidableField's own contract: it must rebind
    // — both its registration and its engine StateChanged subscription — when the cascaded
    // context instance changes, independent of whatever caused that change.
    [Fact]
    public void Field_rebinds_when_the_cascaded_context_is_replaced_without_a_host_remount()
    {
        var order = new EngineOrder();
        var field = new FieldIdentifier(order, nameof(EngineOrder.Description));
        FormidableFieldContext? seen = null;
        RenderFragment fieldFragment = inner =>
        {
            inner.OpenComponent<FormidableField<string>>(0);
            inner.AddComponentParameter(1, "For", (System.Linq.Expressions.Expression<Func<string>>)(() => order.Description));
            inner.AddComponentParameter(2, "ChildContent", (RenderFragment<FormidableFieldContext>)(ctx => b => { seen = ctx; }));
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

        Assert.True(firstEngine.Registry.IsRegistered(field));

        var secondEngine = CreateEngine(order);
        var secondContext = new FormidableFormContext(secondEngine);
        var host = cut.FindComponent<CascadedContextHost>();
        host.Render(parameters =>
        {
            parameters.Add(p => p.Context, secondContext);
            parameters.Add(p => p.ChildContent, fieldFragment);
        });

        Assert.True(secondEngine.Registry.IsRegistered(field));
        Assert.False(firstEngine.Registry.IsRegistered(field));

        // The rendered context must now reflect the NEW engine: a live-pass validation on the
        // new engine's EditContext should reach `seen`. If the StateChanged subscription were
        // still wired to the disposed first engine, this would never update and the
        // WaitForAssertion below would time out.
        order.Description = new string('x', 11);
        cut.InvokeAsync(() => secondEngine.EditContext.NotifyFieldChanged(field));

        cut.WaitForAssertion(() =>
        {
            Assert.NotNull(seen);
            Assert.True(seen!.State.HasErrors);
            Assert.NotEmpty(seen.Issues);
        });
    }

    private static FormValidationEngine<EngineOrder> CreateEngine(EngineOrder order) =>
        new(order, new EditContext(order),
            new FluentValidationModelValidator<EngineOrder>(new EngineOrderValidator()),
            new Formidable.Introspection.ReflectionModelIntrospector(),
            new FormidableOptions());

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
