using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;

namespace Formidable.Blazor;

/// <summary>The base of the kit's components other than the two roots: it binds to the cascaded <see cref="FormidableFormContext"/>, holds whatever <see cref="Register"/> registers, and re-renders on <see cref="IFormidableEngine.StateChanged"/> while <see cref="ObservesEngineState"/> is <see langword="true"/>.</summary>
/// <remarks>
/// Not an extension point (the constructor is not accessible outside the assembly): derive
/// from <see cref="FormidableInputBase{TValue}"/> for a validated control, or put markup of your
/// own inside <see cref="FormidableField{TValue}"/>.
/// </remarks>
public abstract class FormidableComponentBase : ComponentBase, IDisposable
{
    // The binding lives here, in one place, because of the rebind: a host that swaps its model
    // rebuilds its engine and registry, and a surviving component that kept its old binding would
    // hold a dead subscription to the disposed engine and an orphaned registration in the old
    // registry, so it would stop updating and its submit errors would be suppressed as
    // unrevealed. One safety property with one implementation, rather than a copy per component
    // free to drift from the others.
    private readonly FormContextBinding _binding = new();
    private FieldIdentifier _registeredField;
    private bool _verifyRowKeys;
    private bool _reportStaleRegistrations;
    private bool _staleReported;

    private protected FormidableComponentBase()
    {
    }

    /// <summary>The context the enclosing <see cref="FormidableForm{TModel}"/> or <see cref="FormidableValidator{TModel}"/> cascades, and the route to the engine, the <c>EditContext</c> and the registry; <see langword="null"/> until parameters are first set.</summary>
    [CascadingParameter]
    protected FormidableFormContext? Context { get; private set; }

    /// <summary>Whether the component subscribes to <see cref="IFormidableEngine.StateChanged"/> and re-renders on it; <see langword="false"/> subscribes to nothing. Defaults to <see langword="true"/>.</summary>
    protected virtual bool ObservesEngineState => true;

    /// <summary>Whether the component's registration holds its field's live messages until the form's first submit or server reply answers; <see langword="false"/> unless a component that waits overrides it.</summary>
    // Passed to RegisterWithHold by the components that wait, and read again at every later
    // parameter set, so a mounted component's hold follows its parameter at its next render.
    private protected virtual bool HoldsLiveMessages => false;

    /// <summary>Binds the component to the cascaded context when that is a new instance; otherwise applies a change of <c>WaitForSubmit</c> where the component has one, and runs the row-key check <see cref="FormidableOptions.VerifyRowKeys"/> or <see cref="FormidableOptions.ReportStaleRegistrations"/> asks for.</summary>
    /// <exception cref="InvalidOperationException">No <see cref="FormidableFormContext"/> is cascaded (the component stands outside a root), or, under <see cref="FormidableOptions.VerifyRowKeys"/>, the accessor names a different field than the component registered.</exception>
    /// <remarks>
    /// An override must call <c>base.OnParametersSet()</c>, or the component registers nothing
    /// and never re-renders on a state change.
    /// </remarks>
    protected override void OnParametersSet()
    {
        if (_binding.IsBound(Context))
        {
            // The registration stands, so a change of the wait moves only its hold, at the render
            // that carries the new parameter, and the field never leaves the page for it.
            _binding.ChangeHold(HoldsLiveMessages);
            VerifyRowKey();
            return;
        }

        _binding.Update(
            Context,
            GetType(),
            register: Register,
            stateChanged: ObservesEngineState ? OnEngineStateChanged : null);

        var options = Context!.Engine.Options;
        _verifyRowKeys = options.VerifyRowKeys;
        _reportStaleRegistrations = options.ReportStaleRegistrations;
        _registeredField = _verifyRowKeys || _reportStaleRegistrations ? ResolveField() : default;
        _staleReported = false;
    }

    /// <summary>The field the component's accessor names at this moment, resolved afresh; the empty identifier for a component with no accessor.</summary>
    /// <returns>The field the accessor currently resolves to, or the empty identifier, which matches itself on every render and so never counts as a divergence.</returns>
    // Every component that speaks for a field overrides this and calls it from its own Register,
    // so the identifier a component registers and the identifier the row-key check compares
    // against it are the same expression evaluated at two different times, which is the only
    // thing that comparison is entitled to assume. FormidableSummary and FormidableModelMessage
    // keep the default: the first speaks for the whole form, the second for the model-level field
    // no accessor expression can name.
    private protected virtual FieldIdentifier ResolveField() => default;

    /// <summary>Runs the row-key check in whichever mode is on: <see cref="FormidableOptions.VerifyRowKeys"/> throws on a divergence, <see cref="FormidableOptions.ReportStaleRegistrations"/> alone reports it, and with both on the throw wins.</summary>
    /// <exception cref="InvalidOperationException">Under <see cref="FormidableOptions.VerifyRowKeys"/>, the accessor names a different field than the component registered.</exception>
    private void VerifyRowKey()
    {
        if (!_verifyRowKeys)
        {
            if (_reportStaleRegistrations)
            {
                ReportStaleRegistration();
            }

            return;
        }

        var current = ResolveField();
        if (current.Equals(_registeredField))
        {
            return;
        }

        var fix = DescribeIndexedRowFix(_registeredField, current) ??
            "A field is the object owning the " +
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
            "replaced, or a For that now names another field.";

        throw new InvalidOperationException(
            $"{FriendlyTypeName.Of(GetType())} {DescribeChange(_registeredField, current)}, " +
            $"without having been rebuilt in between. {fix} " +
            $"(Reported by {nameof(FormidableOptions)}." +
            $"{nameof(FormidableOptions.VerifyRowKeys)}.)");
    }

