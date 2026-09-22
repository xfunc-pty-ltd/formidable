using Bunit;
using Bunit.Rendering;
using Formidable.Blazor.Tests.Fixtures;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;

namespace Formidable.Blazor.Tests;

public class FormidableValidatorComponentTests : BunitContext
{
    public FormidableValidatorComponentTests()
    {
        Services.AddFormidable();
        Services.AddSingleton<FluentValidation.IValidator<EngineOrder>, EngineOrderValidator>();
    }

    private IRenderedComponent<ContainerFragment> RenderForm(EngineOrder order, FormidableOptions? options = null) =>
        Render(builder =>
        {
            builder.OpenComponent<EditForm>(0);
            builder.AddComponentParameter(1, nameof(EditForm.Model), order);
            builder.AddComponentParameter(2, nameof(EditForm.ChildContent), (RenderFragment<EditContext>)(_ => inner =>
            {
                inner.OpenComponent<FormidableValidator<EngineOrder>>(0);
                if (options is not null)
                {
                    inner.AddComponentParameter(1, nameof(FormidableValidator<EngineOrder>.Options), options);
                }
                inner.CloseComponent();
                inner.OpenComponent<ValidationSummary>(2);
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        });

    [Fact]
    public void Attaches_engine_and_shows_live_error_on_field_change()
    {
        var order = new EngineOrder { Description = new string('x', 11) };
        var cut = RenderForm(order, new FormidableOptions { DisclosureOverride = _ => true });

        var editContext = cut.FindComponent<EditForm>().Instance.EditContext!;
        cut.InvokeAsync(() => editContext.NotifyFieldChanged(new FieldIdentifier(order, nameof(EngineOrder.Description))));

        cut.WaitForAssertion(() => Assert.Contains("10", cut.Markup));
    }

    [Fact]
    public void Cascades_form_context_to_descendants()
    {
        var order = new EngineOrder();
        FormidableFormContext? seen;
        Render(builder =>
        {
            builder.OpenComponent<EditForm>(0);
            builder.AddComponentParameter(1, nameof(EditForm.Model), order);
            builder.AddComponentParameter(2, nameof(EditForm.ChildContent), (RenderFragment<EditContext>)(_ => inner =>
            {
                inner.OpenComponent<FormidableValidator<EngineOrder>>(0);
                inner.AddComponentParameter(1, nameof(FormidableValidator<EngineOrder>.ChildContent), (RenderFragment)(ctx =>
                {
                    ctx.OpenComponent<ContextProbe>(0);
                    ctx.CloseComponent();
                }));
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        });
        seen = ContextProbe.LastContext;

        Assert.NotNull(seen);
        Assert.Same(order, seen!.EditContext.Model);
        Assert.NotNull(seen.Registry);
        Assert.NotNull(seen.Engine);
    }

    [Fact]
    public void Missing_cascading_edit_context_throws_clearly()
    {
        var exception = Assert.ThrowsAny<Exception>(() =>
            Render(builder =>
            {
                builder.OpenComponent<FormidableValidator<EngineOrder>>(0);
                builder.CloseComponent();
            }));

        Assert.Contains("EditForm", exception.Message);
    }

    private sealed class ContextProbe : ComponentBase
    {
        public static FormidableFormContext? LastContext;

        [CascadingParameter]
        public FormidableFormContext? Context { get; set; }

        protected override void OnParametersSet() => LastContext = Context;
    }
}
