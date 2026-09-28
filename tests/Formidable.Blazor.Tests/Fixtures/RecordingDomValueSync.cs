namespace Formidable.Blazor.Tests.Fixtures;

/// <summary>
/// Stands in for the service that writes a field's value back to its element through
/// JavaScript. The kit's number and date inputs call it when they lose focus, and this double
/// records each call.
/// </summary>
public sealed class RecordingDomValueSync : IFormidableDomValueSync
{
    /// <summary>Each call's element id and value, one entry per call, in order.</summary>
    public List<(string ElementId, string? Value)> Calls { get; } = [];

    /// <summary>Runs inside each call, so a test can see its order among other handlers.</summary>
    public Action? OnSync { get; set; }

    public ValueTask SyncValueAsync(string elementId, string? value)
    {
        Calls.Add((elementId, value));
        OnSync?.Invoke();
        return ValueTask.CompletedTask;
    }
}