    /// <summary>Reports a divergence between the registered field and the field the accessor currently names, once until it heals, through <see cref="IStaleRegistrationReporter"/> where the engine implements it; an accessor that throws is skipped.</summary>
    private void ReportStaleRegistration()
    {
        FieldIdentifier current;
        try
        {
            current = ResolveField();
        }
        catch (Exception)
        {
            // A shape the check cannot answer for must render exactly as it would with no check
            // at all, because reporting is this mode's whole severity.
            return;
        }

        if (current.Equals(_registeredField))
        {
            // The latch resets here and on rebind, so a divergence that heals and then reopens is
            // a fresh finding.
            _staleReported = false;
            return;
        }

        if (_staleReported)
        {
            return;
        }

        _staleReported = true;
        (Context!.Engine as IStaleRegistrationReporter)?.Report(
            new StaleRegistrationReport(GetType(), _registeredField, current));
    }

    /// <summary>Describes a divergence: the same field on a different owner (the row case), or two different fields.</summary>
    /// <param name="registered">The field the component registered.</param>
    /// <param name="current">The field the accessor currently names.</param>
    /// <returns>The clause that follows the component's name in the throw and in the report.</returns>
    // Shared by the VerifyRowKey throw and the engine's stale-registration report, so the two
    // tellings of one divergence cannot drift apart.
    internal static string DescribeChange(FieldIdentifier registered, FieldIdentifier current)
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

    /// <summary>The fix a divergence calls for when both fields are rows bound by their index, such as strings in a list or an array: the same index on another list, or another index.</summary>
    /// <param name="registered">The field the component registered.</param>
    /// <param name="current">The field the accessor currently names.</param>
    /// <returns>The advice that follows the description of the change, or <see langword="null"/> when either field is not an element named by its index.</returns>
    // Shared by the VerifyRowKey throw and the engine's stale-registration report, as
    // DescribeChange is. A row bound by its index has no object of its own to key, so the advice
    // every other divergence gets (key each row by its row object) would send the reader to the
    // key that causes the second case.
    internal static string? DescribeIndexedRowFix(FieldIdentifier registered, FieldIdentifier current)
    {
        if (!ResolvedFieldExtensions.IsIndexedElement(registered) || !ResolvedFieldExtensions.IsIndexedElement(current))
        {
            return null;
        }

        return string.Equals(registered.FieldName, current.FieldName, StringComparison.Ordinal)
            ? "A row bound by its index, such as a string in a list or an array, is that list " +
              "plus the index, and the accessor now reaches another list: the page replaced the " +
              "list, or the row holding it. Key the element around the loop by the list, for " +
              "example @key=\"Model.Tags\" on the element around a loop over Model.Tags, so " +
              "that a new list rebuilds its rows."
            : "A row bound by its index, such as a string in a list or an array, is that list " +
              "plus the index, and this component now names another index. A key taken from the " +
              "row's value is the usual cause: it moves the row's components with the value on a " +
              "remove or a reorder, while the index stays the row's identity. Give such a row no " +
              "key of its own. Where the page replaces the list, key the element around the loop " +
              "by the list instead (for example @key=\"Model.Tags\").";
    }

    /// <summary>Called when the cascaded context is a new instance (the first render and every rebind): registers what the component speaks for with <paramref name="context"/>'s registry, or returns <see langword="null"/> to register nothing.</summary>
    /// <param name="context">The context being bound.</param>
    /// <returns>The handle the base releases on the next rebind or on disposal, or <see langword="null"/>.</returns>
    protected abstract FieldRegistration? Register(FormidableFormContext context);

    /// <summary>Re-renders the component when the engine raises <see cref="IFormidableEngine.StateChanged"/>; an override that still wants the re-render calls the base.</summary>
    /// <param name="sender">The engine that raised the event.</param>
    /// <param name="e">The event's arguments.</param>
    protected virtual void OnEngineStateChanged(object? sender, FormidableStateChangedEventArgs e) =>
        _ = InvokeAsync(StateHasChanged);

    /// <summary>Runs <see cref="DisposeCore"/>, then releases the field registration and the engine subscription, whether or not <see cref="DisposeCore"/> threw.</summary>
    // Deliberately not virtual: the base's cleanup is not a derived control's to forget, so a
    // removed field cannot stay registered because someone missed a base call.
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

    /// <summary>Releases what a derived control owns (a JS module, a timer, a subscription); called by <see cref="Dispose"/> before the base releases its registration and subscription. Does nothing by default.</summary>
    /// <remarks>
    /// A control that also implements <see cref="IAsyncDisposable"/> must call
    /// <see cref="Dispose"/> from its <c>DisposeAsync</c>: the renderer calls only the async
    /// overload of a component implementing both, so the registration is otherwise never released.
    /// </remarks>
    protected virtual void DisposeCore()
    {
    }
}
