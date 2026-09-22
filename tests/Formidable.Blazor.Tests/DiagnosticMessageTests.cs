using Bunit;
using Bunit.Rendering;
using Formidable.Blazor.Tests.Fixtures;
using Formidable.Introspection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;

namespace Formidable.Blazor.Tests;

public class DiagnosticMessageTests : BunitContext
{
    public DiagnosticMessageTests()
    {
        // Everything FormidableForm resolves EXCEPT the validator seam — the null validator
        // resolution is the behavior under test. (Add further registrations here if
        // FormidableForm.cs resolves more services before its validator lookup.)
        Services.AddSingleton<IModelIntrospector, ReflectionModelIntrospector>();
    }

    [Fact]
    public void Missing_validator_message_uses_friendly_generic_names()
    {
        var model = new List<EngineItem>();

        var exception = Assert.ThrowsAny<Exception>(() => Render(builder =>
        {
            builder.OpenComponent<FormidableForm<List<EngineItem>>>(0);
            builder.AddComponentParameter(1, "Model", model);
            builder.CloseComponent();
        }));

        Assert.Contains("IModelValidator<List>", exception.Message);
        Assert.DoesNotContain("`", exception.Message);
    }

    [Fact]
    public void Missing_validator_message_names_the_Blazor_registration_call()
    {
        var exception = Assert.ThrowsAny<Exception>(() => RenderFormFor(new EngineOrder()));

        Assert.Contains("services.AddFormidableBlazor()", exception.Message);
        Assert.DoesNotContain("services.AddFormidable()", exception.Message);
    }

    [Fact]
    public void Missing_validator_message_names_the_Blazor_registration_call_in_attach_mode()
    {
        var exception = Assert.ThrowsAny<Exception>(() => RenderValidatorFor(new EngineOrder()));

        Assert.Contains("services.AddFormidableBlazor()", exception.Message);
        Assert.DoesNotContain("services.AddFormidable()", exception.Message);
    }

    [Fact]
    public void Missing_introspector_message_names_the_Blazor_registration_call()
    {
        using var empty = new BunitContext();
        empty.Services.AddSingleton<IModelValidator<EngineOrder>>(
            new FluentValidationModelValidator<EngineOrder>(new EngineOrderValidator()));

        var exception = Assert.ThrowsAny<Exception>(() => empty.Render(builder =>
        {
            builder.OpenComponent<FormidableForm<EngineOrder>>(0);
            builder.AddComponentParameter(1, "Model", new EngineOrder());
            builder.CloseComponent();
        }));

        Assert.Contains("IModelIntrospector", exception.Message);
        Assert.Contains("services.AddFormidableBlazor()", exception.Message);
    }

    // The likeliest first-run wiring mistake: the adapter IS registered (AddFormidable, which
    // AddFormidableBlazor calls, registers the open generic) but the consumer's own
    // FluentValidation validator is not. Resolving the adapter then THROWS during activation
    // instead of returning null, so this state has to be caught and named or the container's own
    // message — a resource key with no prose at all under WebAssembly trimming — is what the
    // reader gets.
    [Fact]
    public void Missing_fluentvalidation_validator_gets_Formidables_own_targeted_message()
    {
        Services.AddFormidable();

        var exception = Assert.ThrowsAny<Exception>(() => RenderFormFor(new EngineOrder()));

        Assert.Contains("No FluentValidation validator for 'EngineOrder' is registered", exception.Message);
        Assert.Contains("services.AddScoped<IValidator<EngineOrder>, EngineOrderValidator>()", exception.Message);
        Assert.Contains("AddValidatorsFromAssembly", exception.Message);
        Assert.DoesNotContain("Unable to resolve service for type", exception.Message);
    }

    [Fact]
    public void Missing_fluentvalidation_validator_preserves_the_container_exception()
    {
        Services.AddFormidable();

        var exception = Assert.ThrowsAny<Exception>(() => RenderFormFor(new EngineOrder()));

        Assert.NotNull(exception.InnerException);
        Assert.Contains("FluentValidation.IValidator", exception.InnerException!.Message);
    }

    [Fact]
    public void Missing_fluentvalidation_validator_gets_the_same_message_in_attach_mode()
    {
        Services.AddFormidable();

        var exception = Assert.ThrowsAny<Exception>(() => RenderValidatorFor(new EngineOrder()));

        Assert.Contains("No FluentValidation validator for 'EngineOrder' is registered", exception.Message);
        Assert.Contains("services.AddScoped<IValidator<EngineOrder>, EngineOrderValidator>()", exception.Message);
    }

