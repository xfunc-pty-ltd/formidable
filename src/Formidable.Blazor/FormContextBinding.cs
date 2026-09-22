namespace Formidable.Blazor;

/// <summary>
/// Owns a component's binding to the cascaded <see cref="FormidableFormContext"/>: the
/// field registration and/or <c>Engine.StateChanged</c> subscription that must be released
/// and re-established whenever the host rebuilds its engine and registry (model swap).
/// Without the rebind, a surviving component keeps a dead subscription to the disposed old
/// engine and an orphaned registration in the old registry — it stops updating and its
/// submit errors are suppressed as unrevealed.
/// </summary>
internal sealed class FormContextBinding : IDisposable
{
    private FormidableFormContext? _context;
    private FieldRegistration? _registration;
    private EventHandler<FormidableStateChangedEventArgs>? _stateChangedHandler;

    /// <summary>The currently bound context, or null before the first <see cref="Update"/>.</summary>
    public FormidableFormContext? Context => _context;

    /// <summary>
    /// True when <paramref name="context"/> is non-null and already the bound instance — the
    /// same no-op condition <see cref="Update"/> applies internally, exposed so a caller can
    /// skip building the <c>register</c>/<c>stateChanged</c> delegates <see cref="Update"/>
    /// would otherwise discard unused on every steady-state render.
    /// </summary>
    public bool IsBound(FormidableFormContext? context) => context is not null && ReferenceEquals(context, _context);

    /// <summary>
    /// Ensures the binding targets <paramref name="context"/>. Throws when no context is
    /// cascaded; no-ops when the instance is unchanged; otherwise releases the previous
    /// registration and subscription, then invokes <paramref name="register"/> (when given)
    /// and subscribes <paramref name="stateChanged"/> (when given) against the new context.
    /// </summary>
    public void Update(
        FormidableFormContext? context,
        Type componentType,
        Func<FormidableFormContext, FieldRegistration?>? register,
        EventHandler<FormidableStateChangedEventArgs>? stateChanged)
    {
        if (context is null)
        {
            throw new InvalidOperationException(
                $"{FriendlyTypeName.Of(componentType)} must be placed inside a FormidableForm or FormidableValidator (no cascading FormidableFormContext found).");
        }

        if (ReferenceEquals(context, _context))
        {
            return;
        }

        Release();
        _registration = register?.Invoke(context);

        if (stateChanged is not null)
        {
            _stateChangedHandler = stateChanged;
            context.Engine.StateChanged += stateChanged;
        }

        _context = context;
    }

    private void Release()
    {
        if (_context is not null && _stateChangedHandler is not null)
        {
            _context.Engine.StateChanged -= _stateChangedHandler;
        }

        _stateChangedHandler = null;
        _registration?.Dispose();
        _registration = null;
        _context = null;
    }

    /// <inheritdoc />
    public void Dispose() => Release();
}
