using Microsoft.AspNetCore.Components;

namespace Formidable.Blazor;

/// <summary>
/// Shared lifecycle for the kit's context-bound components: it holds the cascaded
/// <see cref="FormidableFormContext"/>, binds to it when parameters are set — registering whatever
/// the component speaks for with the form's <see cref="FieldRegistry"/> and subscribing to the
/// engine's <see cref="IFormValidationEngine.StateChanged"/> — rebinds when a host such as
/// <c>FormidableForm</c>/<c>FormidableValidator</c> swaps its model and rebuilds its engine and
/// registry, and releases both on disposal. That rebind is the reason this lives in one place:
/// without it a surviving component keeps a dead subscription to the disposed engine and an
/// orphaned registration in the old registry, so it stops updating and its submit errors are
/// suppressed as unrevealed — one safety property with one implementation, rather than a copy per
/// component free to drift from the others.
/// </summary>
/// <remarks>
/// Public only because a public component cannot inherit a less accessible base; its constructor
/// is not accessible outside this assembly, so the components below it are the ones the kit ships.
/// The supported extension points are unchanged by its existence:
/// <see cref="FormidableInputBase{TValue}"/> for a validated control of your own, and
/// <see cref="FormidableField{TValue}"/> for markup Formidable does not wrap.
/// </remarks>
public abstract class FormidableComponentBase : ComponentBase, IDisposable
{
    private readonly FormContextBinding _binding = new();

    private protected FormidableComponentBase()
    {
    }

    /// <summary>
    /// The cascaded form context, and a component's route to the engine, the <c>EditContext</c>
    /// and the field registry. Supplied by a <c>FormidableForm</c>/<c>FormidableValidator</c>
    /// ancestor: it is null until parameters are first set, and a component rendered outside such
    /// an ancestor throws from <see cref="OnParametersSet"/> with a message naming itself.
    /// </summary>
    [CascadingParameter]
    protected FormidableFormContext? Context { get; private set; }

    /// <summary>
    /// Whether the component re-renders when the engine raises
    /// <see cref="IFormValidationEngine.StateChanged"/> — true for anything that renders a
    /// verdict, and so the default. A component that renders no markup of its own has nothing to
    /// re-render: overriding this to false leaves it unsubscribed altogether rather than
    /// subscribing a handler with no work to do. Turning it off in a component that does render a
    /// verdict is what it sounds like — the state class, the aria attributes and any messages it
    /// renders stop following the field, updating only when something else happens to re-render it.
    /// </summary>
    protected virtual bool ObservesEngineState => true;

    /// <summary>
    /// Binds the component to the currently-cascaded context: it no-ops while the context instance
    /// is unchanged, and otherwise releases the previous binding before calling
    /// <see cref="Register"/> and re-subscribing against the new one. A derived component that
    /// overrides this must call <c>base.OnParametersSet()</c>, or it registers nothing and never
    /// re-renders on a validation state change.
    /// </summary>
    protected override void OnParametersSet()
    {
        if (_binding.IsBound(Context))
        {
            return;
        }

        _binding.Update(
            Context,
            GetType(),
            register: Register,
            stateChanged: ObservesEngineState ? OnEngineStateChanged : null);
    }

    /// <summary>
    /// Registers whatever this component speaks for with <paramref name="context"/>'s field
    /// registry, returning the registration for the binding to release — or null when the
    /// component registers nothing. Called only when the binding targets a new context instance,
    /// which is the first render and every rebind, and therefore also where a component resolves
    /// the field its <c>For</c> names: a rebind is exactly when that resolution can have changed.
    /// </summary>
    /// <param name="context">The context now being bound.</param>
    /// <returns>The registration to release on the next rebind or on disposal, or null.</returns>
    protected abstract FieldRegistration? Register(FormidableFormContext context);

    /// <summary>
    /// Called when the engine raises <see cref="IFormValidationEngine.StateChanged"/> — a
    /// validation pass landing, a post-submit refresh, a server-applied issue — and re-renders the
    /// component on the renderer's synchronization context. An override that still wants the
    /// re-render must call base.
    /// </summary>
    protected virtual void OnEngineStateChanged() => _ = InvokeAsync(StateHasChanged);

    /// <summary>
    /// Runs <see cref="DisposeCore"/>, then releases the field registration and the engine
    /// subscription. Deliberately not virtual: the base's cleanup is not a derived control's to
    /// forget, so a removed field cannot stay revealed because someone missed a base call.
    /// </summary>
    public void Dispose()
    {
        DisposeCore();
        _binding.Dispose();
    }

    /// <summary>
    /// Releases resources a derived control owns — a JS module, a timer, a subscription. Called by
    /// <see cref="Dispose"/> before the base releases the field registration and engine
    /// subscription, and doing nothing by default. A derived control implementing
    /// <see cref="IAsyncDisposable"/> owns the whole disposal path instead, because a component
    /// implementing both interfaces has only its async overload called: such a control must invoke
    /// <see cref="Dispose"/> from its <c>DisposeAsync</c>, or the registration is never released.
    /// </summary>
    protected virtual void DisposeCore()
    {
    }
}