    // The other side of the exception filter, and the one that decides whether a consumer gets a
    // TRUE or a FALSE diagnosis: an activation failure with some cause OTHER than a missing
    // FluentValidation validator must reach them as the container reported it. Here the validator
    // seam is filled by a type the container cannot build for its own unrelated reason, while
    // IValidator<EngineOrder> IS registered — so claiming a missing FluentValidation validator
    // would be a lie, and would bury the only text naming the real cause.
    [Fact]
    public void Activation_failure_with_another_cause_is_left_exactly_as_the_container_reported_it()
    {
        Services.AddSingleton<FluentValidation.IValidator<EngineOrder>, EngineOrderValidator>();
        Services.AddScoped<IModelValidator<EngineOrder>, UnbuildableValidator>();

        var exception = Assert.ThrowsAny<Exception>(() => RenderFormFor(new EngineOrder()));

        Assert.DoesNotContain("No FluentValidation validator for", exception.Message);
        Assert.DoesNotContain("AddValidatorsFromAssembly", exception.Message);
        Assert.Contains(nameof(IUnregisteredDependency), exception.Message);
        Assert.Null(exception.InnerException);
    }

    private interface IUnregisteredDependency;

    private sealed class UnbuildableValidator(IUnregisteredDependency dependency) : IModelValidator<EngineOrder>
    {
        private readonly IUnregisteredDependency _dependency = dependency;

        public Task<ValidationReport> ValidateAsync(
            EngineOrder model, ValidationProfile profile, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException(_dependency.ToString());

        public ValidationReport Validate(EngineOrder model, ValidationProfile profile) =>
            throw new NotSupportedException(_dependency.ToString());
    }

    [Fact]
    public void Omitted_Model_names_the_component_and_the_parameter()
    {
        var exception = Assert.ThrowsAny<Exception>(() => Render(builder =>
        {
            builder.OpenComponent<FormidableForm<EngineOrder>>(0);
            builder.CloseComponent();
        }));

        Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains("FormidableForm", exception.Message);
        Assert.Contains("Model", exception.Message);
    }

    // The three components below have no value binding, so For is the only way they can learn
    // which field they speak for — an omitted one has to say so by name rather than surface as the
    // expression helper's own bare ArgumentNullException.
    [Fact]
    public void Omitted_For_on_a_field_names_the_component_and_the_parameter()
    {
        var exception = AssertThrowsInsideAForm(inner =>
        {
            inner.OpenComponent<FormidableField<string?>>(0);
            inner.AddComponentParameter(1, "ChildContent", (RenderFragment<FormidableFieldContext>)(_ => _ => { }));
            inner.CloseComponent();
        });

        Assert.Contains("FormidableField", exception.Message);
        Assert.Contains("For=", exception.Message);
    }

    [Fact]
    public void Omitted_For_on_an_anchor_names_the_component_and_the_parameter()
    {
        var exception = AssertThrowsInsideAForm(inner =>
        {
            inner.OpenComponent<FormidableFieldAnchor<string?>>(0);
            inner.CloseComponent();
        });

        Assert.Contains("FormidableFieldAnchor", exception.Message);
        Assert.Contains("For=", exception.Message);
    }

    [Fact]
    public void Omitted_For_on_a_message_list_names_the_component_and_the_parameter()
    {
        var exception = AssertThrowsInsideAForm(inner =>
        {
            inner.OpenComponent<FormidableFieldMessage<string?>>(0);
            inner.CloseComponent();
        });

        Assert.Contains("FormidableFieldMessage", exception.Message);
        Assert.Contains("For=", exception.Message);
    }

    /// <summary>
    /// Renders <paramref name="child"/> inside a fully-wired form — this class's own container
    /// deliberately leaves the validator seam empty, and these three tests are about a
    /// component's own parameters rather than the form's wiring.
    /// </summary>
    private static InvalidOperationException AssertThrowsInsideAForm(RenderFragment child)
    {
        using var context = new BunitContext();
        context.Services.AddFormidable();
        context.Services.AddSingleton<FluentValidation.IValidator<EngineOrder>, EngineOrderValidator>();

        return Assert.IsType<InvalidOperationException>(Assert.ThrowsAny<Exception>(() => context.Render(builder =>
        {
            builder.OpenComponent<FormidableForm<EngineOrder>>(0);
            builder.AddComponentParameter(1, "Model", new EngineOrder());
            builder.AddComponentParameter(2, "ChildContent", (RenderFragment<FormidableFormContext>)(_ => child));
            builder.CloseComponent();
        })));
    }

