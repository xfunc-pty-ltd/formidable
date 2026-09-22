using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;

namespace Formidable.Blazor;

/// <summary>
/// Shared lifecycle for the kit's context-bound components: it holds the cascaded
/// <see cref="FormidableFormContext"/>, binds to it when parameters are set — registering whatever
/// the component speaks for with the form's <see cref="FieldRegistry"/> and subscribing to the
/// engine's <see cref="IFormidableEngine.StateChanged"/> — rebinds when a host such as
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
/// The supported ways to bring a control of your own to the engine are unchanged by its
/// existence: <see cref="FormidableInputBase{TValue}"/> for a validated control, and
/// <see cref="FormidableField{TValue}"/> for markup Formidable does not wrap. Registering a
/// field is a narrower job than either, and has routes of its own —
/// <see cref="FormidableFieldAnchor{TValue}"/>, or
/// <see cref="FieldRegistry.Register(Microsoft.AspNetCore.Components.Forms.FieldIdentifier, bool)"/>
/// called directly, whose remarks say what a caller then owns.
/// </remarks>
public abstract class FormidableComponentBase : ComponentBase, IDisposable
{
    private readonly FormContextBinding _binding = new();
    private FieldIdentifier _registeredField;
    private bool _verifyRowKeys;

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
    /// <see cref="IFormidableEngine.StateChanged"/> — true for anything that renders a
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
    /// The no-op path carries one extra job while
    /// <see cref="FormidableOptions.VerifyRowKeys"/> is on: it checks that the field this
    /// component's accessor names is still the one it registered.
    /// </summary>
    protected override void OnParametersSet()
    {
        if (_binding.IsBound(Context))
        {
            VerifyRowKey();
            return;
        }

        _binding.Update(
            Context,
            GetType(),
            register: Register,
            stateChanged: ObservesEngineState ? OnEngineStateChanged : null);

        _verifyRowKeys = Context!.Engine.Options.VerifyRowKeys;
        _registeredField = _verifyRowKeys ? ResolveField() : default;
    }

    /// <summary>
    /// The field this component's accessor names <em>right now</em>, resolved afresh rather than
    /// read back from whatever <see cref="Register"/> resolved. Every component that speaks for a
    /// field overrides this and calls it from its own <see cref="Register"/>, so the identifier a
    /// component registers and the identifier <see cref="FormidableOptions.VerifyRowKeys"/>
    /// compares against it are the same expression evaluated at two different times — which is the
    /// only thing that comparison is entitled to assume. The default is the empty identifier, for a
    /// component with no accessor to resolve: <c>FormidableSummary</c>, which speaks for the whole
    /// form, and <c>FormidableModelMessage</c>, whose field is the model-level one no accessor
    /// expression can name. The empty identifier matches itself on every render, so a component
    /// that keeps the default is never a candidate.
    /// </summary>
    private protected virtual FieldIdentifier ResolveField() => default;

    /// <summary>
    /// Throws when the component's accessor now names a different field than the one it registered
    /// — see <see cref="FormidableOptions.VerifyRowKeys"/> for what that means and why it is worth
    /// stopping on. Silent unless that option is on.
    /// </summary>
    private void VerifyRowKey()
    {
        if (!_verifyRowKeys)
        {
            return;
        }

        var current = ResolveField();
        if (current.Equals(_registeredField))
        {
            return;
        }

        throw new InvalidOperationException(
            $"{FriendlyTypeName.Of(GetType())} {DescribeChange(_registeredField, current)}, " +
            "without having been rebuilt in between. A field is the object owning the " +
            "value plus a member name, so this happens whenever something replaces that " +
            "object without rebuilding the components bound to it. A row list rendered " +
            "without @key is the common way to do it: remove or reorder a row, and Blazor " +
            "reuses each row's components for the next item along — the field " +
            "registration, the element id, the aria attributes and the messages stay with " +
            "the row that moved away, while the input shows the new row's value. Key each " +
            "row by the row object — @key=\"item\" on the element the loop renders " +
            "— so a row's components travel with it. If the list is already keyed " +
            "that way, or there is no list to key at all, something else re-pointed the " +
            "accessor: a key taken from the row's id while the row object itself was " +
            "replaced, or a For that now names another field. " +
            $"(Reported by {nameof(FormidableOptions)}." +
            $"{nameof(FormidableOptions.VerifyRowKeys)}.)");
    }

    /// <summary>
    /// Names both ends of the divergence: the same field on a different owner — the row case, where
    /// spelling the identical name twice would say nothing — or two different fields outright.
    /// </summary>
    private static string DescribeChange(FieldIdentifier registered, FieldIdentifier current)
    {
        var registeredOwner = OwnerName(registered);
        var currentOwner = OwnerName(current);

        return string.Equals(registered.FieldName, current.FieldName, StringComparison.Ordinal)
            && string.Equals(registeredOwner, currentOwner, StringComparison.Ordinal)
                ? $"registered the field '{registeredOwner}.{registered.FieldName}' and is now bound to the same field on a different {currentOwner}"
                : $"registered the field '{registeredOwner}.{registered.FieldName}' and is now bound to '{currentOwner}.{current.FieldName}'";

        static string OwnerName(FieldIdentifier field) =>
            field.Model is { } owner ? FriendlyTypeName.Of(owner.GetType()) : "nothing";
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
    /// Called when the engine raises <see cref="IFormidableEngine.StateChanged"/> — a
    /// validation pass landing, a refresh, a server-applied issue — and re-renders the
    /// component on the renderer's synchronization context. An override that still wants
    /// the re-render must call base. It has the event's own handler shape, so an override
    /// reads whatever <see cref="FormidableStateChangedEventArgs"/> carries.
    /// </summary>
    /// <param name="sender">The engine that raised the event.</param>
    /// <param name="e">The event's arguments.</param>
    protected virtual void OnEngineStateChanged(object? sender, FormidableStateChangedEventArgs e) =>
        _ = InvokeAsync(StateHasChanged);

    /// <summary>
    /// Runs <see cref="DisposeCore"/>, then releases the field registration and the engine
    /// subscription. Deliberately not virtual: the base's cleanup is not a derived control's to
    /// forget, so a removed field cannot stay registered because someone missed a base call. The
    /// release runs in a <see langword="finally"/>, so a throwing <see cref="DisposeCore"/> no
    /// longer skips it.
    /// </summary>
    public void Dispose()
    {
        try
        {
            DisposeCore();
        }
        finally
        {
            _binding.Dispose();
        }
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
