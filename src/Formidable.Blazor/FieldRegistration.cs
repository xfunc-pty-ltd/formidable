using Microsoft.AspNetCore.Components.Forms;

namespace Formidable.Blazor;

/// <summary>The handle <see cref="FieldRegistry.Register"/> returns; disposing it ends that registration.</summary>
public sealed class FieldRegistration : IDisposable
{
    private readonly FieldRegistry _registry;
    private readonly FieldIdentifier _field;
    private readonly bool _keepRegistered;

    // Mutable because a component's wait can change while it stays on the page. Dispose passes the
    // hold as it stands then, so the registration counts toward a retention's hold only if it held
    // when it ended.
    private bool _holdsLiveMessages;
    private bool _disposed;

    internal FieldRegistration(FieldRegistry registry, FieldIdentifier field, bool keepRegistered, bool holdsLiveMessages)
    {
        _registry = registry;
        _field = field;
        _keepRegistered = keepRegistered;
        _holdsLiveMessages = holdsLiveMessages;
    }

    /// <summary>Changes whether this registration holds its field's live messages; does nothing once disposed, or when the hold is already <paramref name="holdsLiveMessages"/>.</summary>
    /// <param name="holdsLiveMessages">The hold the registration takes from now on.</param>
    internal void ChangeHold(bool holdsLiveMessages)
    {
        if (_disposed || _holdsLiveMessages == holdsLiveMessages)
        {
            return;
        }

        _holdsLiveMessages = holdsLiveMessages;
        _registry.ChangeHold(_field, holdsLiveMessages);
    }

    /// <summary>Ends this registration; a second call does nothing.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _registry.Unregister(_field, _keepRegistered, _holdsLiveMessages);
    }
}
