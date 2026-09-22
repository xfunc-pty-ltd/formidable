namespace Formidable.Blazor;

/// <summary>A component's registration and <c>StateChanged</c> subscription against the cascaded <see cref="FormidableFormContext"/>, re-established whenever that context is a new instance.</summary>
internal sealed class FormContextBinding : IDisposable
{
    private FormidableFormContext? _context;
    private FieldRegistration? _registration;
    private EventHandler<FormidableStateChangedEventArgs>? _stateChangedHandler;

    /// <summary>The bound context, or <see langword="null"/> before the first <see cref="Update"/>.</summary>
    public FormidableFormContext? Context => _context;

    /// <summary>Whether <paramref name="context"/> is non-null and already the bound instance.</summary>
    /// <param name="context">The cascaded context the component sees.</param>
    /// <returns><see langword="true"/> when <see cref="Update"/> would do nothing for it.</returns>
    // The same no-op condition Update applies, exposed so a caller can skip building the
    // delegates Update would otherwise discard unused on every steady-state render.
    public bool IsBound(FormidableFormContext? context) => context is not null && ReferenceEquals(context, _context);

    /// <summary>Binds to <paramref name="context"/>: does nothing for the bound instance, otherwise releases the old binding and registers and subscribes against the new.</summary>
    /// <param name="context">The cascaded context, or <see langword="null"/> when none is cascaded.</param>
    /// <param name="componentType">The component's type, named in the exception.</param>
    /// <param name="register">Registers the component's field with the context, or <see langword="null"/> to register nothing.</param>
    /// <param name="stateChanged">The handler to subscribe to the engine's <c>StateChanged</c>, or <see langword="null"/> to subscribe nothing.</param>
    /// <exception cref="InvalidOperationException"><paramref name="context"/> is <see langword="null"/>: the component sits inside no <c>FormidableForm</c> or <c>FormidableValidator</c>.</exception>
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
