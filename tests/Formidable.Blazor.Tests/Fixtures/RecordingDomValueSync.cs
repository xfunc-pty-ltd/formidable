namespace Formidable.Blazor.Tests.Fixtures;

/// <summary>
/// Records every <see cref="IFormidableDomValueSync"/> call, standing in for the JS-backed
/// service so input tests assert against the seam instead of configuring module interop.
/// </summary>
public sealed class RecordingDomValueSync : IFormidableDomValueSync
{
    public List<(string ElementId, string? Value)> Calls { get; } = [];

    /// <summary>Invoked on every sync, for ordering assertions against other handlers.</summary>
    public Action? OnSync { get; set; }

    public ValueTask SyncValueAsync(string elementId, string? value)
    {
        Calls.Add((elementId, value));
        OnSync?.Invoke();
        return ValueTask.CompletedTask;
    }
}
