using Formidable.Blazor.Tests.Fixtures;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Formidable.Blazor.Tests;

/// <summary>
/// A root that replaces its engine lets go of the old one before it disposes it. Disposing
/// cancels the old engine's check, and a rule waiting with <c>WaitAsync(ct)</c> resumes inside
/// that cancel; on a WebAssembly host, with no synchronization context, the abandoned submit's
/// continuation then runs inside the disposal and must find its engine gone. A reset whose
/// disposal throws (a rule's cancellation callback can make it) must not leave the form on the
/// disposed engine. Every root here renders on <see cref="InlineRenderer"/>, and every call into
/// one is made with no synchronization context current, as on that host.
/// </summary>
public class RootRebuildOrderTests
{
    private readonly RecordingFocusService _focus = new();

    private ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddFormidable();
        services.AddSingleton<IFormidableFocusService>(_focus);
        services.AddSingleton<TimeProvider>(new FakeTimeProvider());
        return services.BuildServiceProvider();
    }

    /// <summary>Makes <paramref name="call"/> with no synchronization context, as a WebAssembly host does, and puts the caller's context back before anything awaits.</summary>
    private static T WithoutContext<T>(Func<T> call)
    {
        var ambient = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            return call();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(ambient);
        }
    }

    /// <inheritdoc cref="WithoutContext{T}(Func{T})"/>
    private static void WithoutContext(Action call) =>
        WithoutContext(() =>
        {
            call();
            return true;
        });

    private Dictionary<string, object?> FormParameters(
        EngineOrder order, IModelValidator<EngineOrder> validator, Action onInvalid, Action onValid) => new()
    {
        [nameof(FormidableForm<EngineOrder>.Model)] = order,
        [nameof(FormidableForm<EngineOrder>.Validator)] = validator,
        [nameof(FormidableForm<EngineOrder>.Options)] = new FormidableOptions { DisclosureOverride = _ => true },
        [nameof(FormidableForm<EngineOrder>.OnInvalidSubmit)] =
            EventCallback.Factory.Create<FormidableInvalidSubmitContext>(this, onInvalid),
        [nameof(FormidableForm<EngineOrder>.OnValidSubmit)] = EventCallback.Factory.Create<SubmitOutcome>(this, onValid),
    };

    // A first submit blocks on the rule's error, which shows, and moves focus to it. A second
    // submit waits on the rule when the page resets the form. Disposing the old engine ends that
    // submit inside the reset, and the submit finds the engine replaced: neither callback fires,
    // focus stays, and it answers the empty blocked outcome.
    // Mutation: dispose the old engine before the form points at the new one, and the abandoned
    // submit fires OnInvalidSubmit and moves focus to the old engine's error, inside the reset.
    [Fact]
    public async Task A_submit_a_reset_abandons_fires_no_callback_and_moves_no_focus()
    {
        await using var services = BuildServices();
        using var renderer = new InlineRenderer(services);
        var validator = new GatedValidator();
        validator.Gate.SetResult();
        var invalid = 0;
        var valid = 0;
        var form = await WithoutContext(() => renderer.RenderAsync<FormidableForm<EngineOrder>>(FormParameters(
            new EngineOrder(), new FluentValidationModelValidator<EngineOrder>(validator), () => invalid++, () => valid++)));

        Assert.False((await WithoutContext(form.SubmitAsync)).CanProceed);
        Assert.Equal(1, invalid);
        Assert.Single(_focus.Requests);

        validator.Reset();
        var abandoned = WithoutContext(form.SubmitAsync); // waits on the rule
        Assert.Equal(2, validator.Started);

        await WithoutContext(() => form.ResetAsync());

        Assert.True(abandoned.IsCompleted); // it ended inside the reset
        Assert.Equal(1, invalid);
        Assert.Equal(0, valid);
        Assert.Single(_focus.Requests);
        Assert.Same(RootSubmit.Superseded, await abandoned);
        Assert.Empty(renderer.Reported);
    }

    // The attach-mode counterpart: a new cascaded EditContext rebuilds the engine, and a submit
    // waiting on the old engine's rule ends inside that rebuild. It finds the engine replaced:
    // focus stays and it answers the empty blocked outcome.
    // Mutation: dispose the old engine before the component lets go of it, and the abandoned
    // submit moves focus to the old engine's error and answers that engine's own blocked outcome.
    [Fact]
    public async Task A_submit_an_EditContext_swap_abandons_moves_no_focus()
    {
        await using var services = BuildServices();
        using var renderer = new InlineRenderer(services);
        var validator = new GatedValidator();
        validator.Gate.SetResult();
        var host = await WithoutContext(() => renderer.RenderAsync<SwappableContextHost>(new Dictionary<string, object?>
        {
            [nameof(SwappableContextHost.Validator)] = new FluentValidationModelValidator<EngineOrder>(validator),
        }));
        var attached = host.Attached!;

        Assert.False((await WithoutContext(attached.ValidateForSubmitAsync)).CanProceed);
        Assert.Single(_focus.Requests);

        validator.Reset();
        var abandoned = WithoutContext(attached.ValidateForSubmitAsync); // waits on the rule
        Assert.Equal(2, validator.Started);

        WithoutContext(() => host.Swap(new EngineOrder()));

        Assert.True(abandoned.IsCompleted); // it ended inside the rebuild
        Assert.Single(_focus.Requests);
        Assert.Same(RootSubmit.Superseded, await abandoned);
        Assert.Empty(renderer.Reported);
    }

    // A submit leaves the rule's throwing callback on the engine's token, so disposing that engine
    // throws. A reset still leaves the form on a new engine, rendered for the page to use, and a
    // reset to a new model still tells the binding. The disposal's throw reaches the page, and a
    // later submit and load run on the new engine.
    // Mutation: dispose the old engine first, and the throw leaves the form on the disposed
    // engine, where the later submit throws ObjectDisposedException. Render and tell the binding
    // only after a disposal that returns, and the reset leaves the page drawing the old engine
    // and, for a new model, the binding holding the old one.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_reset_whose_old_engine_throws_as_it_is_disposed_leaves_the_form_on_the_new_engine(bool newModel)
    {
        await using var services = BuildServices();
        using var renderer = new InlineRenderer(services);
        var validator = new CancelCallbackThrowingValidator { Throws = true };
        var order = new EngineOrder();
        var valid = 0;
        EngineOrder? bound = null;
        FormidableFormContext? drawn = null;
        var parameters = FormParameters(order, new FluentValidationModelValidator<EngineOrder>(validator), () => { }, () => valid++);
        parameters[nameof(FormidableForm<EngineOrder>.ModelChanged)] = EventCallback.Factory.Create<EngineOrder>(this, model => bound = model);
        parameters[nameof(FormidableForm<EngineOrder>.ChildContent)] = (RenderFragment<FormidableFormContext>)(context =>
        {
            drawn = context;
            return _ => { };
        });
        var form = await WithoutContext(() => renderer.RenderAsync<FormidableForm<EngineOrder>>(parameters));

        Assert.False((await WithoutContext(form.SubmitAsync)).CanProceed); // leaves the callback on its token
        validator.Throws = false;
        var old = form.Engine;
        var next = newModel ? new EngineOrder() : order;

        var thrown = await Assert.ThrowsAsync<AggregateException>(
            () => WithoutContext(() => newModel ? form.ResetAsync(next) : form.ResetAsync()));

        Assert.IsType<InvalidOperationException>(Assert.Single(thrown.InnerExceptions));
        Assert.NotSame(old, form.Engine);
        Assert.Same(form.Engine, drawn!.Engine);
        Assert.Same(newModel ? next : null, bound);

        next.Description = "ok";
        Assert.True((await WithoutContext(form.SubmitAsync)).CanProceed);
        Assert.Equal(1, valid);
        await WithoutContext(() => form.DiscloseLoadedValuesAsync());
        Assert.Empty(renderer.Reported);
    }

    /// <summary>An <c>EditForm</c> stand-in: it cascades an <see cref="EditContext"/> to a <see cref="FormidableValidator{TModel}"/> and cascades a new one when <see cref="Swap"/> hands it another model.</summary>
    private sealed class SwappableContextHost : ComponentBase
    {
        // One instance for the life of the host: the validator rejects a different Options
        // instance on a render that does not also rebuild its engine.
        private readonly FormidableOptions _options = new() { DisclosureOverride = _ => true };
        private EditContext _editContext = new(new EngineOrder());

        [Parameter]
        public IModelValidator<EngineOrder>? Validator { get; set; }

        /// <summary>The validator this host renders, once it has rendered.</summary>
        public FormidableValidator<EngineOrder>? Attached { get; private set; }

        /// <summary>Cascades a new <see cref="EditContext"/> over <paramref name="model"/> and renders.</summary>
        public void Swap(EngineOrder model)
        {
            _editContext = new EditContext(model);
            StateHasChanged();
        }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<CascadingValue<EditContext>>(0);
            builder.AddComponentParameter(1, "Value", _editContext);
            builder.AddComponentParameter(2, "ChildContent", (RenderFragment)(inner =>
            {
                inner.OpenComponent<FormidableValidator<EngineOrder>>(0);
                inner.AddComponentParameter(1, nameof(FormidableValidator<EngineOrder>.Options), _options);
                if (Validator is not null)
                {
                    inner.AddComponentParameter(2, nameof(FormidableValidator<EngineOrder>.Validator), Validator);
                }

                inner.AddComponentReferenceCapture(3, reference => Attached = (FormidableValidator<EngineOrder>)reference);
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        }
    }
}