    // The default Blazor Web App template renders its pages statically, and a static submit never
    // reaches the pipeline: it posts back, and the platform answers with a 400 telling the reader
    // to add a FormName parameter to EditForm — advice no FormidableForm parameter can follow. The
    // guard turns that dead end into the one instruction that does work.
    [Fact]
    public void Static_rendering_names_the_render_mode_the_page_is_missing()
    {
        var exception = Assert.IsType<InvalidOperationException>(
            Assert.ThrowsAny<Exception>(() => RenderFormOn(new RendererInfo("Static", isInteractive: false))));

        Assert.Contains("FormidableForm", exception.Message);
        Assert.Contains("@rendermode", exception.Message);
        Assert.Contains("InteractiveServer", exception.Message);
        Assert.Contains("InteractiveWebAssembly", exception.Message);
    }

    // The other half of the same signal, and the one that decides whether the guard is usable at
    // all: an interactive component is PRERENDERED by a static renderer before its circuit or
    // runtime picks it up. That pass reports exactly the same non-interactive renderer as the dead
    // end above, so only the assigned render mode tells the two apart — interactivity is coming.
    [Fact]
    public void A_prerendered_interactive_component_is_left_alone()
    {
        using var context = WiredContext();
        context.SetRendererInfo(new RendererInfo("Static", isInteractive: false));

        context.Render<FormidableForm<EngineOrder>>(parameters => parameters
            .Add(p => p.Model, new EngineOrder())
            .SetAssignedRenderMode(Microsoft.AspNetCore.Components.Web.RenderMode.InteractiveServer));
    }

    [Fact]
    public void An_interactive_renderer_is_left_alone()
    {
        RenderFormOn(new RendererInfo("WebAssembly", isInteractive: true));
    }

    // A renderer that declines to describe itself has said nothing, and nothing is not proof of a
    // dead end. bUnit's is one: it throws on RendererInfo unless a test declares one, so a guard
    // that read it unconditionally would fail every component test a consumer writes about their
    // own form — trading the platform's unfollowable 400 for an unfollowable test failure.
    [Fact]
    public void A_renderer_that_does_not_describe_itself_is_left_alone()
    {
        RenderFormOn(rendererInfo: null);
    }

    private static void RenderFormOn(RendererInfo? rendererInfo)
    {
        using var context = WiredContext();
        if (rendererInfo is { } info)
        {
            context.SetRendererInfo(info);
        }

        context.Render(builder =>
        {
            builder.OpenComponent<FormidableForm<EngineOrder>>(0);
            builder.AddComponentParameter(1, "Model", new EngineOrder());
            builder.CloseComponent();
        });
    }

    /// <summary>
    /// A context wired well enough that the render-mode guard is the only thing left that can
    /// throw — this class's own container deliberately leaves the validator seam empty.
    /// </summary>
    private static BunitContext WiredContext()
    {
        var context = new BunitContext();
        context.Services.AddFormidable();
        context.Services.AddSingleton<FluentValidation.IValidator<EngineOrder>, EngineOrderValidator>();
        return context;
    }

    private IRenderedComponent<ContainerFragment> RenderFormFor(EngineOrder order) => Render(builder =>
    {
        builder.OpenComponent<FormidableForm<EngineOrder>>(0);
        builder.AddComponentParameter(1, "Model", order);
        builder.CloseComponent();
    });

    private IRenderedComponent<ContainerFragment> RenderValidatorFor(EngineOrder order) => Render(builder =>
    {
        builder.OpenComponent<EditForm>(0);
        builder.AddComponentParameter(1, nameof(EditForm.Model), order);
        builder.AddComponentParameter(2, nameof(EditForm.ChildContent), (RenderFragment<EditContext>)(_ => inner =>
        {
            inner.OpenComponent<FormidableValidator<EngineOrder>>(0);
            inner.CloseComponent();
        }));
        builder.CloseComponent();
    });
}
