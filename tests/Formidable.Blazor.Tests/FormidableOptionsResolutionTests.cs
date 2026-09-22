using Bunit;
using Bunit.Rendering;
using Formidable.Blazor.Tests.Fixtures;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;

namespace Formidable.Blazor.Tests;

// Covers how an Options parameter is resolved: where an omitted one comes from, and what a host
// does when the parameter is handed a different instance after the engine that read it already
// exists. Building one instance from another is FormidableOptionsCopyTests.
public class FormidableOptionsResolutionTests : BunitContext
{
    private static readonly TimeSpan ConfiguredDebounce = TimeSpan.FromMilliseconds(42);
    private static readonly TimeSpan ParameterDebounce = TimeSpan.FromMilliseconds(77);

    public FormidableOptionsResolutionTests()
    {
        Services.AddSingleton<FluentValidation.IValidator<EngineOrder>, EngineOrderValidator>();

        // RenderForm's FormidableForm carries no fields at all, but the model-level field is
        // still offered to the order service on its first render, and the form puts its layout
        // observer on the same module then too — so every test through here needs the module
        // answered, not just the ones that care about ordering.
        var module = JSInterop.SetupModule("./_content/Formidable.Blazor/formidable.js");
        module.Setup<IReadOnlyList<string>>("orderFields", _ => true).SetResult([]);
        module.SetupVoid("observeLayout", _ => true).SetVoidResult();
        module.SetupVoid("disconnectLayoutObserver", _ => true).SetVoidResult();
    }

    [Fact]
    public void Configured_defaults_apply_when_the_Options_parameter_is_omitted()
    {
        Services.AddFormidableBlazor(options => options.RefreshDebounce = ConfiguredDebounce);

        var cut = RenderForm(new EngineOrder());

        Assert.Equal(ConfiguredDebounce, cut.Instance.Engine!.Options.RefreshDebounce);
    }

    [Fact]
    public void The_Options_parameter_wins_over_configured_defaults()
    {
        Services.AddFormidableBlazor(options => options.RefreshDebounce = ConfiguredDebounce);

        var cut = RenderForm(new EngineOrder(), new FormidableOptions { RefreshDebounce = ParameterDebounce });

        Assert.Equal(ParameterDebounce, cut.Instance.Engine!.Options.RefreshDebounce);
    }

    [Fact]
    public void Built_in_defaults_apply_when_nothing_is_configured()
    {
        Services.AddFormidableBlazor();

        var cut = RenderForm(new EngineOrder());

        Assert.Equal(new FormidableOptions().RefreshDebounce, cut.Instance.Engine!.Options.RefreshDebounce);
    }

    [Fact]
    public void Configured_defaults_reach_the_attach_mode_host_too()
    {
        Services.AddFormidableBlazor(options => options.RefreshDebounce = ConfiguredDebounce);
        var order = new EngineOrder();

        var cut = RenderAttached(order, options: null);

        Assert.Equal(
            ConfiguredDebounce,
            cut.FindComponent<ContextProbe>().Instance.Context!.Engine.Options.RefreshDebounce);
    }

    [Fact]
    public void Changing_only_the_Options_reference_throws_instead_of_being_ignored()
    {
        Services.AddFormidableBlazor();
        var cut = RenderForm(new EngineOrder(), new FormidableOptions());

        var exception = Assert.ThrowsAny<Exception>(() =>
            cut.Render(parameters => parameters.Add(p => p.Options, new FormidableOptions())));

        Assert.Contains("Options is read once", exception.Message);
        Assert.Contains("FormidableForm", exception.Message);
    }

    [Fact]
    public void Changing_Model_and_Options_together_rebuilds_with_the_new_options()
    {
        Services.AddFormidableBlazor();
        var cut = RenderForm(new EngineOrder(), new FormidableOptions());
        var first = cut.Instance.Engine!;

        cut.Render(parameters => parameters
            .Add(p => p.Model, new EngineOrder())
            .Add(p => p.Options, new FormidableOptions { RefreshDebounce = ParameterDebounce }));

        Assert.NotSame(first, cut.Instance.Engine);
        Assert.Equal(ParameterDebounce, cut.Instance.Engine!.Options.RefreshDebounce);
    }

    [Fact]
    public void Re_rendering_with_the_same_Options_instance_is_fine()
    {
        Services.AddFormidableBlazor();
        var options = new FormidableOptions();
        var cut = RenderForm(new EngineOrder(), options);
        var first = cut.Instance.Engine!;

        cut.Render(parameters => parameters.Add(p => p.Options, options));

        Assert.Same(first, cut.Instance.Engine);
    }

    [Fact]
    public void Changing_only_the_Options_reference_throws_in_attach_mode_too()
    {
        Services.AddFormidableBlazor();
        var order = new EngineOrder();
        var cut = RenderAttached(order, new FormidableOptions());

        var exception = Assert.ThrowsAny<Exception>(() =>
            cut.FindComponent<FormidableValidator<EngineOrder>>()
                .Render(parameters => parameters.Add(p => p.Options, new FormidableOptions())));

        Assert.Contains("Options is read once", exception.Message);
        Assert.Contains("FormidableValidator", exception.Message);
    }

    private IRenderedComponent<FormidableForm<EngineOrder>> RenderForm(
        EngineOrder order, FormidableOptions? options = null)
    {
        var container = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<EngineOrder>>(0);
            builder.AddComponentParameter(1, nameof(FormidableForm<EngineOrder>.Model), order);
            if (options is not null)
            {
                builder.AddComponentParameter(2, nameof(FormidableForm<EngineOrder>.Options), options);
            }
            builder.CloseComponent();
        });

        return container.FindComponent<FormidableForm<EngineOrder>>();
    }

    private IRenderedComponent<ContainerFragment> RenderAttached(EngineOrder order, FormidableOptions? options) =>
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
                inner.AddComponentParameter(2, nameof(FormidableValidator<EngineOrder>.ChildContent), (RenderFragment<FormidableFormContext>)(_ => ctx =>
                {
                    ctx.OpenComponent<ContextProbe>(0);
                    ctx.CloseComponent();
                }));
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        });

    private sealed class ContextProbe : ComponentBase
    {
        [CascadingParameter]
        public FormidableFormContext? Context { get; set; }
    }
}
