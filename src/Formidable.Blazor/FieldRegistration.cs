using Microsoft.AspNetCore.Components.Forms;

namespace Formidable.Blazor;

/// <summary>Handle for a <see cref="FieldRegistry"/> registration; dispose to unregister.</summary>
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

    /// <inheritdoc />
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
