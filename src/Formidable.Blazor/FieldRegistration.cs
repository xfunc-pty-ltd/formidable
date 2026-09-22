using Microsoft.AspNetCore.Components.Forms;

namespace Formidable.Blazor;

/// <summary>The handle <see cref="FieldRegistry.Register"/> returns; disposing it ends that registration.</summary>
public sealed class FieldRegistration : IDisposable
{
    private readonly FieldRegistry _registry;
    private readonly FieldIdentifier _field;
    private readonly bool _keepRegistered;
    private bool _disposed;

    internal FieldRegistration(FieldRegistry registry, FieldIdentifier field, bool keepRegistered)
    {
        _registry = registry;
        _field = field;
        _keepRegistered = keepRegistered;
    }

    /// <summary>Ends this registration; a second call does nothing.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _registry.Unregister(_field, _keepRegistered);
    }
}
